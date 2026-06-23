/*
 * inject.c — Phase 4 implementation. See inject.h for the contract.
 *
 * Two compilation modes share this source:
 *
 *   1. Production (default): mingw PE-x86-64, links MinHook + poc_log.
 *      inject_install() creates a MinHook hook on lua_pcall; the engine
 *      triggers the detour on its own Lua thread.
 *
 *   2. Test (-DPHASE4_TEST_API): Linux native, links system LuaJIT.
 *      inject_test_setup() wires up the function pointers manually so
 *      calling p_lua_pcall re-enters detour_lua_pcall (mirroring the
 *      post-MinHook patched-target semantics). The test exercises the
 *      exact detour source against a real LuaJIT VM.
 *
 * Both modes share: g_inject_src, the detour, the reentry guard, the
 * retry-on-error latch logic, the min-interval rate limiter, the
 * max-retry cap, and do_inject's loadbuffer+pcall sequence (with stack
 * save/restore). So A1 passing means the production logic is correct.
 */
#include "inject.h"

/* =====================================================================*
 *  Portability layer
 * =====================================================================*/

#ifdef PHASE4_TEST_API
/* ---- Test build: Linux native, no windows.h ------------------------- */

#include <stdint.h>
#include <stddef.h>
#include <time.h>

typedef long LONG;
typedef unsigned long long ULONGLONG;

/* GCC/clang atomic builtins — same memory-barrier semantics as Win32
 * Interlocked ops. Single-threaded test in practice, but atomic for parity. */
static inline LONG p_atomic_cas(volatile LONG *dst, LONG exch, LONG cmp) {
    return __sync_val_compare_and_swap(dst, cmp, exch);
}
static inline LONG p_atomic_xchg(volatile LONG *dst, LONG val) {
    return __sync_lock_test_and_set(dst, val);
}
static inline LONG p_atomic_inc(volatile LONG *dst) {
    return __sync_add_and_fetch(dst, 1);
}
static inline LONG p_atomic_dec(volatile LONG *dst) {
    return __sync_sub_and_fetch(dst, 1);
}

static inline unsigned long long p_now_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (unsigned long long)ts.tv_sec * 1000ULL +
           (unsigned long long)ts.tv_nsec / 1000000ULL;
}

/* No-op logger. The test verifies behavior via inject_test_* accessors,
 * not by reading log output. */
static inline void poc_log_linef(const char *fmt, ...) { (void)fmt; }

#  define CAPTURED_L()     g_test_captured_L
#  define CAPTURED_TICK()  g_test_captured_tick

#else
/* ---- Production: mingw PE-x86-64, windows.h + MinHook + poc_log ----- */

#  include <windows.h>
#  include <stdint.h>
#  include <MinHook.h>
#  include "poc_log.h"
#  include "expected_addrs.h"   /* EXPECT_LUA_PCALL, EXPECT_LOADBUFFER, etc. — single source of truth */

/* Phase 3 owns the capture; read via its getters (declared in
 * phase3_hooks.h, but we forward-declare here to avoid a header
 * dependency from inject.h — the test build doesn't have phase3). */
void                  *phase3_get_captured_state(void);
unsigned long long    phase3_get_capture_tick(void);

#  define p_atomic_cas(dst, exch, cmp)  InterlockedCompareExchange((dst), (exch), (cmp))
#  define p_atomic_xchg(dst, val)       InterlockedExchange((dst), (val))
#  define p_atomic_inc(dst)             InterlockedIncrement((dst))
#  define p_atomic_dec(dst)             InterlockedDecrement((dst))
#  define p_now_ms()                    GetTickCount64()

#  define CAPTURED_L()     phase3_get_captured_state()
#  define CAPTURED_TICK()  phase3_get_capture_tick()

/* RVAs are sourced from expected_addrs.h so the bake-in is centralized
 * and the A1 disasm-check can verify the same constant against the
 * binary. See report.md for the per-function identification evidence. */
