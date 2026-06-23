/*
 * tool/discover.c - Offline CLI wrapper around the discovery engine.
 *
 * This is the ONLY file in the project that touches the filesystem or
 * uses POSIX headers. The engine itself (everything under engine/) is
 * pure in-memory: it consumes a flat image buffer laid out by RVA, so
 * Phase 2b's injected DLL can call dt_discover() directly on the module
 * base returned by GetModuleHandleW(NULL).
 *
 * What this wrapper does:
 *   1. Read Darktide.exe from disk (size-checked against the pinned build).
 *   2. Parse the PE headers from the file bytes (to recover section RVAs
 *      and file offsets).
 *   3. Map sections into a flat RVA-indexed buffer (image[rva] == byte
 *      at that RVA). This mirrors what the Windows loader produces in
 *      process: each section is copied to its VirtualAddress.
 *   4. Compute the file SHA-256 (over file bytes, not mapped bytes).
 *   5. Call dt_discover(image, image_size, image_base, &result).
 *   6. Write output/addresses.json and output/report.md.
 *
 * Usage:
 *   dt_discover [<path-to-Darktide.exe>] [-o <output-dir>]
 *
 * Phase 2b note: the wrapper will be replaced by a DllMain that calls
 * dt_discover(GetModuleHandleW(NULL), ...). The engine sources do not
 * change.
 */
/* POSIX feature-test macros for readlink(), fstat(), open(), etc.
 * Phase 2b's wrapper (mingw) will define the equivalent Windows code
 * path; this file is Linux-only for Phase 2a. */
#define _POSIX_C_SOURCE 200809L
#define _DEFAULT_SOURCE 1

#include "engine.h"
#include "util.h"

/* winnt.h subset, mirrored from pe.c (kept private to that TU normally;
 * duplicated here only for the section mapping step in the wrapper). */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <fcntl.h>
#include <unistd.h>

/* PE32+ header byte offsets we need (computed directly to avoid struct
 * alignment pitfalls). All offsets relative to the start of the optional
 * header (immediately follows the 4-byte PE sig + 20-byte file header). */
#define DT_OH_MAGIC                  0    /* uint16 */
#define DT_OH_IMAGE_BASE             24   /* uint64 (PE32+) */
#define DT_OH_SECTION_ALIGNMENT      32   /* uint32 */
#define DT_OH_FILE_ALIGNMENT         36   /* uint32 */
#define DT_OH_SIZE_OF_IMAGE          56   /* uint32 */
#define DT_OH_SIZE_OF_HEADERS        60   /* uint32 */
#define DT_OH_NUMBER_OF_RVA_AND_SIZES 108 /* uint32 */

typedef struct {
    char _name[8]; uint32_t VirtualSize, VirtualAddress;
    uint32_t SizeOfRawData, PointerToRawData;
    uint32_t _r[4];
} section_hdr_t;

#define DEFAULT_BINARY "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe"

static long file_size(int fd) {
    struct stat st;
    if (fstat(fd, &st) != 0) return -1;
    return (long)st.st_size;
}

static int read_file(const char *path, uint8_t **out_buf, size_t *out_len) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) { perror("open"); return -1; }
    long sz = file_size(fd);
    if (sz < 0) { close(fd); return -1; }
    uint8_t *buf = malloc((size_t)sz);
    if (!buf) { close(fd); return -1; }
    size_t total = 0;
    while (total < (size_t)sz) {
        ssize_t got = read(fd, buf + total, (size_t)sz - total);
        if (got < 0) { perror("read"); free(buf); close(fd); return -1; }
        if (got == 0) break;
        total += (size_t)got;
    }
    close(fd);
    *out_buf = buf;
    *out_len = total;
    return 0;
}

/* Map file bytes into a flat RVA-indexed buffer of size SizeOfImage.
 * Reads PE header fields by direct offset (robust against struct
 * alignment mistakes that bit us earlier). */
