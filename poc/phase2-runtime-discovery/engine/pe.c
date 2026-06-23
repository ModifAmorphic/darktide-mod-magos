/*
 * pe.c - Minimal in-memory PE parser.
 *
 * The engine consumes a flat buffer laid out by RVA (tool wrapper copies
 * each section to its VirtualAddress, mirroring what the Windows loader
 * does). This file:
 *   - validates DOS/PE signatures
 *   - locates the section table, finds .text/.rdata/.pdata
 *   - parses the .pdata RUNTIME_FUNCTION table into a sorted array
 *   - parses the import directory (for FF 25 import-thunk resolution)
 *
 * No pefile dependency. Constants from winnt.h are hard-coded so the
 * engine compiles cleanly under both native gcc and mingw without
 * pulling windows.h (which mingw does have, but native Linux does not).
 */
#include "engine.h"
#include "util.h"

/* ---- PE constants (subset of IMAGE_* from winnt.h) ---------------- */
#define DT_IMAGE_DOS_SIGNATURE     0x5A4D     /* "MZ"  */
#define DT_IMAGE_NT_SIGNATURE      0x00004550 /* "PE\0\0" */
#define DT_IMAGE_NUMBEROF_DIRECTORY_ENTRIES 16
#define DT_IMAGE_DIRECTORY_ENTRY_IMPORT     1

#pragma pack(push, 1)
typedef struct {
    uint16_t e_magic, e_cblp, e_cp, e_crlc, e_cparhdr,
             e_minalloc, e_maxalloc, e_ss, e_sp, e_csum,
             e_ip, e_cs, e_lfarlc, e_ovno;
    uint16_t e_res[4];
    uint16_t e_oemid, e_oeminfo;
    uint16_t e_res2[10];
    uint32_t e_lfanew;
} dt_dos_header_t;

typedef struct {
    uint16_t Machine;
    uint16_t NumberOfSections;
    uint32_t TimeDateStamp;
    uint32_t PointerToSymbolTable;
    uint32_t NumberOfSymbols;
    uint16_t SizeOfOptionalHeader;
    uint16_t Characteristics;
} dt_file_header_t;

typedef struct {
    uint16_t Magic;                  /* 0x20b for PE32+ */
    uint8_t  MajorLinkerVersion, MinorLinkerVersion;
    uint32_t SizeOfCode, SizeOfInitializedData, SizeOfUninitializedData;
    uint32_t AddressOfEntryPoint;
    uint32_t BaseOfCode;
    uint64_t ImageBase;
    uint32_t SectionAlignment, FileAlignment;
    uint16_t MajorOperatingSystemVersion, MinorOperatingSystemVersion;
    uint16_t MajorImageVersion, MinorImageVersion;
    uint16_t MajorSubsystemVersion, MinorSubsystemVersion;
    uint32_t Win32VersionValue, SizeOfImage, SizeOfHeaders, CheckSum;
    uint16_t Subsystem, DllCharacteristics;
    uint64_t SizeOfStackReserve, SizeOfStackCommit,
             SizeOfHeapReserve, SizeOfHeapCommit;
    uint32_t LoaderFlags, NumberOfRvaAndSizes;
    /* DataDirectory[NumberOfRvaAndSizes] follows */
} dt_optional_header_pe32plus_t;

typedef struct {
    uint32_t VirtualAddress;
    uint32_t Size;
} dt_data_directory_t;

typedef struct {
    char     Name[8];
    uint32_t VirtualSize;
    uint32_t VirtualAddress;
    uint32_t SizeOfRawData;
    uint32_t PointerToRawData;
    uint32_t PointerToRelocations, PointerToLinenumbers;
    uint16_t NumberOfRelocations, NumberOfLinenumbers;
    uint32_t Characteristics;
} dt_section_header_t;

