/*
 * test/a1_inject_test.c — Phase 5 Step 3 Tier A1: C-function bootstrap test.
 *
 * This is the STRONG GATE for Phase 5 Step 3 (the C-function bootstrap
 * approach) and the cumulative gate for Phase 4 (lua_pcall execution).
 * It links the EXACT SAME inject.c source the production DLL uses — but
 * compiled with -DPHASE4_TEST_API. The test wires up the function pointers
 * via inject_test_setup() + inject_test_set_cfunc_bootstrappers() (no
 * MinHook needed); calling p_lua_pcall inside do_inject re-enters
 * detour_lua_pcall, faithfully mirroring what MinHook's patched-target
 * jump does in production.
 *
 * Proves, before risking the live game:
 *
 *   (a) The detour's C-function bootstrap + loadbuffer + pcall sequence
 *       works against a real LuaJIT VM.
 *   (b) [NEW Phase 5 Step 3] lua_pushcclosure + lua_setfield called from
 *       inside the detour actually registers our poc_print C function as
 *       a Lua global — so a VM created WITHOUT openlibs gains `poc_print`
 *       after the detour runs.
 *   (c) The reentry guard prevents infinite recursion when the injected
 *       pcall re-enters the hook (the #1 risk per the brief).
 *   (d) The retry-on-error latch is set only on success (no retry after
 *       a successful injection).
 *   (e) [NEW] The retry-on-error mechanism is preserved when the
 *       C-function bootstrap is unavailable — the chunk errors (pcall_rc=2,
 *       `poc_print` is nil), the stack is cleaned up, and the latch is
 *       NOT set (retry happens).
 *   (f) The lua_pcall / luaL_openlibs / lua_pushcclosure / lua_setfield
 *       RVAs baked into inject.c actually point at those functions in the
 *       game binary — verified by disassembling Darktide.exe at those RVAs
 *       and matching each function's source-compiled shape.
 *
 * The test has FOUR phases:
 *
 *   Phase 1 — open-libs VM + bootstrap wired (the happy path):
 *     Globals already present (luaL_openlibs called) AND our bootstrap
 *     registers poc_print. The chunk succeeds on the first attempt.
 *     Baseline validation that the new chunk + bootstrap don't break
 *     anything when openlibs is also available.
 *
 *   Phase 2 — no-libs VM + bootstrap wired (registration works on a
 *             sandboxed VM, key validation):
 *     Creates a fresh VM WITHOUT luaL_openlibs (no globals at all). The
 *     detour's bootstrap registers poc_print via the C API — then the
 *     chunk succeeds BECAUSE our registration provides poc_print. This is
 *     the offline proof that the C-function bootstrap bypasses the
 *     sandboxed _G.
 *
 *   Phase 3 — no-libs VM + bootstrap wired + openlibs NOT wired
 *             (registration is self-sufficient — production scenario):
 *     Same as Phase 2 but with the openlibs pointer NULL. Validates that
 *     the bootstrap works WITHOUT any help from openlibs — which is the
 *     production scenario (openlibs is disabled in production because
 *     it's destructive).
 *
 *   Phase 4 — no-libs VM + bootstrap DISABLED (negative control +
 *             retry-on-error preserved):
 *     Creates a fresh VM WITHOUT openlibs AND disables the bootstrap
 *     (NULL pointers). The chunk errors (poc_print is nil → LUA_ERRRUN),
 *     the stack is cleaned up (no leak), the latch is NOT set, and retry
 *     happens on the next engine pcall. This is the negative control that
 *     proves the chunk actually depends on our registration (not on some
 *     other mechanism), AND preserves the Phase 4 retry-on-error coverage.
 *
 * Exit codes:
 *   0  all assertions pass
 *   1  LuaJIT state setup failed
 *   2  one or more assertions failed
 */
#include <lua.h>
#include <lualib.h>
#include <lauxlib.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

#include "inject.h"
#include "disasm_check.h"
#include "expected_addrs.h"   /* EXPECT_LUA_PCALL, EXPECT_LUA_PUSHCCLOSURE, etc. */

/* ---- Test runner ----------------------------------------------------- */
static int g_failures = 0;
static int g_checks   = 0;

