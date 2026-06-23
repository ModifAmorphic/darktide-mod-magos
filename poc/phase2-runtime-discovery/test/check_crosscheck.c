/*
 * test/check_crosscheck.c - Tier A test harness.
 *
 * Loads Darktide.exe, runs the engine, and asserts the discovered
 * addresses match the Phase 0 cross-check table verbatim. Exits 0 on
 * success, non-zero on any mismatch.
 *
 * The 7 confirmed addresses are baked in here as the acceptance oracle.
 * lua_pcall is asserted to produce EITHER a candidate (with reasoning)
 * OR an explicit deferral \xe2\x80\x94 either is acceptable; a crash or silent
 * omission is not.
 *
 * Usage:
 *   check_crosscheck [<Darktide.exe>]
 */
#include "engine.h"
#include "util.h"
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>

/* The acceptance oracle (transcribed from addresses.json / report.md). */
#define EXPECT_SIZE           18715784u
#define EXPECT_SHA256_PREFIX  "132eed5fe58515774a41199269dd240ef6092f84" \
                              "b1efc8ad4a28e23ea6791661"
#define EXPECT_LUA_PANIC_BODY 0x328220u
#define EXPECT_INIT_BEGIN     0x32a660u
#define EXPECT_NEWSTATE_THUNK 0xc7c000u
#define EXPECT_NEWSTATE_BODY  0xc7eea0u
#define EXPECT_ATPANIC        0xc77f40u
#define EXPECT_GETTOP         0xc74050u
#define EXPECT_LOADBUFFER     0xc7ad80u

#define DEFAULT_BINARY "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe"

static int g_failures = 0;
static int g_checks   = 0;

#define CHECK(cond, ...) do { \
    g_checks++; \
    if (!(cond)) { \
        fprintf(stderr, "  FAIL: "); fprintf(stderr, __VA_ARGS__); \
        fprintf(stderr, "\n"); \
        g_failures++; \
    } else { \
        fprintf(stderr, "  ok:   "); fprintf(stderr, __VA_ARGS__); \
        fprintf(stderr, "\n"); \
    } \
} while (0)

/* ---- tiny PE mapper (same shape as tool/discover.c, kept here so the
 * test harness is self-contained). Reads optional-header fields by
 * direct byte offset (robust against struct alignment mistakes). */
#define DT_OH_MAGIC                  0
#define DT_OH_IMAGE_BASE             24
#define DT_OH_SIZE_OF_IMAGE          56
#define DT_OH_SIZE_OF_HEADERS        60
#define DT_OH_NUMBER_OF_RVA_AND_SIZES 108

typedef struct {
    char _n[8]; uint32_t VirtualSize, VirtualAddress;
    uint32_t SizeOfRawData, PointerToRawData; uint32_t _r[4];
} sh_t;

static int map_pe_by_rva(const uint8_t *file, size_t file_len,
                          uint8_t **out_image, size_t *out_image_size,
                          uint64_t *out_image_base) {
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
    uint16_t magic = (uint16_t)(oh[DT_OH_MAGIC] | (oh[DT_OH_MAGIC+1] << 8));
    if (magic != 0x20b) return -5;

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
    if (size_of_image == 0 || size_of_image > 256*1024*1024) return -8;

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
    *out_image = img; *out_image_size = size_of_image;
    *out_image_base = image_base;
    return 0;
}

static long file_size(int fd) { struct stat st; if (fstat(fd,&st)!=0) return -1; return (long)st.st_size; }

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

/* Find a Category B entry by name. */
static const dt_cat_b_t *find_cat_b(const dt_result_t *r, const char *name) {
    for (int i = 0; i < r->cat_b_count; ++i)
        if (strcmp(r->cat_b[i].name, name) == 0) return &r->cat_b[i];
    return NULL;
}

static int rva_in_list(const dt_cat_b_t *cb, uint32_t rva) {
    for (int i = 0; i < cb->candidate_rva_count; ++i)
        if ((uint32_t)strtoull(cb->candidate_rvas[i], NULL, 0) == rva) return 1;
    return 0;
}