#  define RVA_LUA_PCALL          EXPECT_LUA_PCALL
#  define RVA_LUAL_LOADBUFFER    EXPECT_LOADBUFFER
#  define RVA_LUAL_OPENLIBS      EXPECT_LUAL_OPENLIBS
#  define RVA_LUA_PUSHCCLOSURE   EXPECT_LUA_PUSHCCLOSURE
#  define RVA_LUA_SETFIELD       EXPECT_LUA_SETFIELD

#endif  /* PHASE4_TEST_API */

/* LUA_GLOBALSINDEX — the pseudo-index for the global table in LuaJIT 2.1.
 * Used as the `idx` argument to lua_setfield to register a global.
 * (lua_setglobal is a macro: lua_setfield(L, LUA_GLOBALSINDEX, name).)
 * Value verified against the engine's init at 0x32a2a0, which uses this
 * exact constant 13 times for _G.<name> replacements. */
#define POC_LUA_GLOBALSINDEX  (-10002)

/* =====================================================================*
 *  Shared state
 * =====================================================================*/

/* Function pointer types — match Phase 3's verified 4-arg shapes. */
typedef int (*lua_pcall_t)(void *L, int nargs, int nresults, int errfunc);
typedef int (*luaL_loadbuffer_t)(void *L, const char *buf, size_t size,
                                     const char *name);
typedef void (*luaL_openlibs_t)(void *L);

/* Phase 5 Step 3 — C-function bootstrap function pointer types.
 *
 * NOTE: lua_pushcfunction and lua_setglobal are MACROS in lua.h, not
 * real functions. The actual symbols are:
 *   - lua_pushcclosure(L, f, n)   — 3 args; we call with n=0 (equivalent
 *                                    to lua_pushcfunction(L, f)).
 *   - lua_setfield(L, idx, k)     — 3 args; we call with idx=LUA_GLOBALSINDEX
 *                                    (-10002; equivalent to lua_setglobal(L, k)
 *                                    when called with a value on the stack top).
 * See report.md §"lua_pushcclosure / lua_setfield identification". */
typedef void (*lua_pushcclosure_t)(void *L, void *f, int n);
typedef void (*lua_setfield_t)(void *L, int idx, const char *k);

/* The injected Lua source — Phase 5 Step 3 C-function bootstrap chunk.
 *
 *   poc_print()
 *   return 42
 *
 * This chunk:
 *   - Calls `poc_print()` — our C function, registered as a Lua global
 *     via lua_pushcclosure + lua_setfield in the detour. It writes a
 *     fixed string to the log file (production) / increments a counter
 *     (test).
 *   - Returns 42 (deterministic return value).
 *
 * Outcomes:
 *   - If `poc_print` was registered correctly → the C function fires,
 *     a log line `[FROM LUA] poc_print called — Lua executed a C
 *     function!` appears, pcall_rc=0. SUCCESS.
 *   - If registration failed → `poc_print` is nil → "attempt to call a
 *     nil value" → pcall_rc=2 (LUA_ERRRUN). RETRY.
 *
 * The chunk uses exactly 1 global (`poc_print`) that we provide ourselves
 * via the C API. No dependency on the sandboxed `_G` at all — this is the
 * bootstrap trick that bypasses the engine's stripped global environment. */
const char g_inject_src[] =
    "poc_print()\n"
    "return 42\n";
const unsigned long long g_inject_src_len = sizeof(g_inject_src) - 1;

/* The "trampoline" — the saved original lua_pcall body.
 *   Production: filled by MinHook (points to the relocated prologue +
 *               jump-back to lua_pcall + patch-len).
 *   Test:       set by inject_test_setup to the real libluajit lua_pcall. */
static lua_pcall_t g_orig_pcall = NULL;

/* The patched target — calling this re-enters detour_lua_pcall.
 *   Production: base + RVA_LUA_PCALL (after MinHook patches its prologue).
 *   Test:       &detour_lua_pcall (set by inject_test_setup). */
static lua_pcall_t p_lua_pcall = NULL;

/* luaL_loadbuffer direct-call pointer (never hooked). */
static luaL_loadbuffer_t p_luaL_loadbuffer = NULL;

/* luaL_openlibs direct-call pointer (Phase 5 Step 2 — never hooked).
 * DISABLED in production (wired to NULL by inject_install — calling
 * openlibs on the engine's state is destructive, verified in live
 * testing). The A1 test may wire it to the real luaL_openlibs to
 * exercise the openlibs call path. */