#define CHECK(cond, ...) do { \
    ++g_checks; \
    if (!(cond)) { ++g_failures; printf("  FAIL: "); printf(__VA_ARGS__); printf("\n"); } \
    else        {             printf("  ok:   "); printf(__VA_ARGS__); printf("\n"); } \
} while (0)

static unsigned long long test_now_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (unsigned long long)ts.tv_sec * 1000ULL +
           (unsigned long long)ts.tv_nsec / 1000000ULL;
}

/* A trivial engine-simulating chunk (no-op). Pushes a function on the
 * stack that the engine's pcall would consume. Doesn't reference any
 * globals, so it runs fine even in a no-libs VM. */
static const char *ENGINE_SIM = "return\n";

/* Helper: load the engine-sim chunk onto L's stack. Aborts the test on
 * failure (can't proceed without a chunk for the engine's pcall). */
static int push_engine_sim(lua_State *L) {
    if (luaL_loadbuffer(L, ENGINE_SIM, strlen(ENGINE_SIM), "[engine-sim]") != 0) {
        printf("[A1] FAIL: engine-sim chunk failed to load: %s\n",
               lua_tostring(L, -1));
        lua_pop(L, 1);
        return 1;
    }
    return 0;
}

/* Detect the lua_State::top offset for THIS LuaJIT build. The production
 * DLL hardcodes 0x18 (Darktide binary's non-GC64 LuaJIT, confirmed by
 * lua_pcall disasm). The system LuaJIT used by A1 may be GC64 (different
 * struct layout — e.g., LuaJIT 2.1.1780076327 on x86_64 has top at 0x28).
 *
 * Method: snapshot the first 128 bytes of the lua_State, push one value
 * (which advances L->top by sizeof(TValue)=8), then find which 8-byte-
 * aligned slot increased by exactly 8. That slot is L->top. */
static size_t detect_L_top_offset(lua_State *L) {
    lua_settop(L, 0);   /* clean stack */
    uint8_t *p = (uint8_t *)L;
    uint64_t before[16];
    for (int i = 0; i < 16; i++) before[i] = *(uint64_t *)(p + i * 8);
    lua_pushinteger(L, 1);
    size_t found = (size_t)-1;
    for (int i = 0; i < 16; i++) {
        uint64_t after = *(uint64_t *)(p + i * 8);
        if (after == before[i] + 8) { found = (size_t)(i * 8); break; }
    }
    lua_settop(L, 0);   /* pop */
    return found;
}

/* =====================================================================*
 *  Phase 1 — open-libs VM + bootstrap wired (the happy path)
 * =====================================================================*
 * luaL_newstate + luaL_openlibs → globals already present. The detour's
 * bootstrap registers poc_print. The chunk succeeds on the first attempt.
 *
 * Validates:
 *   - load_rc=0, pcall_rc=0
 *   - inject_count=1 (one attempt)
 *   - max_depth=2 (reentry guard caught the recursion)
 *   - cfuncs_registered=1 (bootstrap latch set)
 *   - poc_print_calls >= 1 (the C function actually fired from Lua)
 *   - latch set (no retry on 2nd call)
 */
