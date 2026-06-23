/* discover_worker.c — Phase 2b worker thread: in-process discovery.
 *
 * Spawned by DllMain after Phase 1 forwarding is set up. Runs OUTSIDE
 * the loader lock (DllMain has returned by the time this thread executes).
 *
 * Responsibilities:
 *   1. Get the host process main module base via GetModuleHandleW(NULL).
 *      In the live game this is Darktide.exe. GetModuleHandleW(NULL) is
 *      loader-safe (returns the already-loaded EXE base; no loader
 *      interaction, no lock taken).
 *   2. Read SizeOfImage from the in-memory PE optional header.
 *   3. Heap-allocate a dt_result_t (3.4 MB — stack would blow).
 *   4. Call dt_discover(base, (const uint8_t*)base, size_of_image, result).
 *   5. Log each discovered address with a cross-check against the baked-in
 *      Phase 0 expected values (expected_addrs.h), marking MATCH/MISMATCH.
 *   6. Write the structured result to darktide-poc-discovery.json next to
 *      the DLL (for Phase 3 to consume).
 *   7. Free the result and exit the thread.
 *
 * LOADER-LOCK CONSTRAINT: this thread MUST NOT call LoadLibrary,
 * GetModuleHandle for unrelated modules, FreeLibrary, GetProcAddress, or
 * anything else that touches the Windows loader. The engine + capstone
 * are statically linked (no dynamic loading), and the only loader API we
 * touch is GetModuleHandleW(NULL) (safe). stdio file I/O (fopen/fwrite)
 * goes through msvcrt which is already mapped and does not load modules.
 *
 * No hooks, no function calls into the target, no Lua. Discovery + log only.
 */
#include "poc_log.h"
#include "expected_addrs.h"

#include "engine.h"
#include "util.h"

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

/* PE32+ optional-header field offsets, measured FROM e_lfanew (the PE
 * signature offset). The optional header begins at e_lfanew + 4 (PE sig)
 * + 20 (file header) = e_lfanew + 0x18, so:
 *   ImageBase    is at optional_header + 0x18 = e_lfanew + 0x30
 *   SizeOfImage  is at optional_header + 0x38 = e_lfanew + 0x50
 *
 * NOTE on the spec wording: the task brief says "SizeOfImage at offset
 * 0x50 from the optional header start". That value (0x50) is actually the
 * offset from e_lfanew; the offset from the optional header start is 0x38.
 * We use the from-e_lfanew values here, reading directly off `base`.
 * See engine/pe.c dt_optional_header_pe32plus_t for the packed layout. */
#define OFF_FROM_LFANEW_IMAGE_BASE     0x30u   /* uint64 (PE32+) */
#define OFF_FROM_LFANEW_SIZE_OF_IMAGE  0x50u   /* uint32 */

/* ------------------------------------------------------------------- */
/* Helpers: look up discovered addresses in the result struct.          */
/* ------------------------------------------------------------------- */

static const dt_cat_b_t *find_cat_b(const dt_result_t *r, const char *name) {
    for (int i = 0; i < r->cat_b_count; ++i)
        if (strcmp(r->cat_b[i].name, name) == 0) return &r->cat_b[i];
    return NULL;
}

/* Resolve a discovered RVA by label. Returns 1 and writes *out on success,
 * 0 if the field couldn't be resolved (discovery didn't reach that phase). */
static int resolve_discovered(const dt_result_t *r, const char *label,
                              uint32_t *out) {
    if (strcmp(label, "lua_panic_body") == 0) {
        if (!r->init.found) return 0;
        *out = r->init.lua_panic_body_rva;
        return r->init.lua_panic_body_rva != 0;
    }
    if (strcmp(label, "init_begin") == 0) {
        if (!r->init.found) return 0;
        *out = r->init.begin;
        return r->init.begin != 0;
    }
    if (strcmp(label, "lua_newstate_thunk") == 0) {
        const dt_cat_b_t *c = find_cat_b(r, "lua_newstate");
        if (!c || !c->thunk_entry_rva[0]) return 0;
        *out = (uint32_t)dt_parse_hex(c->thunk_entry_rva);
        return *out != 0;
    }
    if (strcmp(label, "lua_newstate_body") == 0) {
        const dt_cat_b_t *c = find_cat_b(r, "lua_newstate");
        if (!c || !c->real_body_rva[0]) return 0;
        *out = (uint32_t)dt_parse_hex(c->real_body_rva);
        return *out != 0;
    }
    /* lua_atpanic / lua_gettop / luaL_loadbuffer: first candidate RVA. */
    const char *cb_name = NULL;
    if      (strcmp(label, "lua_atpanic")     == 0) cb_name = "lua_atpanic";
    else if (strcmp(label, "lua_gettop")      == 0) cb_name = "lua_gettop";
    else if (strcmp(label, "luaL_loadbuffer") == 0) cb_name = "luaL_loadbuffer";
    if (cb_name) {
        const dt_cat_b_t *c = find_cat_b(r, cb_name);
        if (!c || c->candidate_rva_count == 0 || !c->candidate_rvas[0][0])
            return 0;
        *out = (uint32_t)dt_parse_hex(c->candidate_rvas[0]);
        return *out != 0;
    }
    return 0;
}