static luaL_openlibs_t p_luaL_openlibs = NULL;

/* Phase 5 Step 3 — C-function bootstrap direct-call pointers (never
 * hooked). Wired by inject_install to base + RVA_LUA_PUSHCCLOSURE and
 * base + RVA_LUA_SETFIELD. The A1 test wires them via
 * inject_test_set_cfunc_bootstrappers() or leaves them NULL to disable
 * the bootstrap (negative control). */
static lua_pushcclosure_t p_lua_pushcclosure = NULL;
static lua_setfield_t     p_lua_setfield     = NULL;

/* Reentry guard: 1 while our loadbuffer+pcall is in flight. Breaks the
 * recursion when our injected pcall (via p_lua_pcall) re-enters the
 * detour. THIS IS THE #1 RISK PER THE BRIEF — set BEFORE we pcall,
 * cleared AFTER. Also set during the openlibs call (same reentrancy
 * pattern — openlibs MAY trigger nested pcall internally via
 * luaopen_package etc.). */
static volatile LONG g_injecting = 0;

/* Openlibs one-shot latch: 0 = not yet called on the captured state,
 * 1 = done. Set to 1 on the first qualifying detour entry (after the
 * capture check passes), never reset. The openlibs call happens at most
 * once per DLL lifetime. Subsequent detour entries skip it. */
static volatile LONG g_openlibs_called = 0;

/* Phase 5 Step 3 — C-function bootstrap one-shot latch: 0 = poc_print not
 * yet registered, 1 = done. Set to 1 on the first qualifying detour entry
 * (after the capture check passes), never reset in production. The
 * registration happens at most once per DLL lifetime. The A1 test resets
 * it between sub-phases via inject_test_reset_cfunc_bootstrap(). */
static volatile LONG g_cfuncs_registered = 0;

/* Phase 5 Step 3 — poc_print call counter (test observable).
 * In production, poc_print's effect is the log line. In the test build,
 * the log is a no-op, so we increment this counter as the observable.
 * The A1 test asserts it's > 0 after the chunk runs. */
#ifdef PHASE4_TEST_API
static volatile LONG g_test_poc_print_calls = 0;
#endif

/* Latch: 0 = keep trying, 1 = done (success OR gave up). Set ONLY on
 * success (pcall returned 0) or when the max-retry cap is hit. While 0,
 * each qualifying engine lua_pcall call triggers another attempt. */
static volatile LONG g_injected = 0;

/* Retry bookkeeping. g_attempt_count is the total number of loadbuffer+
 * pcall attempts made so far (bounded by PHASE4_MAX_INJECT_ATTEMPTS).
 * g_last_attempt_tick backs the min-interval rate limiter
 * (PHASE4_INJECT_DELAY_MS). */
static volatile LONG        g_attempt_count     = 0;
static unsigned long long   g_last_attempt_tick = 0;

#ifdef PHASE4_TEST_API
/* Test-only state. */
static void                   *g_test_captured_L       = NULL;
static unsigned long long      g_test_captured_tick    = 0;
static volatile LONG           g_test_detour_depth     = 0;
static volatile LONG           g_test_max_depth        = 0;
static int                     g_test_last_load_rc     = -1;
static int                     g_test_last_pcall_rc    = -1;
/* L->top offset for THIS LuaJIT build. Defaults to the Darktide binary's
 * offset (0x18); the test overrides it after auto-detecting against the
 * system LuaJIT (which may be GC64 and use a different layout). */
static size_t                  g_test_L_top_offset     = 0x18;
#endif