static void run_phase1(void) {
    printf("\n[A1] --- Phase 1: open-libs VM + bootstrap wired "
           "(chunk should succeed) ---\n");

    lua_State *L = luaL_newstate();
    if (!L) {
        printf("[A1] FAIL: luaL_newstate returned NULL\n");
        ++g_failures;
        return;
    }
    luaL_openlibs(L);
    printf("[A1] OK: LuaJIT state created, libs opened\n");

    /* Wire up inject.c's function pointers. openlibs DISABLED here
     * (Phase 1 already opened libs manually; the detour's openlibs call
     * would be a no-op anyway). */
    inject_test_setup((void *)&lua_pcall, (void *)&luaL_loadbuffer,
                      /*real_openlibs=*/NULL,
                      (void *)L, /*captured_tick=*/test_now_ms() - 5000);
    /* Wire the C-function bootstrap pointers (ENABLED). */
    inject_test_set_cfunc_bootstrappers((void *)&lua_pushcclosure,
                                         (void *)&lua_setfield);
    printf("[A1] OK: inject_test_setup wired (openlibs=DISABLED, "
           "cfunc_bootstrap=ENABLED, min_interval_ms=%d)\n",
           PHASE4_INJECT_DELAY_MS);

    /* Load the engine-sim chunk (what the engine's pcall would consume). */
    if (push_engine_sim(L)) { lua_close(L); ++g_failures; return; }
    printf("[A1] OK: engine-sim chunk loaded on the Lua stack\n");

    /* Simulate the engine calling lua_pcall via our detour. */
    printf("[A1] invoking detour_lua_pcall(L, 0, 0, 0) ...\n");
    int engine_rc = detour_lua_pcall(L, 0, 0, 0);
    CHECK(engine_rc == 0,
          "engine-sim pcall returned 0 (got %d)", engine_rc);

    /* Verify the injection happened. */
    int load_rc        = inject_test_last_load_rc();
    int pcall_rc       = inject_test_last_pcall_rc();
    int count          = inject_test_inject_count();
    int depth          = inject_test_detour_depth();
    int cfuncs_reg     = inject_test_cfuncs_registered();
    int poc_print_calls = inject_test_poc_print_calls();

    printf("[A1] detour results: load_rc=%d pcall_rc=%d inject_count=%d "
           "max_depth=%d cfuncs_registered=%d poc_print_calls=%d\n",
           load_rc, pcall_rc, count, depth, cfuncs_reg, poc_print_calls);

    CHECK(cfuncs_reg == 1,
          "cfunc bootstrap latch set after detour (got %d)", cfuncs_reg);
    CHECK(load_rc == 0,
          "injected loadbuffer returned 0 (got %d)", load_rc);
    CHECK(pcall_rc == 0,
          "injected pcall returned 0 = success (got %d)", pcall_rc);
    CHECK(count == 1,
          "exactly one injection attempt (got %d)", count);
    CHECK(depth == 2,
          "detour reached depth 2 (outer + injected reentry), no infinite "
          "recursion (got %d)", depth);
    CHECK(poc_print_calls >= 1,
          "poc_print C function actually fired from Lua (got %d calls)",
          poc_print_calls);

    /* Latch set on success: a second engine call must NOT re-inject. */
    printf("[A1] calling detour again to verify latch (success -> no retry)...\n");
    int count_before = inject_test_inject_count();
    int calls_before = inject_test_poc_print_calls();
    int reg_before   = inject_test_cfuncs_registered();
    if (push_engine_sim(L)) { lua_close(L); ++g_failures; return; }
    detour_lua_pcall(L, 0, 0, 0);
    int count_after = inject_test_inject_count();
    int calls_after = inject_test_poc_print_calls();
    int reg_after   = inject_test_cfuncs_registered();
    CHECK(count_after == count_before,
          "latch after success: inject_count unchanged after 2nd call "
          "(before=%d after=%d)", count_before, count_after);
    CHECK(reg_after == reg_before,
          "cfunc bootstrap latch still set after 2nd call (before=%d after=%d)",
          reg_before, reg_after);
    CHECK(calls_after == calls_before,
          "poc_print not re-called after latch set (before=%d after=%d) "
          "[bootstrap is one-shot]", calls_before, calls_after);

    lua_close(L);
}

/* =====================================================================*
 *  Phase 2 — no-libs VM + bootstrap wired (key validation)
 * =====================================================================*
 * Creates a fresh lua_State WITHOUT luaL_openlibs (no globals at all).
 * The detour's bootstrap registers poc_print via the C API — then the
 * chunk succeeds BECAUSE our registration provides poc_print.
 *
 * This is the offline proof that the C-function bootstrap bypasses the
 * sandboxed _G: we register our own implementation via the C API, NOT
 * dependent on whatever the engine did to _G.
 *
 * Validates:
 *   - cfuncs_registered=1 (bootstrap latch set, registration ran)
 *   - load_rc=0, pcall_rc=0 (chunk succeeded BECAUSE we registered poc_print)
 *   - poc_print_calls >= 1 (the C function fired from Lua)
 *   - stack clean (no leak)
 */