static int map_pe_by_rva(const uint8_t *file, size_t file_len,
                          uint8_t **out_image, size_t *out_image_size,
                          uint64_t *out_image_base) {
    if (file_len < 0x40) return -1;
    /* DOS header e_lfanew at offset 0x3C. */
    uint32_t e_lfanew = (uint32_t)file[0x3C] | ((uint32_t)file[0x3D] << 8) |
                        ((uint32_t)file[0x3E] << 16) | ((uint32_t)file[0x3F] << 24);
    if ((size_t)e_lfanew + 4 + 20 > file_len) return -3;
    /* PE signature. */
    if (memcmp(file + e_lfanew, "PE\0\0", 4) != 0) return -4;

    /* File header (20 bytes after PE sig). NumberOfSections at offset 2. */
    const uint8_t *fh = file + e_lfanew + 4;
    uint16_t num_sections = (uint16_t)(fh[2] | (fh[3] << 8));
    uint16_t size_opt_hdr = (uint16_t)(fh[16] | (fh[17] << 8));

    /* Optional header immediately follows file header. */
    const uint8_t *oh = fh + 20;
    if ((size_t)(oh - file) + size_opt_hdr > file_len) return -7;
    uint16_t magic = (uint16_t)(oh[DT_OH_MAGIC] | (oh[DT_OH_MAGIC+1] << 8));
    if (magic != 0x20b) return -5;                       /* require PE32+ */

    /* Read the few fields we need by offset. */
    uint64_t image_base = 0;
    for (int i = 0; i < 8; ++i)
        image_base |= (uint64_t)oh[DT_OH_IMAGE_BASE + i] << (8 * i);
    uint32_t size_of_image = (uint32_t)oh[DT_OH_SIZE_OF_IMAGE]        |
                             ((uint32_t)oh[DT_OH_SIZE_OF_IMAGE+1] << 8) |
                             ((uint32_t)oh[DT_OH_SIZE_OF_IMAGE+2] << 16)|
                             ((uint32_t)oh[DT_OH_SIZE_OF_IMAGE+3] << 24);
    uint32_t size_of_headers = (uint32_t)oh[DT_OH_SIZE_OF_HEADERS]        |
                               ((uint32_t)oh[DT_OH_SIZE_OF_HEADERS+1] << 8) |
                               ((uint32_t)oh[DT_OH_SIZE_OF_HEADERS+2] << 16)|
                               ((uint32_t)oh[DT_OH_SIZE_OF_HEADERS+3] << 24);
    (void)oh; /* NumberOfRvaAndSizes not needed by the wrapper; the engine
               * re-parses PE itself via dt_engine_setup(). */

    if (size_of_image == 0 || size_of_image > 256*1024*1024) return -8;

    uint8_t *img = calloc(1, size_of_image);
    if (!img) return -6;

    /* Copy headers (capped to file_len). */
    size_t hb = size_of_headers;
    if (hb > file_len) hb = file_len;
    if (hb > size_of_image) hb = size_of_image;
    memcpy(img, file, hb);

    /* Section table immediately follows the optional header. */
    const section_hdr_t *secs = (const section_hdr_t *)(oh + size_opt_hdr);
    for (int i = 0; i < num_sections; ++i) {
        if ((size_t)(secs + i + 1) - (size_t)file > file_len) break;
        uint32_t va = secs[i].VirtualAddress;
        uint32_t raw = secs[i].SizeOfRawData;
        uint32_t ptr = secs[i].PointerToRawData;
        if (va + raw > size_of_image) continue;
        if ((size_t)ptr + raw > file_len) continue;
        memcpy(img + va, file + ptr, raw);
    }

    *out_image       = img;
    *out_image_size  = size_of_image;
    *out_image_base  = image_base;
    return 0;
}