/* Import directory entry (20 bytes). */
typedef struct {
    uint32_t OriginalFirstThunk; /* RVA to INT (hint/name table) */
    uint32_t TimeDateStamp;
    uint32_t ForwarderChain;
    uint32_t Name;               /* RVA to DLL name string */
    uint32_t FirstThunk;         /* RVA to IAT (where fixes go) */
} dt_import_dir_entry_t;
#pragma pack(pop)

/* ---- Import-table cache (lazy-built, reset per dt_discover call) -- */
typedef struct {
    int      is_ordinal;
    uint32_t iat_rva;
    int      dll_idx;
    uint16_t hint;
    char     name[128];
} dt_import_entry_t;

typedef struct {
    char               dll[64];
    uint32_t           iat_rva_start;
    uint32_t           iat_rva_end;
} dt_import_dll_t;

typedef struct {
    int              ready;
    dt_import_dll_t  dlls[256];
    int              dll_count;
    dt_import_entry_t entries[8192];
    int              entry_count;
} dt_import_cache_t;

/* ---- Engine-wide mutable state ------------------------------------ */
/* Singleton: one active discovery at a time. Reset on dt_engine_setup. */
typedef struct {
    dt_engine_ctx_t       ctx;
    dt_section_t          text_storage;
    dt_section_t          rdata_storage;
    dt_section_t          pdata_storage;
    dt_runtime_function_t runtime_funcs[65536];
    uint32_t              runtime_func_count;
    dt_import_cache_t     imports;
} dt_engine_state_t;

static dt_engine_state_t g_state;

/* Allow helpers in other TUs to reach the active context. */
const dt_engine_ctx_t *dt_engine_ctx_active(void) { return &g_state.ctx; }

/* ------------------------------------------------------------------- */
/* Public lookup helpers.                                              */
/* ------------------------------------------------------------------- */
const uint8_t *dt_image_at(const dt_engine_ctx_t *ctx, uint32_t rva, size_t n) {
    if (rva + n < rva) return NULL;                  /* wrap */
    if ((size_t)rva + n > ctx->image_size) return NULL;
    return ctx->image + rva;
}

size_t dt_rva_to_offset(const dt_engine_ctx_t *ctx, uint32_t rva) {
    /* Image is laid out by RVA in our buffer; offset == rva. */
    (void)ctx;
    return rva;
}

const dt_runtime_function_t *dt_find_runtime_function(
    const dt_engine_ctx_t *ctx, uint32_t rva) {
    const dt_runtime_function_t *arr = ctx->runtime_functions;
    int lo = 0, hi = (int)ctx->runtime_function_count - 1;
    while (lo <= hi) {
        int mid = (lo + hi) >> 1;
        if (rva < arr[mid].begin)      hi = mid - 1;
        else if (rva >= arr[mid].end)  lo = mid + 1;
        else                           return &arr[mid];
    }
    return NULL;
}

const dt_section_t *dt_section_text(void)  { return &g_state.text_storage; }
const dt_section_t *dt_section_rdata(void) { return &g_state.rdata_storage; }
const dt_section_t *dt_section_pdata(void) { return &g_state.pdata_storage; }

/* ------------------------------------------------------------------- */
/* Section discovery.                                                  */
/* ------------------------------------------------------------------- */
static int find_section_by_name(const dt_section_header_t *secs, int n,
                                const char *name, dt_section_t *out) {
    for (int i = 0; i < n; ++i) {
        char buf[9];
        memcpy(buf, secs[i].Name, 8); buf[8] = '\0';
        for (int k = 7; k >= 0 && buf[k] == '\0'; --k) buf[k] = '\0';
        if (strcmp(buf, name) == 0) {
            dt_strncpy(out->name, name, sizeof(out->name));
            out->rva         = secs[i].VirtualAddress;
            out->file_offset = secs[i].PointerToRawData;
            out->raw_size    = secs[i].SizeOfRawData;
            out->virtual_size= secs[i].VirtualSize;
            return 1;
        }
    }
    return 0;
}