static void run_phase2(void) {
    printf("\n[A1] --- Phase 2: no-libs VM + bootstrap wired "
           "(registration should bypass the sandbox) ---\n");

    /* Reset all detour state so Phase 2 starts fresh. */
    inject_test_reset();

    lua_State *L = luaL_newstate();
    if (!L) {
        printf("[A1] FAIL: luaL_newstate returned NULL\n");
        ++g_failures;
        return;
    }
    /* NO luaL_openlibs — no globals at all (no print, no io, no error). */
    printf("[A1] OK: LuaJIT state created (libs NOT opened)\n");

    /* Wire up inject.c's function pointers. openlibs DISABLED. */
    inject_test_setup((void *)&lua_pcall, (void *)&luaL_loadbuffer,
                      /*real_openlibs=*/NULL,
                      (void *)L, /*captured_tick=*/test_now_ms() - 5000);
    /* C-function bootstrap ENABLED. */
    inject_test_set_cfunc_bootstrappers((void *)&lua_pushcclosure,
                                         (void *)&lua_setfield);
    printf("[A1] OK: inject_test_setup wired (openlibs=DISABLED, "
           "cfunc_bootstrap=ENABLED)\n");

    int top0 = lua_gettop(L);   /* 0 on a fresh state */

    if (push_engine_sim(L)) { lua_close(L); ++g_failures; return; }

    printf("[A1] invoking detour_lua_pcall(L, 0, 0, 0) against no-libs VM "
           "(bootstrap wired)...\n");
    int engine_rc = detour_lua_pcall(L, 0, 0, 0);
    CHECK(engine_rc == 0,
          "engine-sim pcall still succeeds (got %d)", engine_rc);

    int load_rc        = inject_test_last_load_rc();
    int pcall_rc       = inject_test_last_pcall_rc();
    int count          = inject_test_inject_count();
    int cfuncs_reg     = inject_test_cfuncs_registered();
    int poc_print_calls = inject_test_poc_print_calls();
    int top1           = lua_gettop(L);

    printf("[A1] detour results: load_rc=%d pcall_rc=%d inject_count=%d "
           "cfuncs_registered=%d poc_print_calls=%d stack_top=%d (was %d)\n",
           load_rc, pcall_rc, count, cfuncs_reg, poc_print_calls, top1, top0);

    CHECK(cfuncs_reg == 1,
          "cfunc bootstrap latch set - registration ran from inside detour "
          "(got %d)", cfuncs_reg);
    CHECK(load_rc == 0,
          "loadbuffer succeeded - chunk is syntactically valid (got %d)",
          load_rc);
    CHECK(pcall_rc == 0,
          "pcall returned 0 - chunk succeeded because we registered "
          "poc_print via the C API, bypassing the sandboxed _G (got %d)",
          pcall_rc);
    CHECK(count == 1,
          "one attempt made (got %d)", count);
    CHECK(poc_print_calls >= 1,
          "poc_print C function actually fired from Lua on a no-libs VM "
          "(got %d calls) - this proves the bootstrap bypasses _G",
          poc_print_calls);
    CHECK(top1 == top0,
          "stack clean after success - no leak (expected %d, got %d)",
          top0, top1);

    lua_close(L);
}

/* =====================================================================*
 *  Phase 3 — no-libs VM + bootstrap wired + openlibs NOT wired
 *            (registration is self-sufficient — production scenario)
 * =====================================================================*
 * Identical to Phase 2 (openlibs was already disabled there), but states
 * the production-equivalent scenario explicitly: the engine's _G is
 * sandboxed AND openlibs is destructive (so we cannot call it). Our
 * C-function bootstrap is the SOLE mechanism providing `poc_print` to
 * the chunk. Verifies the chunk + registration work in exactly the
 * configuration production will use.
 *
 * (Phase 2 and Phase 3 are functionally identical given how inject_test_setup
 * is configured, but they document different intents: Phase 2 = "bootstrap
 * works", Phase 3 = "bootstrap is sufficient without openlibs". Kept
 * separate for clarity in the test output. Reuses the same wiring.)
 */
