/*
 * corrupt_iat_test.c — Phase 2b regression test (should-fix #5).
 *
 * Guards the engine's IAT-value-independence property permanently: the
 * discovered addresses must NOT change when every IAT slot is overwritten
 * with garbage. This is the one-off probe the reviewer ran to prove the
 * property; ported into the test suite so a future regression is caught.
 *
 * Why this matters: at runtime, the Windows loader overwrites IAT slots
 * with resolved function pointers (real addresses, not hint/name RVAs).
 * The engine must NOT depend on those bytes. It reads import names from
 * the INT (OriginalFirstThunk) and resolves import thunks by IAT-slot
 * RVA (not by IAT-slot value). Corrupting the values must be a no-op.
 *
 * Method:
 *   1. Read Darktide.exe, map by RVA into a buffer (same as check_crosscheck).
 *   2. Run discovery on the CLEAN image; serialize JSON to a string.
 *   3. Make a SECOND copy, corrupt every IAT slot (0xDEADBEEFCAFEBABE+i),
 *      run discovery, serialize JSON to a second string.
 *   4. Assert the two JSON strings are byte-identical (0 diffs).
 *
 * Exit codes: 0 PASS, non-zero FAIL.
 *
 * Native gcc (Linux) — mirrors check_crosscheck.c's build path.
 */
#define _POSIX_C_SOURCE 200809L

#include "engine.h"
#include "util.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>

#define DEFAULT_BINARY "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe"

/* ---- tiny PE mapper (same as check_crosscheck.c) ------------------- */
#define OH_MAGIC                  0
#define OH_IMAGE_BASE             24
#define OH_SIZE_OF_IMAGE          56
#define OH_SIZE_OF_HEADERS        60
#define OH_NUMBER_OF_RVA_AND_SIZES 108
#define DD_IMPORT_VA             (OH_NUMBER_OF_RVA_AND_SIZES + 0)  /* not used */

typedef struct {
    char _n[8]; uint32_t VirtualSize, VirtualAddress;
    uint32_t SizeOfRawData, PointerToRawData; uint32_t _r[4];
} sh_t;

typedef struct {
    uint32_t OriginalFirstThunk;
    uint32_t TimeDateStamp;
    uint32_t ForwarderChain;
    uint32_t Name;
    uint32_t FirstThunk;
} import_desc_t;

static int map_pe_by_rva(const uint8_t *file, size_t file_len,
                         uint8_t **out_image, size_t *out_image_size,
                         uint64_t *out_image_base,
                         uint32_t *out_import_dir_rva,
                         uint32_t *out_import_dir_size) {
    if (file_len < 0x40) return -1;
    uint32_t e_lfanew = (uint32_t)file[0x3C] | ((uint32_t)file[0x3D] << 8) |
                        ((uint32_t)file[0x3E] << 16) | ((uint32_t)file[0x3F] << 24);
    if ((size_t)e_lfanew + 24 > file_len) return -3;
    if (memcmp(file + e_lfanew, "PE\0\0", 4) != 0) return -4;

    const uint8_t *fh = file + e_lfanew + 4;
    uint16_t num_sections = (uint16_t)(fh[2] | (fh[3] << 8));
    uint16_t size_opt_hdr = (uint16_t)(fh[16] | (fh[17] << 8));
    const uint8_t *oh = fh + 20;
    if ((size_t)(oh - file) + size_opt_hdr > file_len) return -7;
    uint16_t magic = (uint16_t)(oh[OH_MAGIC] | (oh[OH_MAGIC + 1] << 8));
    if (magic != 0x20b) return -5;

    uint64_t image_base = 0;
    for (int i = 0; i < 8; ++i)
        image_base |= (uint64_t)oh[OH_IMAGE_BASE + i] << (8 * i);
    uint32_t size_of_image = (uint32_t)oh[OH_SIZE_OF_IMAGE] |
                             ((uint32_t)oh[OH_SIZE_OF_IMAGE + 1] << 8) |
                             ((uint32_t)oh[OH_SIZE_OF_IMAGE + 2] << 16) |
                             ((uint32_t)oh[OH_SIZE_OF_IMAGE + 3] << 24);
    uint32_t size_of_headers = (uint32_t)oh[OH_SIZE_OF_HEADERS] |
                               ((uint32_t)oh[OH_SIZE_OF_HEADERS + 1] << 8) |
                               ((uint32_t)oh[OH_SIZE_OF_HEADERS + 2] << 16) |
                               ((uint32_t)oh[OH_SIZE_OF_HEADERS + 3] << 24);
    if (size_of_image == 0 || size_of_image > 256 * 1024 * 1024) return -8;

    uint8_t *img = calloc(1, size_of_image);
    if (!img) return -6;
    size_t hb = size_of_headers;
    if (hb > file_len) hb = file_len;
    if (hb > size_of_image) hb = size_of_image;
    memcpy(img, file, hb);

    const sh_t *secs = (const sh_t *)(oh + size_opt_hdr);
    for (int i = 0; i < num_sections; ++i) {
        if ((size_t)(secs + i + 1) - (size_t)file > file_len) break;
        uint32_t va = secs[i].VirtualAddress;
        uint32_t raw = secs[i].SizeOfRawData;
        uint32_t ptr = secs[i].PointerToRawData;
        if (va + raw > size_of_image) continue;
        if ((size_t)ptr + raw > file_len) continue;
        memcpy(img + va, file + ptr, raw);
    }
    /* Import directory = DataDirectory[1], immediately follows NumberOfRvaAndSizes.
     * DataDirectory starts at oh + 0x70 (PE32+). DD[1] at oh + 0x78. */
    uint32_t import_rva  = (uint32_t)oh[0x78] | ((uint32_t)oh[0x79] << 8) |
                           ((uint32_t)oh[0x7A] << 16) | ((uint32_t)oh[0x7B] << 24);
    uint32_t import_size = (uint32_t)oh[0x7C] | ((uint32_t)oh[0x7D] << 8) |
                           ((uint32_t)oh[0x7E] << 16) | ((uint32_t)oh[0x7F] << 24);

    *out_image = img; *out_image_size = size_of_image;
    *out_image_base = image_base;
    *out_import_dir_rva = import_rva;
    *out_import_dir_size = import_size;
    return 0;
}