/* =====================================================================*
 *  poc_print — the C function we register as a Lua global
 * =====================================================================*
 * Phase 5 Step 3 — the C-function bootstrap POC.
 *
 * This is a Lua-callable C function (Lua C API calling convention:
 * `int (*)(lua_State*)`). It takes NO arguments and writes a fixed
 * string to the log file. Returns 0 (no Lua return values).
 *
 * We register this as the Lua global `poc_print` via:
 *   p_lua_pushcclosure(L, &poc_print, 0);   -- pushes the C closure
 *   p_lua_setfield(L, LUA_GLOBALSINDEX, "poc_print");  -- pops & sets _G
 *
 * Then the injected chunk `poc_print()` invokes it. The log line
 * `[FROM LUA] poc_print called — Lua executed a C function!` proves Lua
 * executed a C function we provided — bypassing the sandboxed `_G`.
 *
 * The function runs on the engine's Lua thread (inside our pcall detour).
 * It must be fast and side-effect-free from the engine's perspective.
 * Writing to a log file is fine (poc_log_linef opens, writes, closes —
 * no Lua state interaction, no loader calls).
 *
 * Reentry safety: poc_print does NOT call any Lua API (no pcall, no
 * loadbuffer). It just writes to a file. So the reentry guard is not
 * strictly needed here, but the registration (pushcclosure + setfield)
 * IS wrapped in g_injecting as a defensive measure.
 */
static int poc_print(void *L) {
    /* L is the lua_State* passed by the Lua C calling convention. We
     * don't dereference it — poc_print takes no args and pushes no
     * results, so L is unused. Using void* (not lua_State*) keeps this
     * file portable across the production build (MinHook + windows.h)
     * and the test build (-DPHASE4_TEST_API, no Lua headers). */
    (void)L;
#ifdef PHASE4_TEST_API
    /* Test observable: increment the counter so the A1 test can verify
     * the C function actually fired (the log is a no-op in test build). */
    p_atomic_inc(&g_test_poc_print_calls);
#endif
    poc_log_linef("[FROM LUA] poc_print called \xe2\x80\x94 Lua executed a C function!");
    return 0;  /* no return values */
}

/* =====================================================================*
 *  do_inject: loadbuffer + pcall (the actual execution)
 * =====================================================================*
 * Caller already verified: L matches captured state, latch not set,
 * rate-limiter interval elapsed, max-retry cap not hit, and g_attempt_count
 * was just incremented. Sets g_injecting around our pcall.
 *
 * Returns 1 on success (chunk executed — latch should be set), 0 on
 * failure (globals not ready, chunk errored, or loadbuffer failed — the
 * caller should allow retry, subject to the cap).
 *
 * Stack contract: on success the stack is clean (pcall consumed the chunk,
 * 0 results). On failure we restore L->top to its pre-loadbuffer value,
 * popping the error object — the engine sees the stack exactly as its own
 * pcall left it. */
static int do_inject(void *L, unsigned long long now,
                     unsigned long long captured_tick) {
    /* Reentry guard ON before any lua_pcall-family call. */
    p_atomic_xchg(&g_injecting, 1);

    /* Save L->top so we can restore it on error (pop the error object).
     * L->top offset in lua_State: 0x18 in the Darktide binary's LuaJIT
     * (non-GC64; confirmed by lua_pcall disasm `mov rcx, [rcx+0x18]`).
     * The A1 test runs against the SYSTEM LuaJIT (typically GC64 on
     * modern x86_64), which uses a different offset — so the test sets
     * it via inject_test_set_L_top_offset() after auto-detecting it.
     * Each stack slot is 8 bytes (TValue), confirmed by lua_gettop's
     * `(top-base)>>3` computation. */
#ifdef PHASE4_TEST_API
    uint8_t **L_top = (uint8_t **)((uint8_t *)L + g_test_L_top_offset);
#else
    uint8_t **L_top = (uint8_t **)((uint8_t *)L + 0x18);
#endif
    uint8_t *saved_top = *L_top;

    int load_rc = p_luaL_loadbuffer(L, g_inject_src, (size_t)g_inject_src_len,
                                     "[poc-inject]");
    int pcall_rc = -1;
    if (load_rc == 0) {
        /* This call goes through p_lua_pcall, which is the patched target.
         * It re-enters detour_lua_pcall. The reentry sees g_injecting==1
         * and routes straight to g_orig_pcall (the trampoline / real
         * lua_pcall), which executes our chunk. Net recursion depth: 2. */
        pcall_rc = p_lua_pcall(L, 0, 0, 0);
    }

    int success = (load_rc == 0 && pcall_rc == 0);

    if (!success) {
        /* FAILED — globals not ready (pcall_rc=LUA_ERRRUN), chunk errored,
         * or loadbuffer itself failed (pcall_rc still -1). In all cases
         * exactly one item (error object) was left on the stack above
         * saved_top. Restore L->top to pop it. The engine's stack is left
         * exactly as its own pcall left it — this retry is stack-neutral. */
        *L_top = saved_top;
    }

    /* Reentry guard OFF. */
    p_atomic_xchg(&g_injecting, 0);

#ifdef PHASE4_TEST_API
    g_test_last_load_rc  = load_rc;
    g_test_last_pcall_rc = pcall_rc;
#endif

    /* delay_ms = how long after capture this attempt happened. Logged
     * either way (success or failure) so Tier B can verify timing. */
    poc_log_linef("injected attempt=%lu load_rc=%d pcall_rc=%d (0=success) "
                  "delay_ms=%llu src_len=%llu",
                  (unsigned long)g_attempt_count, load_rc, pcall_rc,
                  (unsigned long long)(now - captured_tick),
                  g_inject_src_len);

    return success;
}