/* ------------------------------------------------------------------- */
/* PE parse (internal).                                                */
/* ------------------------------------------------------------------- */
typedef struct {
    uint16_t num_sections;
    uint16_t size_opt_hdr;
    uint32_t num_rva_and_sizes;
    uint32_t import_dir_rva;
    uint32_t import_dir_size;
    uint64_t image_base;
    uint32_t size_of_image;
    uint32_t size_of_headers;
    dt_section_header_t secs[64];
} dt_pe_info_t;

static int parse_pe(const uint8_t *img, size_t img_size, dt_pe_info_t *out) {
    if (img_size < sizeof(dt_dos_header_t)) return -1;
    const dt_dos_header_t *dos = (const dt_dos_header_t *)img;
    if (dos->e_magic != DT_IMAGE_DOS_SIGNATURE) return -2;

    if ((size_t)dos->e_lfanew + 4 + sizeof(dt_file_header_t) > img_size)
        return -3;
    const uint8_t *pe_base = img + dos->e_lfanew;
    if (dt_load_u32le(pe_base) != DT_IMAGE_NT_SIGNATURE) return -4;

    const dt_file_header_t *fh =
        (const dt_file_header_t *)(pe_base + 4);
    out->num_sections    = fh->NumberOfSections;
    out->size_opt_hdr    = fh->SizeOfOptionalHeader;
    if (out->num_sections > 64) return -5;

    const dt_optional_header_pe32plus_t *oh =
        (const dt_optional_header_pe32plus_t *)
            ((const uint8_t *)fh + sizeof(dt_file_header_t));
    if (oh->Magic != 0x20b) return -6;                /* require PE32+ */

    out->image_base        = oh->ImageBase;
    out->size_of_image     = oh->SizeOfImage;
    out->size_of_headers   = oh->SizeOfHeaders;
    out->num_rva_and_sizes = oh->NumberOfRvaAndSizes;

    const dt_data_directory_t *dd =
        (const dt_data_directory_t *)
            ((const uint8_t *)oh + sizeof(*oh));
    if (out->num_rva_and_sizes > DT_IMAGE_DIRECTORY_ENTRY_IMPORT) {
        out->import_dir_rva  = dd[DT_IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
        out->import_dir_size = dd[DT_IMAGE_DIRECTORY_ENTRY_IMPORT].Size;
    }

    const dt_section_header_t *secs =
        (const dt_section_header_t *)
            ((const uint8_t *)oh + sizeof(*oh) +
             out->num_rva_and_sizes * sizeof(dt_data_directory_t));
    for (int i = 0; i < out->num_sections; ++i) out->secs[i] = secs[i];
    return 0;
}

/* Parse .pdata; returns count or negative on error.
 *
 * Uses min(raw_size, virtual_size) as the scan bound: in the in-memory
 * (RVA-laid-out) view, .pdata occupies virtual_size bytes; the on-disk
 * raw_size may be larger (file alignment padding) or smaller. See the
 * scan_bound rationale in scan.c. */
static int parse_pdata(const uint8_t *img, size_t img_size,
                       const dt_section_t *pdata,
                       dt_runtime_function_t *out, int max_entries) {
    uint32_t scan = pdata->raw_size <= pdata->virtual_size
                  ? pdata->raw_size : pdata->virtual_size;
    if ((size_t)pdata->rva + scan > img_size) return -1;
    const uint8_t *base = img + pdata->rva;
    int n = scan / 12;
    if (n > max_entries) n = max_entries;
    int count = 0;
    for (int i = 0; i < n; ++i) {
        uint32_t b = dt_load_u32le(base + i * 12 + 0);
        uint32_t e = dt_load_u32le(base + i * 12 + 4);
        uint32_t u = dt_load_u32le(base + i * 12 + 8);
        if (b == 0 && e == 0) continue;
        out[count].begin = b;
        out[count].end   = e;
        out[count].unwind= u;
        count++;
    }
    /* Insertion sort by (begin, end). ~44k entries, runs once. */
    for (int i = 1; i < count; ++i) {
        dt_runtime_function_t v = out[i];
        int j = i - 1;
        while (j >= 0 &&
               (out[j].begin > v.begin ||
                (out[j].begin == v.begin && out[j].end > v.end))) {
            out[j + 1] = out[j];
            --j;
        }
        out[j + 1] = v;
    }
    return count;
}

static int import_entry_cmp(const void *a, const void *b) {
    const dt_import_entry_t *pa = a;
    const dt_import_entry_t *pb = b;
    if (pa->iat_rva < pb->iat_rva) return -1;
    if (pa->iat_rva > pb->iat_rva) return  1;
    return 0;
}

static int parse_imports(const uint8_t *img, size_t img_size,
                         uint32_t import_dir_rva, uint32_t import_dir_size,
                         dt_import_cache_t *imp) {
    if (imp->ready) return 0;
    if (import_dir_rva == 0 || import_dir_size == 0) {
        imp->ready = 1;
        return 0;
    }
    if ((size_t)import_dir_rva + import_dir_size > img_size) return -1;

    const dt_import_dir_entry_t *dir =
        (const dt_import_dir_entry_t *)(img + import_dir_rva);
    int max_dir = import_dir_size / sizeof(dt_import_dir_entry_t);

    for (int i = 0; i < max_dir; ++i) {
        const dt_import_dir_entry_t *e = &dir[i];
        if (e->Name == 0 && e->FirstThunk == 0) break;

        if (imp->dll_count >= 256) break;
        dt_import_dll_t *dll = &imp->dlls[imp->dll_count];
        memset(dll, 0, sizeof(*dll));
        if (e->Name && (size_t)e->Name + 1 < img_size) {
            dt_strncpy(dll->dll, (const char *)(img + e->Name), sizeof(dll->dll));
        }
        /* OriginalFirstThunk (INT) is the authoritative name/hint table on
         * disk. If it's 0, the only fallback is FirstThunk (the IAT), which
         * the Windows loader overwrites with resolved function pointers once
         * the module is loaded. Reading the IAT as a name table in that
         * state would yield garbage (real pointers, not hint/name RVAs), so
         * we skip the DLL entirely rather than risk mis-parsing. Darktide.exe
         * has no bound imports (every descriptor has a non-zero INT), so this
         * is purely defensive against future game updates. The DLL's name is
         * still recorded above for diagnostics; its import entries are not. */
        if (e->OriginalFirstThunk == 0) {
            imp->dll_count++;
            continue;
        }
        uint32_t thunk_rva = e->OriginalFirstThunk;
        uint32_t iat_rva   = e->FirstThunk;
        dll->iat_rva_start = iat_rva;

        if (iat_rva == 0) {
            imp->dll_count++;
            continue;
        }

        int idx = 0;
        uint32_t cur_int = thunk_rva;
        uint32_t cur_iat = iat_rva;
        while (1) {
            if ((size_t)cur_int + 8 > img_size) break;
            uint64_t v = dt_load_u64le(img + cur_int);
            if (v == 0) break;

            if (imp->entry_count >= 8192) break;
            dt_import_entry_t *entry = &imp->entries[imp->entry_count];
            memset(entry, 0, sizeof(*entry));
            entry->iat_rva = cur_iat;
            entry->dll_idx = imp->dll_count;
            if (v & 0x8000000000000000ULL) {
                entry->is_ordinal = 1;
                snprintf(entry->name, sizeof(entry->name),
                         "ordinal#%u", (uint32_t)(v & 0xFFFF));
            } else {
                uint32_t hn_rva = (uint32_t)v;
                if (hn_rva + 2 < img_size) {
                    const uint8_t *p = img + hn_rva;
                    entry->hint = (uint16_t)(p[0] | (p[1] << 8));
                    size_t name_max = sizeof(entry->name) - 1;
                    size_t k = 0;
                    while (k < name_max && hn_rva + 2 + k < img_size &&
                           p[2 + k] != 0) {
                        entry->name[k] = (char)p[2 + k];
                        k++;
                    }
                    entry->name[k] = '\0';
                }
            }
            imp->entry_count++;
            cur_int += 8;
            cur_iat += 8;
            idx++;
        }
        dll->iat_rva_end = iat_rva + 8 * (uint32_t)idx;
        imp->dll_count++;
    }

    qsort(imp->entries, (size_t)imp->entry_count,
          sizeof(imp->entries[0]), import_entry_cmp);
    imp->ready = 1;
    return 0;
}

/* ------------------------------------------------------------------- */
/* Engine ctx setup (called once per dt_discover()).                   */
/* ------------------------------------------------------------------- */
int dt_engine_setup(const uint8_t *image, size_t image_size,
                    uint64_t image_base,
                    const dt_section_t **out_text,
                    const dt_section_t **out_rdata,
                    const dt_section_t **out_pdata,
                    const dt_runtime_function_t **out_rf,
                    uint32_t *out_rf_count) {
    memset(&g_state, 0, sizeof(g_state));

    dt_pe_info_t info; memset(&info, 0, sizeof(info));
    int rc = parse_pe(image, image_size, &info);
    if (rc != 0) return rc;

    if (!find_section_by_name(info.secs, info.num_sections, ".text",
                              &g_state.text_storage))  return -10;
    if (!find_section_by_name(info.secs, info.num_sections, ".rdata",
                              &g_state.rdata_storage)) return -11;
    if (!find_section_by_name(info.secs, info.num_sections, ".pdata",
                              &g_state.pdata_storage)) return -12;

    int n = parse_pdata(image, image_size, &g_state.pdata_storage,
                        g_state.runtime_funcs, 65536);
    if (n < 0) return -13;
    g_state.runtime_func_count = (uint32_t)n;

    parse_imports(image, image_size, info.import_dir_rva, info.import_dir_size,
                  &g_state.imports);

    g_state.ctx.image                  = image;
    g_state.ctx.image_size             = image_size;
    g_state.ctx.image_base             = image_base ? image_base : info.image_base;
    g_state.ctx.text                   = &g_state.text_storage;
    g_state.ctx.rdata                  = &g_state.rdata_storage;
    g_state.ctx.pdata                  = &g_state.pdata_storage;
    g_state.ctx.runtime_functions      = g_state.runtime_funcs;
    g_state.ctx.runtime_function_count = g_state.runtime_func_count;

    if (out_text)      *out_text      = &g_state.text_storage;
    if (out_rdata)     *out_rdata     = &g_state.rdata_storage;
    if (out_pdata)     *out_pdata     = &g_state.pdata_storage;
    if (out_rf)        *out_rf        = g_state.runtime_funcs;
    if (out_rf_count)  *out_rf_count  = g_state.runtime_func_count;
    return 0;
}

/* Expose the import cache to thunk.c. */
const dt_import_cache_t *dt_get_import_cache(void) { return &g_state.imports; }

/* ------------------------------------------------------------------- */
/* Resolve an IAT entry RVA to (dll, name). Returns 1 on hit.          */
/* ------------------------------------------------------------------- */
int dt_import_lookup(uint32_t iat_rva,
                     const char **dll_out, const char **name_out) {
    const dt_import_cache_t *imp = &g_state.imports;
    /* Binary search entries by iat_rva. */
    int lo = 0, hi = imp->entry_count - 1;
    while (lo <= hi) {
        int mid = (lo + hi) >> 1;
        if (imp->entries[mid].iat_rva < iat_rva)      lo = mid + 1;
        else if (imp->entries[mid].iat_rva > iat_rva) hi = mid - 1;
        else {
            if (dll_out)  *dll_out  = imp->dlls[imp->entries[mid].dll_idx].dll;
            if (name_out) *name_out = imp->entries[mid].name;
            return 1;
        }
    }
    return 0;
}
