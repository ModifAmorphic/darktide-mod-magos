/*
 * inject.h — Phase 4 + Phase 5 Step 3: execute Lua inside the engine's VM
 * via the **C-function bootstrap**.
 *
 * This module is the headline proof of the POC ("if this works, the core
 * approach is proven"). It installs a single hook on `lua_pcall` at
 * `base + 0xc744c0` (the address re-identified by source-pattern matching
 * against LuaJIT 2.1's lj_api.c — see report.md §"lua_pcall
 * re-identification"). When the engine calls lua_pcall on the captured
 * state, our detour:
 *
 *   1. Lets the engine's pcall complete (transparent passthrough).
 *   2. (Phase 5 Step 2, retained but DISABLED in production) If
 *      luaL_openlibs has not been called yet on this state, calls
 *      `luaL_openlibs(L)` ONCE at `base + 0xc7f380`. DISABLED because
 *      live testing proved it's destructive (overwrites the engine's
 *      custom globals → crash within 1 second). The pointer is wired to
 *      NULL in inject_install; the test build still exercises it.
 *   3. (Phase 5 Step 3 NEW — the C-function bootstrap) If the bootstrap
 *      has not yet run on this state, registers our own `poc_print` C
 *      function as a Lua global via `lua_pushcclosure(L, &poc_print, 0)`
 *      at `base + 0xc74580` + `lua_setfield(L, LUA_GLOBALSINDEX,
 *      "poc_print")` at `base + 0xc74cb0`. LUA_GLOBALSINDEX = -10002 in
 *      LuaJIT 2.1. This BYPASSES the sandboxed `_G` entirely: we provide
 *      our own implementation registered via the C API, not dependent on
 *      whatever the engine did to `_G`.
 *   4. Attempts to execute our diagnostic Lua chunk via
 *      `luaL_loadbuffer` (`base + 0xc7ad80`) + `lua_pcall(L, 0, 0, 0)`.
 *
 * The injected (diagnostic) chunk (Phase 5 Step 3):
 *
 *        poc_print()
 *        return 42
 *
 * What the chunk proves:
 *   - If `poc_print` was registered correctly → the C function fires,
 *     a log line `[FROM LUA] poc_print called — Lua executed a C
 *     function!` appears in `darktide-poc.log`, and pcall_rc=0.
 *   - If registration failed → `poc_print` is nil → "attempt to call a
 *     nil value" → pcall_rc=2 (LUA_ERRRUN).
 *
 * The observable effect is the log line. This proves Lua executed a
 * function that produced a verifiable side effect — via our C-function
 * bootstrap, bypassing the sandboxed `_G`.
 *
 * Retry-on-error timing model (Phase 4 rev 3, unchanged):
 *   The chunk self-checks for readiness — if `poc_print` is still nil
 *   (registration hasn't happened yet), `lua_pcall` returns nonzero
 *   (LUA_ERRRUN=2), which we treat as "not ready — try again". The latch
 *   is only set on success (pcall returns 0), so the next engine
 *   `lua_pcall` call triggers another attempt. This continues until the
 *   chunk executes successfully or the max-retry cap is hit.
 *
 * Threading model:
 *   - The detour runs on whatever thread the engine called lua_pcall on.
 *     That thread owns g_captured_L (per LuaJIT's single-thread-state
 *     contract). Our loadbuffer+pcall calls happen on that same thread.
 *     Safe by construction.
 *   - We do NOT call any LuaJIT API from any other thread.
 *
 * Reentry:
 *   - When our injected chunk executes via `p_lua_pcall(L, 0, 0, 0)`, that
 *     call goes through the SAME hook (p_lua_pcall is the patched address),
 *     re-entering detour_lua_pcall. The g_injecting flag breaks the
 *     recursion: when g_injecting is 1, the detour calls the trampoline
 *     directly without attempting injection.
 *   - The C-function bootstrap (pushcclosure + setfield) also sets
 *     g_injecting (defensive — neither function should re-enter pcall,
 *     but the same guard pattern as openlibs protects against GC-induced
 *     surprises).
 *
 * Stack hygiene:
 *   - lua_pushcclosure pushes +1 (the C closure). lua_setfield pops -1
 *     (it consumes the value being set). Net: 0 on the bootstrap.
 *   - luaL_loadbuffer pushes the chunk (+1). lua_pcall(L, 0, 0, 0) pops
 *     the chunk and pushes 0 results (-1). Net: 0 on success.
 *   - On failure (loadbuffer error OR pcall error), 1 error object is
 *     left above where the engine expects the stack. We restore L->top
 *     directly (write to `[L + 0x18]`, the `top` field — offset confirmed
 *     by lua_pcall's disasm `mov rcx, [rcx+0x18]`). This pops the error
 *     object without needing `lua_pop`/`lua_settop` (whose addresses we
 *     don't have confirmed). The engine sees its stack exactly as its
 *     own pcall left it — every retry is stack-neutral.
 *
 * All actions log to `darktide-poc.log` (poc_log.h). The C-function
 * bootstrap log line:
 *   [darktide-poc] cfunctions registered: poc_print
 * The C function itself logs:
 *   [darktide-poc] [FROM LUA] poc_print called — Lua executed a C function!
 *
 * Test build (-DPHASE4_TEST_API):
 *   - No MinHook dependency; the test wires up the function pointers via
 *     inject_test_setup() and calls detour_lua_pcall() directly to
 *     simulate the engine's hooked call. This is the A1 strong gate.
 *   - The C-function bootstrap pointers are wired via
 *     inject_test_set_cfunc_bootstrappers() (to the real libluajit
 *     `lua_pushcclosure` and `lua_setfield`) or left NULL to disable
 *     the bootstrap (exercises the retry-on-error path — used by A1
 *     Phase 4 negative control).
 *   - poc_print has a test-only side effect (counter increment) exposed
 *     via inject_test_poc_print_calls(), so the test can verify the C
 *     function actually fired (the log is a no-op in the test build).
 */
