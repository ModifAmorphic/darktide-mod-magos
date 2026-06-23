/* in_memory_host.c — Phase 2b Tier A1 test: in-memory engine correctness.
 *
 * This is the STRONG GATE. It validates that the discovery engine produces
 * the correct 7 addresses when run against a real loader-mapped image of
 * Darktide.exe (NOT the manual RVA-map that check_crosscheck.c uses).
 *
 * Strategy:
 *   1. LoadLibraryExW(Darktide.exe, NULL, <image flag>) under Wine. Try
 *      LOAD_LIBRARY_AS_IMAGE_RESOURCE first (sections mapped to RVAs,
 *      IAT NOT fixed up — which is fine since the engine is IAT-value-
 *      independent); fall back to LOAD_LIBRARY_AS_DATAFILE if Wine rejects
 *      the first flag.
 *   2. Mask the low bits off the returned handle to get the mapped base.
 *   3. Read SizeOfImage from the in-memory PE optional header.
 *   4. Heap-allocate dt_result_t and call dt_discover.
 *   5. Assert the 7 baked-in expected addresses match exactly.
 *
 * Exit codes:
 *   0  PASS — all 7 addresses match via the in-memory path
 *   1..9  various load/PE/discovery failures
 *   10+N  N addresses mismatched (each failure is logged)
 *
 * Run via run_in_memory_test.sh (sets the Wine path to Darktide.exe).
 */
#include "engine.h"
#include "util.h"

#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* PE32+ optional-header field offsets from e_lfanew. The optional header
 * starts at e_lfanew + 0x18, so ImageBase (opt+0x18) is at e_lfanew+0x30
 * and SizeOfImage (opt+0x38) is at e_lfanew+0x50. */
#define OFF_FROM_LFANEW_IMAGE_BASE     0x30u
#define OFF_FROM_LFANEW_SIZE_OF_IMAGE  0x50u

static int g_failures = 0;
static int g_checks   = 0;

#define CHECK(cond, ...) do { \
    g_checks++; \
    if (!(cond)) { \
        printf("  FAIL: "); printf(__VA_ARGS__); printf("\n"); \
        g_failures++; \
    } else { \
        printf("  ok:   "); printf(__VA_ARGS__); printf("\n"); \
    } \
} while (0)

/* Validate that base points at a mapped PE image: MZ at +0, PE at +e_lfanew. */
static int looks_like_mapped_pe(uintptr_t base) {
    if (base == 0) return 0;
    const uint8_t *p = (const uint8_t *)base;
    /* Touch carefully; if the page isn't mapped we'd SEGV, but Wine maps
     * the headers as part of any LoadLibraryEx view, so this is safe. */
    if (p[0] != 'M' || p[1] != 'Z') return 0;
    uint32_t e_lfanew = *(const uint32_t *)(p + 0x3C);
    if (e_lfanew == 0 || e_lfanew > (1u << 20)) return 0;
    if (memcmp(p + e_lfanew, "PE\0\0", 4) != 0) return 0;
    return 1;
}

static const dt_cat_b_t *find_cat_b(const dt_result_t *r, const char *label) {
    for (int i = 0; i < r->cat_b_count; ++i)
        if (strcmp(r->cat_b[i].name, label) == 0) return &r->cat_b[i];
    return NULL;
}

/* Try a sequence of LoadLibraryEx flags to get an RVA-mapped image view.
 * Per MSDN: with LOAD_LIBRARY_AS_DATAFILE or LOAD_LIBRARY_AS_IMAGE_RESOURCE
 * the low 2 bits of the handle are set; mask them off before treating the
 * handle as a base address. Returns the masked base, or 0 on failure. */
static uintptr_t load_image_view(const WCHAR *path, uint32_t *out_size,
                                 uint64_t *out_image_base) {
    DWORD flags[] = {
        LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_AS_DATAFILE, /* 0x22 */
        LOAD_LIBRARY_AS_IMAGE_RESOURCE,                            /* 0x20 */
        LOAD_LIBRARY_AS_DATAFILE,                                  /* 0x2  */
    };
    for (int i = 0; i < 3; ++i) {
        HMODULE h = LoadLibraryExW(path, NULL, flags[i]);
        if (!h) continue;
        uintptr_t base = (uintptr_t)h & ~((uintptr_t)0x3);
        if (!looks_like_mapped_pe(base)) {
            FreeLibrary(h);
            continue;
        }
        /* Read SizeOfImage + ImageBase from the optional header. */
        const uint8_t *p = (const uint8_t *)base;
        uint32_t e_lfanew = *(const uint32_t *)(p + 0x3C);
        *out_size = *(const uint32_t *)(p + e_lfanew + OFF_FROM_LFANEW_SIZE_OF_IMAGE);
        *out_image_base = *(const uint64_t *)(p + e_lfanew + OFF_FROM_LFANEW_IMAGE_BASE);
        printf("[A1] mapped via flag 0x%x: base=0x%llx size=0x%x image_base=0x%llx\n",
               (unsigned)flags[i], (unsigned long long)base, *out_size,
               (unsigned long long)*out_image_base);
        /* NOTE: we deliberately do NOT FreeLibrary until after discovery,
         * since the engine reads the mapped bytes. The caller frees. We
         * return the original handle via a side-channel... but actually
         * FreeLibrary takes the original HMODULE. Stash it. */
        return base;
        /* (The HMODULE leak here is intentional for the test's lifetime —
         * the process exits immediately after. Not worth the complexity
         * of threading the handle back.) */
    }
    return 0;
}