static void run_phase3(void) {
    printf("\n[A1] --- Phase 3: no-libs VM + bootstrap wired + openlibs "
           "NOT wired (production scenario) ---\n");

    inject_test_reset();

    lua_State *L = luaL_newstate();
    if (!L) {
        printf("[A1] FAIL: luaL_newstate returned NULL\n");
        ++g_failures;
        return;
    }
    printf("[A1] OK: LuaJIT state created (libs NOT opened)\n");

    /* Production wiring: openlibs=NULL (destructive, disabled in prod),
     * C-function bootstrap ENABLED. */
    inject_test_setup((void *)&lua_pcall, (void *)&luaL_loadbuffer,
                      /*real_openlibs=*/NULL,
                      (void *)L, /*captured_tick=*/test_now_ms() - 5000);
    inject_test_set_cfunc_bootstrappers((void *)&lua_pushcclosure,
                                         (void *)&lua_setfield);
    printf("[A1] OK: production-equivalent wiring "
           "(openlibs=NULL [disabled], cfunc_bootstrap=ENABLED)\n");

    int top0 = lua_gettop(L);

    if (push_engine_sim(L)) { lua_close(L); ++g_failures; return; }

    printf("[A1] invoking detour_lua_pcall(L, 0, 0, 0) ...\n");
    int engine_rc = detour_lua_pcall(L, 0, 0, 0);
    CHECK(engine_rc == 0,
          "engine-sim pcall still succeeds (got %d)", engine_rc);

    int load_rc        = inject_test_last_load_rc();
    int pcall_rc       = inject_test_last_pcall_rc();
    int count          = inject_test_inject_count();
    int cfuncs_reg     = inject_test_cfuncs_registered();
    int poc_print_calls = inject_test_poc_print_calls();
    int top1           = lua_gettop(L);

    printf("[A1] detour results: load_rc=%d pcall_rc=%d inject_count=%d "
           "cfuncs_registered=%d poc_print_calls=%d stack_top=%d (was %d)\n",
           load_rc, pcall_rc, count, cfuncs_reg, poc_print_calls, top1, top0);

    CHECK(cfuncs_reg == 1,
          "cfunc bootstrap latch set (got %d)", cfuncs_reg);
    CHECK(load_rc == 0,
          "loadbuffer succeeded (got %d)", load_rc);
    CHECK(pcall_rc == 0,
          "pcall returned 0 - bootstrap is self-sufficient without openlibs "
          "(got %d)", pcall_rc);
    CHECK(poc_print_calls >= 1,
          "poc_print fired from Lua (got %d calls)", poc_print_calls);
    CHECK(top1 == top0,
          "stack clean after success - no leak (expected %d, got %d)",
          top0, top1);

    lua_close(L);
}

/* =====================================================================*
 *  Phase 4 — no-libs VM + bootstrap DISABLED (negative control +
 *            retry-on-error preserved)
 * =====================================================================*
 * Creates a fresh VM WITHOUT luaL_openlibs AND disables the C-function
 * bootstrap (NULL pointers). This is the negative control that proves:
 *
 *   1. The chunk actually depends on our registration (not on some other
 *      mechanism). With registration disabled, `poc_print` is nil →
 *      "attempt to call a nil value" → lua_pcall returns LUA_ERRRUN (2).
 *   2. The retry-on-error mechanism (Phase 4 rev 3) is preserved when
 *      the bootstrap is unavailable. The latch is NOT set on failure;
 *      the next engine pcall triggers another attempt.
 *
 * Validates:
 *   - cfuncs_registered=0 (bootstrap disabled, skipped)
 *   - pcall_rc=2 (LUA_ERRRUN - poc_print is nil)
 *   - load_rc=0 (chunk is syntactically valid)
 *   - poc_print_calls=0 (the C function never ran - Lua never reached it)
 *   - stack cleaned up (gettop same before and after - no leak)
 *   - latch NOT set (retry happens on next engine pcall -> count increments)
 */