#ifndef DARKTIDE_POC_PHASE4_INJECT_H
#define DARKTIDE_POC_PHASE4_INJECT_H

/* Production builds need windows.h for HMODULE. Test builds
 * (-DPHASE4_TEST_API) compile inject.c against the system LuaJIT without
 * any Windows headers, so the HMODULE-using declarations are hidden.
 * stddef.h is always needed for size_t (inject_test_set_L_top_offset). */
#include <stddef.h>
#ifndef PHASE4_TEST_API
#include <windows.h>
#endif

/* ---- Configuration ---------------------------------------------------*/

/* Min interval (ms) between injection attempts — a rate limiter, NOT a
 * readiness delay. The engine's lua_pcall may fire dozens of times per
 * second during its init burst; this prevents hammering the VM with our
 * loadbuffer+pcall on every call. The chunk itself determines readiness
 * (it errors if globals aren't registered yet), so this value only
 * throttles how aggressively we re-check. Default 500ms = at most ~2
 * checks per second, which is plenty to catch the globals-ready moment
 * within a single-digit number of attempts. Override at compile time with
 * -DPHASE4_INJECT_DELAY_MS=N (the A1 test uses 0 so the rate limiter is a
 * no-op — the mock VM has all globals registered via luaL_openlibs before
 * the test triggers the detour).
 *
 * NOTE: the macro name retains "DELAY" for backward compatibility with the
 * test's compile flag (-DPHASE4_INJECT_DELAY_MS=0). Its semantics changed
 * from "fixed wait before a one-shot injection" to "min interval between
 * retry attempts". */
#ifndef PHASE4_INJECT_DELAY_MS
#define PHASE4_INJECT_DELAY_MS 500
#endif

/* Maximum number of injection attempts before giving up. Bounds overhead
 * if the chunk can never succeed (e.g., `io` is permanently sandboxed and
 * `print` is too — the readiness check never passes). At the default
 * min-interval of 500ms, 200 attempts span ~100s, which comfortably
 * covers the engine's full startup window. When the cap is hit, the latch
 * is set (retrying stops) and a "giving up" line is logged. */
#ifndef PHASE4_MAX_INJECT_ATTEMPTS
#define PHASE4_MAX_INJECT_ATTEMPTS 200
#endif

/* The Lua source string injected into the engine VM. Defined in inject.c;
 * declared here for inspection/test access. */
extern const char g_inject_src[];
extern const unsigned long long g_inject_src_len;

/* ---- Production API --------------------------------------------------*/