/* =====================================================================*
 *  The detour — retry-on-error injection
 * =====================================================================*
 * Each time the engine calls lua_pcall, this hook fires. After letting
 * the engine's pcall complete (transparent passthrough), it checks the
 * guards in order and, if all pass, attempts to inject our chunk. On
 * failure (globals not ready), the latch is NOT set — the next engine
 * pcall triggers another attempt. On success, the latch is set and no
 * further attempts are made. A max-retry cap prevents infinite retries
 * if the chunk can never succeed. */
int detour_lua_pcall(void *L, int nargs, int nresults, int errfunc) {
    int rc;

#ifdef PHASE4_TEST_API
    LONG depth = p_atomic_inc(&g_test_detour_depth);
    if (depth > g_test_max_depth) p_atomic_xchg(&g_test_max_depth, depth);
#endif

    /* Guard 1 — reentry break: if we're inside our own injection's pcall,
     * just call the original (the trampoline / real lua_pcall). No
     * injection logic. This breaks the recursion when do_inject's
     * p_lua_pcall re-enters this detour. */
    if (g_injecting) {
        rc = g_orig_pcall ? g_orig_pcall(L, nargs, nresults, errfunc) : 0;
        goto done;
    }

    /* Engine's pcall completes first (transparent passthrough — the engine
     * cannot observe any difference from an unhooked call). */
    rc = g_orig_pcall ? g_orig_pcall(L, nargs, nresults, errfunc) : 0;

    /* Guard 2 — capture: only act on the captured lua_State*. Phase 3's
     * lua_newstate hook must have fired (captured_L != NULL) and the
     * caller's L must match it. */
    void *captured_L = CAPTURED_L();
    if (captured_L == NULL || L != captured_L) goto done;

    unsigned long long captured_tick = CAPTURED_TICK();
    if (captured_tick == 0) goto done;   /* defensive — shouldn't happen */

    /* ---- Phase 5 Step 2: one-shot openlibs diagnostic -------------- *
     * Call luaL_openlibs(L) ONCE on the captured state, BEFORE any
     * injection attempt. This tests whether the sandboxed _G (Phase 4
     * finding) is fixed by openlibs.
     *
     * The reentry guard (g_injecting) is set around the openlibs call so
     * any nested pcall triggered by openlibs's internals (e.g. via
     * luaopen_package's loader initialization) routes straight to the
     * trampoline and does not re-enter our injection logic.
     *
     * p_luaL_openlibs is NULL when openlibs is disabled. PRODUCTION wires
     * it to NULL (calling openlibs on the engine's state is destructive —
     * it crashes the game within 1 second, verified in live testing). The
     * A1 test may wire it to the real luaL_openlibs to exercise the
     * openlibs call path (the openlibs logic is retained for diagnostic
     * purposes). */
    if (p_luaL_openlibs != NULL && !g_openlibs_called) {
        p_atomic_xchg(&g_injecting, 1);
        p_luaL_openlibs(L);
        p_atomic_xchg(&g_injecting, 0);
        p_atomic_xchg(&g_openlibs_called, 1);
        poc_log_linef("openlibs called on captured L=0x%llx",
                      (unsigned long long)(uintptr_t)L);
    }

    /* ---- Phase 5 Step 3: C-function bootstrap ---------------------- *
     * Register our poc_print C function as a Lua global ONCE on the
     * captured state, BEFORE the injection attempt. This bypasses the
     * sandboxed _G entirely — we provide our own implementation via the
     * LuaJIT C API.
     *
     *   lua_pushcclosure(L, &poc_print, 0)  -- pushes a C closure (nups=0)
     *   lua_setfield(L, LUA_GLOBALSINDEX, "poc_print")
     *                                        -- pops it & sets _G.poc_print
     *
     * Stack-neutral: pushcclosure pushes 1, setfield pops 1 (consumes the
     * value being set). The reentry guard is set defensively (neither
     * function should re-enter pcall, but GC indirection is unpredictable).
     *
     * p_lua_pushcclosure / p_lua_setfield are NULL when the bootstrap is
     * disabled (A1 Phase 4 negative control). Production always wires
     * both to non-NULL (resolved from the binary in inject_install). */
    if (p_lua_pushcclosure != NULL && p_lua_setfield != NULL &&
        !g_cfuncs_registered) {
        p_atomic_xchg(&g_injecting, 1);
        p_lua_pushcclosure(L, (void *)&poc_print, 0);
        p_lua_setfield(L, POC_LUA_GLOBALSINDEX, "poc_print");
        p_atomic_xchg(&g_injecting, 0);
        p_atomic_xchg(&g_cfuncs_registered, 1);
        poc_log_linef("cfunctions registered: poc_print");
    }

    /* Guard 3 — latch: stop retrying once we've succeeded or given up. */
    if (g_injected) goto done;

    /* Guard 4 — min-interval rate limiter. Prevents hammering the VM
     * during the engine's init burst (lua_pcall may fire dozens of times
     * per second). This is NOT a readiness delay — the chunk's self-check
     * determines readiness; this just throttles how often we re-check.
     * First attempt (g_last_attempt_tick == 0) always passes.
     * When PHASE4_INJECT_DELAY_MS is 0 (test build), the rate limiter is
     * compiled out entirely (the comparison would be unsigned < 0, a no-op). */
    unsigned long long now = p_now_ms();
#if PHASE4_INJECT_DELAY_MS > 0
    if (g_last_attempt_tick != 0 &&
        (now - g_last_attempt_tick) < (unsigned long long)PHASE4_INJECT_DELAY_MS) {
        goto done;
    }
#endif

    /* Guard 5 — max-retry cap. If we've exhausted all attempts, give up
     * (set the latch so we never try again) and log it. This catches the
     * case where the C-function bootstrap permanently fails to register
     * poc_print (e.g., wrong pushcclosure/setfield RVAs after a game
     * update — the chunk can never succeed). */
    if ((unsigned long)g_attempt_count >= (unsigned long)PHASE4_MAX_INJECT_ATTEMPTS) {
        poc_log_linef("giving up after %lu attempts — poc_print never "
                      "became available", (unsigned long)g_attempt_count);
        p_atomic_xchg(&g_injected, 1);
        goto done;
    }

    /* ---- Attempt the injection ----------------------------------- *
     * Increment the counter, run loadbuffer+pcall, and set the latch
     * only on success. On failure, do_inject already restored the stack. */
    p_atomic_inc(&g_attempt_count);

    int success = do_inject(L, now, captured_tick);

    g_last_attempt_tick = p_now_ms();

    if (success) {
        /* Chunk executed — globals were ready. Stop retrying forever. */
        p_atomic_xchg(&g_injected, 1);
    }

done:
#ifdef PHASE4_TEST_API
    p_atomic_dec(&g_test_detour_depth);
#endif
    return rc;
}