/* Overwrite every IAT slot in the mapped image with garbage. Walks the
 * import directory, and for each descriptor walks OriginalFirstThunk to
 * find the entry count, then overwrites FirstThunk[0..count). Returns
 * the number of slots corrupted. */
static long corrupt_all_iat(uint8_t *img, size_t img_size,
                            uint32_t import_rva, uint32_t import_size) {
    if (import_rva == 0 || import_size == 0) return 0;
    if ((size_t)import_rva + import_size > img_size) return 0;
    long count = 0;
    long i = 0;
    for (;;) {
        size_t off = import_rva + (size_t)i * sizeof(import_desc_t);
        if (off + sizeof(import_desc_t) > img_size) break;
        import_desc_t *d = (import_desc_t *)(img + off);
        if (d->Name == 0 && d->FirstThunk == 0) break;  /* terminator */

        uint32_t int_rva = d->OriginalFirstThunk;
        uint32_t iat_rva = d->FirstThunk;
        if (int_rva == 0 || iat_rva == 0) { i++; continue; }

        /* Count entries via the INT (OriginalFirstThunk). The INT and IAT
         * are parallel arrays of the same length. */
        uint32_t cur_int = int_rva;
        uint32_t cur_iat = iat_rva;
        while (cur_int + 8 <= img_size && cur_iat + 8 <= img_size) {
            uint64_t v = dt_load_u64le(img + cur_int);
            if (v == 0) break;
            /* Overwrite the IAT slot with a distinct garbage value. */
            uint64_t garbage = 0xDEADBEEFCAFEBABEULL + (uint64_t)count;
            for (int b = 0; b < 8; ++b)
                img[cur_iat + b] = (uint8_t)(garbage >> (8 * b));
            count++;
            cur_int += 8;
            cur_iat += 8;
        }
        i++;
    }
    return count;
}

static long file_size(int fd) { struct stat st; if (fstat(fd, &st) != 0) return -1; return (long)st.st_size; }

static int read_file(const char *path, uint8_t **buf, size_t *len) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    long sz = file_size(fd);
    if (sz < 0) { close(fd); return -1; }
    uint8_t *b = malloc((size_t)sz);
    if (!b) { close(fd); return -1; }
    size_t total = 0;
    while (total < (size_t)sz) {
        ssize_t got = read(fd, b + total, (size_t)sz - total);
        if (got <= 0) break;
        total += (size_t)got;
    }
    close(fd);
    *buf = b; *len = total; return 0;
}

/* Serialize discovery output to a malloc'd string via open_memstream. */
static char *run_discovery_to_json(const uint8_t *image, size_t image_size,
                                   uint64_t image_base, size_t *out_len) {
    dt_result_t *r = calloc(1, sizeof(*r));
    if (!r) return NULL;
    int rc = dt_discover(image, image_size, image_base, r);
    if (rc != 0) { free(r); return NULL; }

    char *buf = NULL;
    size_t len = 0;
    FILE *mf = open_memstream(&buf, &len);
    if (!mf) { free(r); return NULL; }
    dt_write_json(r, mf);
    fclose(mf);
    free(r);
    *out_len = len;
    return buf;
}