int main(int argc, char **argv) {
    /* Default Wine-side path to the game binary. Override via argv[1]. */
    const char *utf8_path = (argc > 1) ? argv[1] :
        "Z:\\games\\steamapps\\common\\Warhammer 40,000 DARKTIDE\\binaries\\Darktide.exe";

    /* Convert UTF-8 path to UTF-16 for the Wide APIs. */
    WCHAR wpath[MAX_PATH];
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8_path, -1, wpath, MAX_PATH);
    if (n == 0) {
        printf("[A1] FAIL: path conversion failed for %s\n", utf8_path);
        return 2;
    }
    printf("[A1] loading %s as mapped image\n", utf8_path);

    uint32_t size = 0;
    uint64_t image_base = 0;
    uintptr_t base = load_image_view(wpath, &size, &image_base);
    if (base == 0) {
        printf("[A1] FAIL: LoadLibraryEx could not map the image under any "
               "tried flag (err=%lu)\n", GetLastError());
        return 3;
    }
    CHECK(size != 0, "SizeOfImage non-zero (0x%x)", size);
    CHECK(size <= (256u << 20), "SizeOfImage plausible (<=256MB)");

    /* Heap-allocate the result (3.4 MB). */
    dt_result_t *r = (dt_result_t *)calloc(1, sizeof(dt_result_t));
    if (!r) {
        printf("[A1] FAIL: calloc(%zu) failed\n", sizeof(dt_result_t));
        return 4;
    }

    printf("[A1] running dt_discover against the loader-mapped image...\n");
    int rc = dt_discover((const uint8_t *)base, size, image_base, r);
    CHECK(rc == 0, "dt_discover() returned 0 (got %d)", rc);
    if (rc != 0) { free(r); return 5; }

    /* The 7 cross-check addresses (same oracle as check_crosscheck.c). */
    printf("[A1] ---- cross-check (in-memory path) ----\n");
    CHECK(r->init.lua_panic_body_rva == 0x328220u,
          "lua_panic body == 0x328220 (got 0x%x)", r->init.lua_panic_body_rva);
    CHECK(r->init.found, "init candidate selected");
    CHECK(r->init.begin == 0x32a660u,
          "init begin == 0x32a660 (got 0x%x)", r->init.begin);

    /* Find a cat_b entry by name. */
    #define FIND_CB(label) \
        const dt_cat_b_t *cb = find_cat_b(r, (label))

    {
        FIND_CB("lua_newstate");
        CHECK(cb != NULL, "lua_newstate present");
        if (cb) {
            CHECK((uint32_t)dt_parse_hex(cb->thunk_entry_rva) == 0xc7c000u,
                  "lua_newstate thunk == 0xc7c000 (got 0x%x)",
                  (uint32_t)dt_parse_hex(cb->thunk_entry_rva));
            CHECK((uint32_t)dt_parse_hex(cb->real_body_rva) == 0xc7eea0u,
                  "lua_newstate body == 0xc7eea0 (got 0x%x)",
                  (uint32_t)dt_parse_hex(cb->real_body_rva));
        }
    }
    {
        FIND_CB("lua_atpanic");
        CHECK(cb != NULL && cb->candidate_rva_count >= 1, "lua_atpanic present");
        if (cb && cb->candidate_rva_count >= 1)
            CHECK((uint32_t)dt_parse_hex(cb->candidate_rvas[0]) == 0xc77f40u,
                  "lua_atpanic == 0xc77f40 (got 0x%x)",
                  (uint32_t)dt_parse_hex(cb->candidate_rvas[0]));
    }
    {
        FIND_CB("lua_gettop");
        CHECK(cb != NULL && cb->candidate_rva_count >= 1, "lua_gettop present");
        if (cb && cb->candidate_rva_count >= 1)
            CHECK((uint32_t)dt_parse_hex(cb->candidate_rvas[0]) == 0xc74050u,
                  "lua_gettop == 0xc74050 (got 0x%x)",
                  (uint32_t)dt_parse_hex(cb->candidate_rvas[0]));
    }
    {
        FIND_CB("luaL_loadbuffer");
        CHECK(cb != NULL && cb->candidate_rva_count >= 1, "luaL_loadbuffer present");
        if (cb && cb->candidate_rva_count >= 1)
            CHECK((uint32_t)dt_parse_hex(cb->candidate_rvas[0]) == 0xc7ad80u,
                  "luaL_loadbuffer == 0xc7ad80 (got 0x%x)",
                  (uint32_t)dt_parse_hex(cb->candidate_rvas[0]));
    }

    /* lua_pcall: deferral is the expected outcome. */
    {
        FIND_CB("lua_pcall");
        CHECK(cb != NULL, "lua_pcall present in cat_b");
        if (cb) {
            int has_candidate = cb->candidate_rva_count > 0;
            int has_deferral = cb->evidence[0] && strstr(cb->evidence, "deferred");
            CHECK(has_candidate || has_deferral,
                  "lua_pcall is candidate or honest deferral (cand=%d defer=%d)",
                  has_candidate, has_deferral);
        }
    }

    printf("\n[A1] =========================================\n");
    printf("[A1]  checks:   %d\n", g_checks);
    printf("[A1]  failures: %d\n", g_failures);
    if (g_failures == 0)
        printf("[A1]  RESULT: PASS \xe2\x80\x94 in-memory discovery matches Phase 0 oracle.\n");
    else
        printf("[A1]  RESULT: FAIL \xe2\x80\x94 see failures above.\n");
    printf("[A1] =========================================\n");

    free(r);
    return g_failures == 0 ? 0 : 10 + g_failures;
}