/* =====================================================================*
 *  Production: MinHook install
 * =====================================================================*/
#ifndef PHASE4_TEST_API

int inject_install(HMODULE main_module) {
    if (!main_module) {
        poc_log_linef("inject ABORT main_module is NULL");
        return 1;
    }
    uintptr_t base = (uintptr_t)main_module;

    LPVOID p_pcall        = (LPVOID)(base + RVA_LUA_PCALL);
    LPVOID p_loadbuffer   = (LPVOID)(base + RVA_LUAL_LOADBUFFER);
    LPVOID p_openlibs     = (LPVOID)(base + RVA_LUAL_OPENLIBS);
    LPVOID p_pushcclosure = (LPVOID)(base + RVA_LUA_PUSHCCLOSURE);
    LPVOID p_setfield     = (LPVOID)(base + RVA_LUA_SETFIELD);

    /* Wire up the function pointers used by do_inject and the detour.
     *
     * openlibs is DISABLED — calling it on the engine's state is
     * destructive (overwrites the engine's custom globals → crash within
     * 1 second, verified in live testing). The engine already called
     * openlibs during its own init. We don't need to call it again.
     *
     * The C-function bootstrap pointers (pushcclosure + setfield) ARE
     * wired — these are what register our poc_print as a Lua global,
     * bypassing the sandboxed _G. */
    p_lua_pcall        = (lua_pcall_t)p_pcall;            /* patched after MH_EnableHook */
    p_luaL_loadbuffer  = (luaL_loadbuffer_t)p_loadbuffer;
    /* p_luaL_openlibs = (luaL_openlibs_t)p_openlibs; -- DISABLED (destructive) */
    p_lua_pushcclosure = (lua_pushcclosure_t)p_pushcclosure;
    p_lua_setfield     = (lua_setfield_t)p_setfield;

    poc_log_linef("inject targets: lua_pcall=0x%llx (rva=0x%x) "
                  "luaL_loadbuffer=0x%llx (rva=0x%x) "
                  "luaL_openlibs=0x%llx (rva=0x%x) [DISABLED] "
                  "lua_pushcclosure=0x%llx (rva=0x%x) "
                  "lua_setfield=0x%llx (rva=0x%x) "
                  "min_interval_ms=%d max_attempts=%d",
                  (unsigned long long)(uintptr_t)p_pcall,        RVA_LUA_PCALL,
                  (unsigned long long)(uintptr_t)p_loadbuffer,    RVA_LUAL_LOADBUFFER,
                  (unsigned long long)(uintptr_t)p_openlibs,      RVA_LUAL_OPENLIBS,
                  (unsigned long long)(uintptr_t)p_pushcclosure,  RVA_LUA_PUSHCCLOSURE,
                  (unsigned long long)(uintptr_t)p_setfield,      RVA_LUA_SETFIELD,
                  PHASE4_INJECT_DELAY_MS, PHASE4_MAX_INJECT_ATTEMPTS);

    /* MinHook was initialized by phase3_install() (which always runs first).
     * If phase3_install failed, DllMain skipped us. */

    /* CreateHook on a target MinHook has already hooked returns
     * MH_ERROR_ALREADY_CREATED. Phase 4 builds Phase 3 with
     * -DPHASE3_INCLUDE_PCALL_OBSERVERS=0 so no observer hook exists on
     * 0xc744c0 — we own this target. */
    MH_STATUS s = MH_CreateHook(p_pcall, (LPVOID)&detour_lua_pcall,
                                (LPVOID *)&g_orig_pcall);
    if (s != MH_OK) {
        poc_log_linef("inject ABORT MH_CreateHook(lua_pcall) failed: %s (%d)",
                      MH_StatusToString(s), (int)s);
        return 2;
    }
    /* Per-target enable (phase3_install's MH_EnableHook(MH_ALL_HOOKS) only
     * enabled hooks that existed at that moment — ours is new). */
    s = MH_EnableHook(p_pcall);
    if (s != MH_OK) {
        poc_log_linef("inject ABORT MH_EnableHook(lua_pcall) failed: %s (%d)",
                      MH_StatusToString(s), (int)s);
        return 3;
    }

    poc_log_linef("inject lua_pcall hook installed at 0x%llx (rva=0x%x) "
                  "min_interval_ms=%d max_attempts=%d",
                  (unsigned long long)(uintptr_t)p_pcall, RVA_LUA_PCALL,
                  PHASE4_INJECT_DELAY_MS, PHASE4_MAX_INJECT_ATTEMPTS);
    return 0;
}