int main(int argc, char **argv) {
    const char *binary = (argc > 1) ? argv[1] : DEFAULT_BINARY;
    fprintf(stderr, "[iat] reading %s\n", binary);

    uint8_t *file_bytes = NULL; size_t file_len = 0;
    if (read_file(binary, &file_bytes, &file_len) != 0) {
        fprintf(stderr, "[iat] FAIL: cannot read binary\n");
        return 2;
    }

    /* Clean image. */
    uint8_t *clean_img = NULL; size_t clean_size = 0; uint64_t img_base = 0;
    uint32_t imp_rva = 0, imp_size = 0;
    int rc = map_pe_by_rva(file_bytes, file_len, &clean_img, &clean_size,
                           &img_base, &imp_rva, &imp_size);
    if (rc != 0) {
        fprintf(stderr, "[iat] FAIL: clean PE map failed (rc=%d)\n", rc);
        return 3;
    }
    fprintf(stderr, "[iat] import dir: rva=0x%x size=0x%x\n", imp_rva, imp_size);

    /* Corrupted image: fresh map, then trash every IAT slot. */
    uint8_t *corrupt_img = NULL; size_t corrupt_size = 0; uint64_t cb = 0;
    uint32_t cr = 0, cs = 0;
    rc = map_pe_by_rva(file_bytes, file_len, &corrupt_img, &corrupt_size,
                       &cb, &cr, &cs);
    if (rc != 0) {
        fprintf(stderr, "[iat] FAIL: corrupt PE map failed (rc=%d)\n", rc);
        return 3;
    }
    long n_corrupted = corrupt_all_iat(corrupt_img, corrupt_size, cr, cs);
    fprintf(stderr, "[iat] corrupted %ld IAT slots with 0xDEADBEEFCAFEBABE+i\n",
            n_corrupted);
    if (n_corrupted == 0) {
        fprintf(stderr, "[iat] FAIL: no IAT slots found to corrupt\n");
        return 4;
    }

    /* Run discovery on both. */
    fprintf(stderr, "[iat] running discovery on clean image...\n");
    size_t clean_json_len = 0;
    char *clean_json = run_discovery_to_json(clean_img, clean_size, img_base,
                                             &clean_json_len);
    if (!clean_json) {
        fprintf(stderr, "[iat] FAIL: clean discovery failed\n");
        return 5;
    }

    fprintf(stderr, "[iat] running discovery on IAT-corrupted image...\n");
    size_t corrupt_json_len = 0;
    char *corrupt_json = run_discovery_to_json(corrupt_img, corrupt_size, cb,
                                               &corrupt_json_len);
    if (!corrupt_json) {
        fprintf(stderr, "[iat] FAIL: corrupt discovery failed\n");
        return 5;
    }

    /* Compare: must be byte-identical. */
    int pass = (clean_json_len == corrupt_json_len &&
                memcmp(clean_json, corrupt_json, clean_json_len) == 0);
    fprintf(stderr, "[iat] clean json: %zu bytes, corrupt json: %zu bytes\n",
            clean_json_len, corrupt_json_len);

    if (!pass) {
        /* Find the first differing byte for diagnostics. */
        size_t minlen = clean_json_len < corrupt_json_len ? clean_json_len : corrupt_json_len;
        size_t diff_at = minlen;
        for (size_t k = 0; k < minlen; ++k) {
            if (clean_json[k] != corrupt_json[k]) { diff_at = k; break; }
        }
        fprintf(stderr, "[iat] FAIL: JSON differs at byte %zu (clean_len=%zu corrupt_len=%zu)\n",
                diff_at, clean_json_len, corrupt_json_len);
        /* Show a window around the diff. */
        size_t lo = diff_at > 80 ? diff_at - 80 : 0;
        size_t hi = diff_at + 80;
        if (hi > minlen) hi = minlen;
        fprintf(stderr, "[iat] clean   context: ...%.*s...\n",
                (int)(hi - lo), clean_json + lo);
        fprintf(stderr, "[iat] corrupt context: ...%.*s...\n",
                (int)(hi - lo), corrupt_json + lo);
        /* Dump both to /tmp for offline diff. */
        FILE *cf = fopen("/tmp/clean.json", "wb");
        FILE *xf = fopen("/tmp/corrupt.json", "wb");
        if (cf) { fwrite(clean_json, 1, clean_json_len, cf); fclose(cf); }
        if (xf) { fwrite(corrupt_json, 1, corrupt_json_len, xf); fclose(xf); }
        fprintf(stderr, "[iat] full outputs at /tmp/clean.json and /tmp/corrupt.json\n");
    } else {
        fprintf(stderr, "[iat] PASS: IAT corruption produced 0 diffs in discovery output.\n");
        fprintf(stderr, "[iat] (engine read %ld corrupted IAT slots; result identical)\n",
                n_corrupted);
    }

    free(clean_json);
    free(corrupt_json);
    free(clean_img);
    free(corrupt_img);
    free(file_bytes);
    return pass ? 0 : 1;
}