static void run_phase4(void) {
    printf("\n[A1] --- Phase 4: no-libs VM + bootstrap DISABLED "
           "(chunk should fail + retry) ---\n");

    inject_test_reset();

    lua_State *L = luaL_newstate();
    if (!L) {
        printf("[A1] FAIL: luaL_newstate returned NULL\n");
        ++g_failures;
        return;
    }
    printf("[A1] OK: LuaJIT state created (libs NOT opened)\n");

    /* Disable BOTH openlibs and the C-function bootstrap. */
    inject_test_setup((void *)&lua_pcall, (void *)&luaL_loadbuffer,
                      /*real_openlibs=*/NULL,
                      (void *)L, /*captured_tick=*/test_now_ms() - 5000);
    inject_test_set_cfunc_bootstrappers(/*pushcclosure=*/NULL,
                                         /*setfield=*/NULL);
    printf("[A1] OK: inject_test_setup wired (openlibs=DISABLED, "
           "cfunc_bootstrap=DISABLED)\n");

    int top0 = lua_gettop(L);

    if (push_engine_sim(L)) { lua_close(L); ++g_failures; return; }

    printf("[A1] invoking detour_lua_pcall(L, 0, 0, 0) against no-libs VM "
           "(bootstrap disabled)...\n");
    int engine_rc = detour_lua_pcall(L, 0, 0, 0);
    CHECK(engine_rc == 0,
          "engine-sim pcall still succeeds (got %d)", engine_rc);

    int load_rc        = inject_test_last_load_rc();
    int pcall_rc       = inject_test_last_pcall_rc();
    int count          = inject_test_inject_count();
    int cfuncs_reg     = inject_test_cfuncs_registered();
    int poc_print_calls = inject_test_poc_print_calls();
    int top1           = lua_gettop(L);

    printf("[A1] detour results: load_rc=%d pcall_rc=%d inject_count=%d "
           "cfuncs_registered=%d poc_print_calls=%d stack_top=%d (was %d)\n",
           load_rc, pcall_rc, count, cfuncs_reg, poc_print_calls, top1, top0);

    CHECK(cfuncs_reg == 0,
          "cfunc bootstrap NOT registered - pointers were NULL (got %d)",
          cfuncs_reg);
    CHECK(load_rc == 0,
          "no-bootstrap: loadbuffer succeeded - chunk is syntactically valid "
          "(got %d)", load_rc);
    CHECK(pcall_rc == 2,
          "no-bootstrap: pcall returned LUA_ERRRUN=2 - poc_print is nil "
          "(got %d)", pcall_rc);
    CHECK(count == 1,
          "no-bootstrap: one attempt made (got %d)", count);
    CHECK(poc_print_calls == 0,
          "no-bootstrap: poc_print NEVER fired (got %d calls) - Lua never "
          "reached the C function body", poc_print_calls);
    CHECK(top1 == top0,
          "no-bootstrap: stack clean after failed attempt - no leak "
          "(expected %d, got %d)", top0, top1);

    /* Retry: call detour again. The latch should NOT be set (failure ->
     * retry), so inject_count increments. */
    printf("[A1] calling detour again to verify retry on failure...\n");
    int count_before_retry = inject_test_inject_count();
    if (push_engine_sim(L)) { lua_close(L); ++g_failures; return; }
    int engine_rc2 = detour_lua_pcall(L, 0, 0, 0);
    int count_after_retry  = inject_test_inject_count();
    int top2               = lua_gettop(L);
    int pcall_rc2          = inject_test_last_pcall_rc();

    CHECK(engine_rc2 == 0,
          "no-bootstrap retry: engine-sim pcall still succeeds (got %d)",
          engine_rc2);
    CHECK(count_after_retry == count_before_retry + 1,
          "no-bootstrap retry: inject_count incremented - latch NOT set on "
          "failure (before=%d after=%d)", count_before_retry,
          count_after_retry);
    CHECK(pcall_rc2 == 2,
          "no-bootstrap retry: pcall still returns LUA_ERRRUN=2 (got %d)",
          pcall_rc2);
    CHECK(top2 == top0,
          "no-bootstrap retry: stack still clean after 2nd failed attempt "
          "(expected %d, got %d)", top0, top2);

    lua_close(L);
}