#endif  /* !PHASE4_TEST_API */

/* =====================================================================*
 *  Test API (only compiled with -DPHASE4_TEST_API)
 * =====================================================================*/
#ifdef PHASE4_TEST_API

void inject_test_setup(void *real_pcall, void *real_loadbuffer,
                       void *real_openlibs,
                       void *captured_L, unsigned long long captured_tick) {
    g_orig_pcall         = (lua_pcall_t)real_pcall;
    p_luaL_loadbuffer    = (luaL_loadbuffer_t)real_loadbuffer;
    /* p_lua_pcall points at our own detour, so do_inject's p_lua_pcall(...)
     * re-enters detour_lua_pcall — exactly what MinHook's patched-target
     * jump does in production. The reentry guard then routes the inner
     * call to g_orig_pcall (the real lua_pcall from libluajit). */
    p_lua_pcall          = (lua_pcall_t)(void *)&detour_lua_pcall;
    /* p_luaL_openlibs: if real_openlibs is NULL, the detour's openlibs
     * call is skipped (used by A1 Phase 3 to validate retry-on-error
     * without openlibs). Otherwise it points at the real luaL_openlibs
     * from libluajit (validates the openlibs diagnostic works). */
    p_luaL_openlibs      = (luaL_openlibs_t)real_openlibs;
    g_test_captured_L    = captured_L;
    g_test_captured_tick = captured_tick;
}