int main(int argc, char **argv) {
    const char *binary = DEFAULT_BINARY;
    const char *out_dir = NULL;
    for (int i = 1; i < argc; ++i) {
        if (strcmp(argv[i], "-o") == 0 && i + 1 < argc) {
            out_dir = argv[++i];
        } else if (strcmp(argv[i], "-h") == 0 || strcmp(argv[i], "--help") == 0) {
            fprintf(stderr, "usage: %s [<Darktide.exe>] [-o <output-dir>]\n",
                    argv[0]);
            return 0;
        } else {
            binary = argv[i];
        }
    }

    /* Resolve output dir: default to <script_dir>/output. */
    char out_dir_buf[1024];
    if (!out_dir) {
        /* Resolve the directory of the executable. */
        ssize_t n = readlink("/proc/self/exe", out_dir_buf, sizeof(out_dir_buf) - 16);
        if (n < 0) { perror("readlink /proc/self/exe"); return 1; }
        out_dir_buf[n] = '\0';
        char *slash = strrchr(out_dir_buf, '/');
        if (slash) *slash = '\0';
        strncat(out_dir_buf, "/output", sizeof(out_dir_buf) - strlen(out_dir_buf) - 1);
        out_dir = out_dir_buf;
    }

    fprintf(stderr, "[phase2a] reading %s\n", binary);
    uint8_t *file_bytes = NULL; size_t file_len = 0;
    if (read_file(binary, &file_bytes, &file_len) != 0) return 1;

    if (file_len != DT_EXPECTED_FILE_SIZE) {
        fprintf(stderr,
            "ABORT: binary size mismatch. Expected %u bytes, got %zu. "
            "The offsets in the anchors doc are pinned to the %u-byte build; "
            "refusing to continue.\n",
            DT_EXPECTED_FILE_SIZE, file_len, DT_EXPECTED_FILE_SIZE);
        free(file_bytes); return 1;
    }

    char sha[65];
    dt_sha256_hex(file_bytes, file_len, sha);
    fprintf(stderr, "[phase2a] binary ok: %zu bytes, sha256=%.16s...\n",
            file_len, sha);

    /* Map by RVA. */
    uint8_t *image = NULL; size_t image_size = 0; uint64_t image_base = 0;
    int rc = map_pe_by_rva(file_bytes, file_len, &image, &image_size, &image_base);
    if (rc != 0) {
        fprintf(stderr, "ABORT: PE map failed (rc=%d)\n", rc);
        free(file_bytes); return 1;
    }
    fprintf(stderr, "[phase2a] mapped image: %zu bytes at base 0x%llx\n",
            image_size, (unsigned long long)image_base);

    /* Run the engine. dt_result_t is ~3.4MB (lots of evidence strings
     * and call-graph slots) so it must live on the heap, not the stack. */
    dt_result_t *result = calloc(1, sizeof(*result));
    if (!result) { free(image); free(file_bytes); return 1; }
    rc = dt_discover(image, image_size, image_base, result);
    if (rc != 0) {
        fprintf(stderr, "ABORT: dt_discover failed (rc=%d)\n", rc);
        free(result); free(image); free(file_bytes); return 1;
    }

    /* Fill in the binary metadata that only the wrapper knows. */
    snprintf(result->binary_path, sizeof(result->binary_path), "%s", binary);
    result->binary_size = (uint32_t)file_len;
    snprintf(result->sha256, sizeof(result->sha256), "%s", sha);

    /* Print a one-line summary per confirmed function to stderr. */
    fprintf(stderr, "[phase2a] ---- discovery summary ----\n");
    {
        char panic_buf[32];
        snprintf(panic_buf, sizeof(panic_buf), "0x%x",
                 result->init.lua_panic_body_rva);
        fprintf(stderr, "[phase2a] init: %s (%d bytes), marker=%s, panic_body=%s\n",
                result->init.begin_rva, result->init.size_bytes,
                result->init.lua_environment_marker_found ? "yes" : "no",
                panic_buf);
    }
    for (int i = 0; i < result->cat_b_count; ++i) {
        char rvas[128] = "";
        for (int k = 0; k < result->cat_b[i].candidate_rva_count; ++k) {
            char one[24];
            snprintf(one, sizeof(one), "%s%s", k ? "," : "",
                     result->cat_b[i].candidate_rvas[k]);
            strncat(rvas, one, sizeof(rvas) - strlen(rvas) - 1);
        }
        fprintf(stderr, "[phase2a]   %-18s %-6s %s\n",
                result->cat_b[i].name, result->cat_b[i].confidence,
                rvas[0] ? rvas : "(none)");
    }
    fprintf(stderr, "[phase2a]   lua_pcall: %s\n", result->pcall_summary);

    /* Make the output dir if missing (mkdir -p). */
    char mkdir_cmd[1200];
    snprintf(mkdir_cmd, sizeof(mkdir_cmd), "mkdir -p '%s'", out_dir);
    if (system(mkdir_cmd) != 0) {
        fprintf(stderr, "[phase2a] warn: mkdir -p %s failed\n", out_dir);
    }

    char json_path[1200], report_path[1200];
    snprintf(json_path,   sizeof(json_path),   "%s/addresses.json", out_dir);
    snprintf(report_path, sizeof(report_path), "%s/report.md",      out_dir);

    FILE *jf = fopen(json_path, "wb");
    if (!jf) { perror("fopen addresses.json"); rc = 1; goto done; }
    dt_write_json(result, jf);
    fclose(jf);
    fprintf(stderr, "[phase2a] wrote %s\n", json_path);

    FILE *rf = fopen(report_path, "wb");
    if (!rf) { perror("fopen report.md"); rc = 1; goto done; }
    dt_write_report(result, rf);
    fclose(rf);
    fprintf(stderr, "[phase2a] wrote %s\n", report_path);

done:
    free(result);
    free(image);
    free(file_bytes);
    return rc;
}