int main(int argc, char **argv) {
    const char *binary = DEFAULT_BINARY;
    if (argc > 1) binary = argv[1];

    fprintf(stderr, "[tierA] reading %s\n", binary);
    uint8_t *file_bytes = NULL; size_t file_len = 0;
    if (read_file(binary, &file_bytes, &file_len) != 0) {
        fprintf(stderr, "[tierA] FAIL: cannot read binary\n");
        return 2;
    }

    fprintf(stderr, "[tierA] ---- binary checks ----\n");
    CHECK(file_len == EXPECT_SIZE,
          "binary size %zu matches expected %u", file_len, EXPECT_SIZE);

    char sha[65]; dt_sha256_hex(file_bytes, file_len, sha);
    CHECK(strncmp(sha, EXPECT_SHA256_PREFIX, 64) == 0,
          "binary SHA-256 %.16s... matches oracle", sha);

    uint8_t *image = NULL; size_t image_size = 0; uint64_t image_base = 0;
    int rc = map_pe_by_rva(file_bytes, file_len, &image, &image_size, &image_base);
    CHECK(rc == 0, "PE map by RVA succeeded (rc=%d)", rc);
    if (rc != 0) { free(file_bytes); return 1; }

    fprintf(stderr, "[tierA] running engine...\n");
    /* dt_result_t is ~3.4MB; allocate on the heap to avoid blowing the
     * stack when the test calls into dt_write_report etc. */
    dt_result_t *r = calloc(1, sizeof(*r));
    if (!r) { free(image); free(file_bytes); return 1; }
    rc = dt_discover(image, image_size, image_base, r);
    CHECK(rc == 0, "dt_discover() returned 0 (got %d)", rc);
    if (rc != 0) { free(r); free(image); free(file_bytes); return 1; }

    /* -------------------------------------------------------------- */
    fprintf(stderr, "[tierA] ---- Phase 0 cross-check table ----\n");
    fprintf(stderr, "[tierA] 7 confirmed addresses must reproduce exactly.\n");

    /* lua_panic body */
    {
        char buf[32]; snprintf(buf, sizeof(buf), "0x%x", EXPECT_LUA_PANIC_BODY);
        /* lua_panic_body_rva is the uint; compare by value. */
        CHECK(r->init.lua_panic_body_rva == EXPECT_LUA_PANIC_BODY,
              "lua_panic body == 0x%x (got 0x%x)",
              EXPECT_LUA_PANIC_BODY, r->init.lua_panic_body_rva);
    }

    /* init candidate */
    CHECK(r->init.found,
          "init candidate was selected");
    CHECK(r->init.begin == EXPECT_INIT_BEGIN,
          "init begin == 0x%x (got 0x%x)", EXPECT_INIT_BEGIN, r->init.begin);
    CHECK(r->init.lua_environment_marker_found,
          "init body contains 'lua_environment' string ref");
    CHECK(r->init.lea_of_lua_panic_count >= 1,
          "init has >=1 LEA-of-&lua_panic site (got %d)",
          r->init.lea_of_lua_panic_count);

    /* lua_newstate (thunk + body) */
    {
        const dt_cat_b_t *ns = find_cat_b(r, "lua_newstate");
        CHECK(ns != NULL, "lua_newstate present in category B");
        if (ns) {
            CHECK(rva_in_list(ns, EXPECT_NEWSTATE_THUNK),
                  "lua_newstate candidate_rvas contains thunk 0x%x",
                  EXPECT_NEWSTATE_THUNK);
            CHECK(rva_in_list(ns, EXPECT_NEWSTATE_BODY),
                  "lua_newstate candidate_rvas contains real body 0x%x",
                  EXPECT_NEWSTATE_BODY);
            CHECK(strcmp(ns->confidence, "high") == 0,
                  "lua_newstate confidence == 'high' (got '%s')", ns->confidence);
        }
    }

    /* lua_atpanic */
    {
        const dt_cat_b_t *ap = find_cat_b(r, "lua_atpanic");
        CHECK(ap != NULL, "lua_atpanic present in category B");
        if (ap) {
            CHECK(rva_in_list(ap, EXPECT_ATPANIC),
                  "lua_atpanic == 0x%x", EXPECT_ATPANIC);
            CHECK(strcmp(ap->confidence, "high") == 0,
                  "lua_atpanic confidence == 'high' (got '%s')", ap->confidence);
        }
    }

    /* lua_gettop */
    {
        const dt_cat_b_t *gt = find_cat_b(r, "lua_gettop");
        CHECK(gt != NULL, "lua_gettop present in category B");
        if (gt) {
            CHECK(rva_in_list(gt, EXPECT_GETTOP),
                  "lua_gettop == 0x%x", EXPECT_GETTOP);
            CHECK(strcmp(gt->confidence, "high") == 0,
                  "lua_gettop confidence == 'high' (got '%s')", gt->confidence);
        }
    }

    /* luaL_loadbuffer */
    {
        const dt_cat_b_t *lb = find_cat_b(r, "luaL_loadbuffer");
        CHECK(lb != NULL, "luaL_loadbuffer present in category B");
        if (lb) {
            CHECK(rva_in_list(lb, EXPECT_LOADBUFFER),
                  "luaL_loadbuffer == 0x%x", EXPECT_LOADBUFFER);
            CHECK(strcmp(lb->confidence, "high") == 0,
                  "luaL_loadbuffer confidence == 'high' (got '%s')", lb->confidence);
        }
    }

    /* lua_pcall: must be either candidate OR explicit deferral. */
    {
        const dt_cat_b_t *pc = find_cat_b(r, "lua_pcall");
        CHECK(pc != NULL, "lua_pcall present in category B (even if deferred)");
        if (pc) {
            int has_candidate = (pc->candidate_rva_count > 0);
            int has_deferral = (pc->evidence[0] != '\0' &&
                                strstr(pc->evidence, "deferred") != NULL);
            CHECK(has_candidate || has_deferral,
                  "lua_pcall is either a candidate or an honest deferral "
                  "(candidates=%d, deferral=%d, summary='%s')",
                  has_candidate, has_deferral, r->pcall_summary);
            /* The clustering attempt MUST have run (pcall_candidates or
             * a survey summary that mentions 'deferred' or 'candidate'). */
            CHECK(r->pcall_summary[0] != '\0',
                  "lua_pcall clustering summary was populated");
        }
    }

    /* ---- Phase 0 methodology gaps (sanity, all 6 must be present) -- */
    fprintf(stderr, "[tierA] ---- methodology gap handling ----\n");
    CHECK(r->methodology_gap_count == 6,
          "all 6 methodology gaps recorded (got %d)", r->methodology_gap_count);

    /* ---- Phase D must show the negative result (0 xrefs) ---------- */
    fprintf(stderr, "[tierA] ---- Phase D negative result ----\n");
    CHECK(r->phase_d.result_count == 4,
          "all 4 error strings surveyed (got %d)", r->phase_d.result_count);
    int total_xrefs = 0;
    for (int i = 0; i < r->phase_d.result_count; ++i)
        total_xrefs += r->phase_d.results[i].lea_xref_count;
    CHECK(total_xrefs == 0,
          "Phase D total LEA xrefs == 0 (gap confirmed; got %d)", total_xrefs);

    /* ---- Print summary ---- */
    fprintf(stderr, "\n[tierA] =========================================\n");
    fprintf(stderr, "[tierA]  checks:   %d\n", g_checks);
    fprintf(stderr, "[tierA]  failures: %d\n", g_failures);
    if (g_failures == 0) {
        fprintf(stderr, "[tierA]  RESULT: PASS \xe2\x80\x94 all 7 confirmed addresses "
                        "match Phase 0's oracle.\n");
    } else {
        fprintf(stderr, "[tierA]  RESULT: FAIL \xe2\x80\x94 see failures above.\n");
    }
    fprintf(stderr, "[tierA] =========================================\n");

    free(image); free(file_bytes);
    return g_failures == 0 ? 0 : 1;
}