/* Write the JSON next to the DLL. Returns 1 on success, 0 on failure. */
static int write_discovery_json(HMODULE self, const dt_result_t *r) {
    (void)self;
    WCHAR dir[MAX_PATH];
    size_t dlen = poc_log_dir(dir, MAX_PATH);
    if (dlen == 0) return 0;
    static const WCHAR name[] = L"darktide-poc-discovery.json";
    size_t name_chars = (sizeof(name) / sizeof(WCHAR)) - 1;
    if (dlen + name_chars + 1 > MAX_PATH) return 0;

    WCHAR path[MAX_PATH];
    for (size_t k = 0; k < dlen; ++k) path[k] = dir[k];
    for (size_t k = 0; k <= name_chars; ++k) path[dlen + k] = name[k];

    /* fopen on a wide path: _wfopen is in msvcrt (already mapped; no
     * loader interaction). */
    FILE *f = _wfopen(path, L"wb");
    if (!f) return 0;
    int rc = dt_write_json(r, f);
    fclose(f);
    return rc == 0;
}

/* ------------------------------------------------------------------- */
/* Worker thread entry point.                                           */
/* ------------------------------------------------------------------- */

typedef struct {
    HMODULE self;   /* our DLL handle, for path resolution */
} worker_ctx_t;

DWORD WINAPI discover_worker(LPVOID param) {
    worker_ctx_t *wc = (worker_ctx_t *)param;
    HMODULE self = wc->self;
    /* wc was allocated by the spawner; free it here. */
    free(wc);

    /* 1. Main module base. GetModuleHandleW(NULL) returns the host EXE
     *    base (Darktide.exe in the live game). It does NOT take the
     *    loader lock — the EXE is already mapped. */
    HMODULE main_mod = GetModuleHandleW(NULL);
    if (!main_mod) {
        poc_log_linef("discover ABORT GetModuleHandleW(NULL) failed err=%lu",
                      GetLastError());
        return 1;
    }
    uintptr_t base = (uintptr_t)main_mod;

    /* 2. Read SizeOfImage from the in-memory PE optional header. */
    const uint8_t *base_bytes = (const uint8_t *)base;
    uint32_t e_lfanew = *(const uint32_t *)(base_bytes + 0x3C);
    if (e_lfanew == 0 || e_lfanew > (1u << 20)) {
        poc_log_linef("discover ABORT implausible e_lfanew=0x%x", e_lfanew);
        return 1;
    }
    /* Validate the PE signature before trusting anything else. */
    if (memcmp(base_bytes + e_lfanew, "PE\0\0", 4) != 0) {
        poc_log_linef("discover ABORT PE signature missing at base+0x%x",
                      e_lfanew);
        return 1;
    }
    uint32_t size_of_image =
        *(const uint32_t *)(base_bytes + e_lfanew + OFF_FROM_LFANEW_SIZE_OF_IMAGE);
    uint64_t image_base =
        *(const uint64_t *)(base_bytes + e_lfanew + OFF_FROM_LFANEW_IMAGE_BASE);

    poc_log_linef("discover start base=0x%llx size_of_image=0x%x image_base=0x%llx",
                  (unsigned long long)base, size_of_image,
                  (unsigned long long)image_base);

    if (size_of_image == 0 || size_of_image > (256u << 20)) {
        poc_log_linef("discover ABORT implausible SizeOfImage=0x%x",
                      size_of_image);
        return 1;
    }

    /* 3. Heap-allocate the result (3.4 MB — never stack-allocate). */
    dt_result_t *result = (dt_result_t *)calloc(1, sizeof(dt_result_t));
    if (!result) {
        poc_log_linef("discover ABORT calloc(%zu) failed", sizeof(dt_result_t));
        return 1;
    }

    /* 4. Run discovery against the live in-memory module. The engine is
     *    IAT-value-independent and reads only RVAs, so the fact that the
     *    loader fixed up the IAT (and zeroed section padding) is harmless. */
    int rc = dt_discover((const uint8_t *)base, size_of_image, image_base,
                         result);
    if (rc != 0) {
        poc_log_linef("discover ABORT dt_discover rc=%d", rc);
        free(result);
        return 1;
    }

    /* 5. Cross-check each discovered address against the baked-in Phase 0
     *    expected values, logging MATCH or MISMATCH. */
    int matched = 0, mismatched = 0, unresolved = 0;
    for (size_t i = 0; i < kExpectedAddrsCount; ++i) {
        const expected_addr_t *e = &kExpectedAddrs[i];
        uint32_t got = 0;
        int found = resolve_discovered(result, e->label, &got);
        if (!found) {
            ++unresolved;
            poc_log_linef("discover %s expected=0x%x UNRESOLVED",
                          e->label, e->expected);
        } else if (got == e->expected) {
            ++matched;
            poc_log_linef("discover %s rva=0x%x expected=0x%x MATCH",
                          e->label, got, e->expected);
        } else {
            ++mismatched;
            poc_log_linef("discover %s rva=0x%x expected=0x%x MISMATCH",
                          e->label, got, e->expected);
        }
    }

    /* lua_pcall: log the clustering outcome verbatim (deferred or candidate).
     * Not a MATCH/MISMATCH — Phase 0 also deferred this. */
    const char *pcall_outcome = "unknown";
    const dt_cat_b_t *pc = find_cat_b(result, "lua_pcall");
    if (pc) {
        pcall_outcome = (pc->candidate_rva_count > 0) ? "candidate" : "deferred";
    }
    poc_log_linef("discover lua_pcall outcome=%s summary=%s",
                  pcall_outcome, result->pcall_summary);

    /* Summary line: the one the RUNBOOK tells the user to grep for. */
    poc_log_linef("discover summary matched=%d mismatched=%d unresolved=%d pcall=%s",
                  matched, mismatched, unresolved, pcall_outcome);

    /* 6. Write the structured JSON for Phase 3 to consume. */
    int json_ok = write_discovery_json(self, result);
    poc_log_linef("discover json %s", json_ok ? "written" : "FAILED");

    /* 7. Free and exit. */
    free(result);
    poc_log_linef("discover done");
    return 0;
}