void inject_test_set_cfunc_bootstrappers(void *pushcclosure, void *setfield) {
    /* Wire (or unwire) the C-function bootstrap pointers.
     * Pass the system LuaJIT's &lua_pushcclosure and &lua_setfield to
     * enable the bootstrap, or NULL/NULL to disable it (negative control). */
    p_lua_pushcclosure = (lua_pushcclosure_t)pushcclosure;
    p_lua_setfield     = (lua_setfield_t)setfield;
}

void inject_test_reset(void) {
    g_injecting             = 0;
    g_injected              = 0;
    g_attempt_count         = 0;
    g_last_attempt_tick     = 0;
    g_openlibs_called       = 0;
    g_cfuncs_registered     = 0;
    g_test_detour_depth     = 0;
    g_test_max_depth        = 0;
    g_test_last_load_rc     = -1;
    g_test_last_pcall_rc    = -1;
    g_test_poc_print_calls  = 0;
    /* NOTE: g_test_L_top_offset, p_luaL_openlibs, p_lua_pushcclosure, and
     * p_lua_setfield are NOT reset here — they're properties of the
     * linked LuaJIT build / test phase, set once via the corresponding
     * setters and persist across resets. */
}

void inject_test_reset_openlibs(void) {
    /* Used between A1 phases that each want to exercise the one-shot
     * openlibs call. Does not touch any other state. */
    g_openlibs_called = 0;
}

void inject_test_reset_cfunc_bootstrap(void) {
    /* Used between A1 phases that each want to exercise the one-shot
     * C-function bootstrap. Resets the bootstrap latch AND the poc_print
     * call counter (so the next phase starts from a clean observable).
     * Does not touch the bootstrap pointers themselves — the test
     * configures those once per phase via inject_test_set_cfunc_bootstrappers. */
    g_cfuncs_registered    = 0;
    g_test_poc_print_calls = 0;
}

int inject_test_last_load_rc(void)  { return g_test_last_load_rc; }
int inject_test_last_pcall_rc(void) { return g_test_last_pcall_rc; }
int inject_test_inject_count(void)  { return (int)g_attempt_count; }
int inject_test_detour_depth(void)  { return (int)g_test_max_depth; }
int inject_test_openlibs_called(void) { return (int)g_openlibs_called; }
int inject_test_cfuncs_registered(void) { return (int)g_cfuncs_registered; }
int inject_test_poc_print_calls(void)  { return (int)g_test_poc_print_calls; }

void inject_test_set_L_top_offset(size_t offset) {
    g_test_L_top_offset = offset;
}

#endif  /* PHASE4_TEST_API */