/* Install the lua_pcall hook (MinHook). Resolves `lua_pcall`,
 * `luaL_loadbuffer`, `lua_pushcclosure`, and `lua_setfield` from
 * `main_module`'s base + confirmed RVAs, creates + enables a single hook
 * on lua_pcall. Returns 0 on success, nonzero on failure (logged). Safe
 * to call from DllMain under the loader lock — MinHook does no loader
 * operations. Must be called AFTER phase3_install() (which initializes
 * MinHook and installs lua_newstate). */
#ifndef PHASE4_TEST_API
int inject_install(HMODULE main_module);
#endif

/* ---- Test API (only when compiled with -DPHASE4_TEST_API) ------------*
 *
 * Production builds (DLL) do not see these symbols. The A1 mock-VM test
 * compiles inject.c with -DPHASE4_TEST_API against the system LuaJIT,
 * exercising the exact detour source against a real VM. */
#ifdef PHASE4_TEST_API

/* The detour function (callable from outside so the test can simulate
 * the engine invoking the hooked lua_pcall). */
int detour_lua_pcall(void *L, int nargs, int nresults, int errfunc);

/* Set up the function pointers WITHOUT MinHook. After this call:
 *   - p_luaL_loadbuffer points to the real luaL_loadbuffer
 *   - p_lua_pcall      points to detour_lua_pcall (so calling it
 *                       re-enters the detour, just like a real hook)
 *   - g_orig_pcall     points to real_pcall (the "trampoline")
 *   - The capture getters report (captured_L, captured_tick)
 * This mirrors the post-MinHook state of the production pointers.
 *
 * Pass real_openlibs = &luaL_openlibs to enable the openlibs call in the
 * detour (validates the Phase 5 diagnostic), or NULL to disable it
 * (validates the retry-on-error path when openlibs is unavailable —
 * used by A1 Phase 3). Production wires this to NULL (openlibs is
 * destructive). */
void inject_test_setup(void *real_pcall, void *real_loadbuffer,
                       void *real_openlibs,
                       void *captured_L, unsigned long long captured_tick);

/* Set the C-function bootstrap pointers. Pass the system LuaJIT's
 * `&lua_pushcclosure` and `&lua_setfield` to ENABLE the bootstrap (the
 * detour registers poc_print as a global), or pass NULL/NULL to DISABLE
 * it (the detour skips registration — exercises the retry-on-error path
 * when registration is unavailable; used by A1 Phase 4 negative control).
 * Production always wires both to non-NULL (resolved from the binary). */
void inject_test_set_cfunc_bootstrappers(void *pushcclosure, void *setfield);

/* Reset all detour state to initial. Used between sub-tests.
 * NOTE: does NOT reset the L->top offset (that's a property of the
 * linked LuaJIT build, set once via inject_test_set_L_top_offset). */
void inject_test_reset(void);

/* Reset ONLY the openlibs one-shot latch (g_openlibs_called). Used
 * between A1 phases that each want to exercise the openlibs call
 * (which is one-shot by design). Does not touch any other state. */
void inject_test_reset_openlibs(void);

/* Reset the C-function bootstrap one-shot latch (g_cfuncs_registered)
 * AND the poc_print call counter. Used between A1 phases that each want
 * to exercise the bootstrap. Does not touch any other state. */
void inject_test_reset_cfunc_bootstrap(void);

/* Test introspection. */
int      inject_test_last_load_rc(void);   /* last loadbuffer rc (-1 if never) */
int      inject_test_last_pcall_rc(void);  /* last pcall rc (-1 if never) */
int      inject_test_inject_count(void);   /* # of injection attempts */
int      inject_test_detour_depth(void);   /* max recursion depth observed */
int      inject_test_openlibs_called(void); /* 1 if openlibs latch is set */
int      inject_test_cfuncs_registered(void); /* 1 if C-function bootstrap latch is set */
int      inject_test_poc_print_calls(void); /* # of times poc_print was called from Lua */

/* Override the L->top offset used for stack save/restore. Production
 * hardcodes 0x18 (Darktide binary's non-GC64 LuaJIT). The system LuaJIT
 * used by A1 may be GC64 (different struct layout), so the test auto-
 * detects the correct offset and sets it here before triggering the
 * detour. See a1_inject_test.c's detect_L_top_offset(). */
void     inject_test_set_L_top_offset(size_t offset);

#endif  /* PHASE4_TEST_API */

#endif /* DARKTIDE_POC_PHASE4_INJECT_H */