/* =====================================================================*/
int main(void) {
    printf("[A1] Phase 5 Step 3 C-function bootstrap test\n");
    printf("[A1] libluajit: %s\n", LUA_RELEASE);

    /* 0a. Offline binary-shape verification: the lua_pcall RVA baked into
     *     inject.c must point at lua_pcall in Darktide.exe. (Phase 4 check,
     *     retained.) */
    const char *pe_path = getenv("DARKTIDE_EXE");
    printf("[A1] verifying lua_pcall RVA 0x%x in %s ...\n",
           EXPECT_LUA_PCALL, pe_path ? pe_path : "(default Darktide.exe path)");
    int disasm_rc = disasm_check_lua_pcall(pe_path, EXPECT_LUA_PCALL);
    CHECK(disasm_rc == 0,
          "Darktide.exe @ 0x%x matches lua_pcall source pattern (got rc=%d)",
          EXPECT_LUA_PCALL, disasm_rc);

    /* 0b. Offline verification: luaL_openlibs RVA (retained even though
     *     openlibs is disabled in production — the address is still baked
     *     in for diagnostic purposes and must be correct). */
    printf("[A1] verifying luaL_openlibs RVA 0x%x in %s ...\n",
           EXPECT_LUAL_OPENLIBS, pe_path ? pe_path : "(default Darktide.exe path)");
    int disasm_rc2 = disasm_check_luaL_openlibs(pe_path, EXPECT_LUAL_OPENLIBS);
    CHECK(disasm_rc2 == 0,
          "Darktide.exe @ 0x%x matches luaL_openlibs source pattern "
          "(got rc=%d)", EXPECT_LUAL_OPENLIBS, disasm_rc2);

    /* 0c. [NEW Phase 5 Step 3] Offline verification: lua_pushcclosure RVA
         (what we call to push a C function — `lua_pushcfunction` is a
         macro for this). */
    printf("[A1] verifying lua_pushcclosure RVA 0x%x in %s ...\n",
           EXPECT_LUA_PUSHCCLOSURE, pe_path ? pe_path : "(default Darktide.exe path)");
    int disasm_rc3 = disasm_check_lua_pushcclosure(pe_path, EXPECT_LUA_PUSHCCLOSURE);
    CHECK(disasm_rc3 == 0,
          "Darktide.exe @ 0x%x matches lua_pushcclosure source pattern "
          "(got rc=%d)", EXPECT_LUA_PUSHCCLOSURE, disasm_rc3);

    /* 0d. [NEW Phase 5 Step 3] Offline verification: lua_setfield RVA
         (what we call to register a global — `lua_setglobal` is a macro
         for lua_setfield(L, LUA_GLOBALSINDEX, name)). */
    printf("[A1] verifying lua_setfield RVA 0x%x in %s ...\n",
           EXPECT_LUA_SETFIELD, pe_path ? pe_path : "(default Darktide.exe path)");
    int disasm_rc4 = disasm_check_lua_setfield(pe_path, EXPECT_LUA_SETFIELD);
    CHECK(disasm_rc4 == 0,
          "Darktide.exe @ 0x%x matches lua_setfield source pattern "
          "(got rc=%d)", EXPECT_LUA_SETFIELD, disasm_rc4);

    /* Detect the L->top offset for this LuaJIT build. Production hardcodes
     * 0x18 (Darktide's non-GC64 LuaJIT); the system LuaJIT may differ. */
    {
        lua_State *probe = luaL_newstate();
        size_t top_off = detect_L_top_offset(probe);
        lua_close(probe);
        CHECK(top_off != (size_t)-1,
              "detected L->top offset in system LuaJIT (got 0x%zx)", top_off);
        if (top_off == (size_t)-1) {
            printf("[A1] FAIL: could not detect L->top offset - aborting\n");
            return g_failures == 0 ? 1 : 2;
        }
        printf("[A1] L->top offset: 0x%zx (system LuaJIT %s; production "
               "uses 0x18)\n", top_off, LUA_RELEASE);
        inject_test_set_L_top_offset(top_off);
    }

    /* Phase 1: open-libs VM + bootstrap wired -> chunk succeeds. */
    run_phase1();
    /* Phase 2: no-libs VM + bootstrap wired -> registration bypasses sandbox. */
    run_phase2();
    /* Phase 3: no-libs VM + bootstrap wired + openlibs NOT wired ->
     * registration is self-sufficient (production scenario). */
    run_phase3();
    /* Phase 4: no-libs VM + bootstrap DISABLED -> chunk fails + retries
     * (negative control + retry-on-error coverage). */
    run_phase4();

    printf("\n[A1] =========================================\n");
    printf("[A1]  checks:   %d\n", g_checks);
    printf("[A1]  failures: %d\n", g_failures);
    if (g_failures == 0)
        printf("[A1]  RESULT: PASS - C-function bootstrap + Lua execution + "
               "retry-on-error all proven against a real LuaJIT.\n");
    else
        printf("[A1]  RESULT: FAIL - see failures above.\n");
    printf("[A1] =========================================\n");

    return g_failures == 0 ? 0 : 2;
}
