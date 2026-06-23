/*
 * inject.h — Phase 5: DMF bootstrap via the C-function bootstrap.
 *
 * Phase 5 builds on Phase 4's retry-on-error injection mechanism + Phase 5
 * Step 3's C-function bootstrap. It implements the full DMF dependency
 * surface as C functions, builds the `Mods` table, and loads
 * `dmf_loader.lua` from a staging directory — all via the LuaJIT C API,
 * bypassing the sandboxed `_G` entirely.
 *
 * This module hooks `lua_pcall` at `base + 0xc744c0` (the address re-
 * identified in Phase 4 rev2 by source-pattern matching against LuaJIT
 * 2.1's lj_api.c). When the engine calls lua_pcall on the captured state,
 * our detour:
 *
 *   1. Lets the engine's pcall complete (transparent passthrough).
 *   2. (Phase 5 Step 2, retained but DISABLED in production) If
 *      luaL_openlibs has not been called yet on this state, calls
 *      `luaL_openlibs(L)` ONCE at `base + 0xc7f380`. DISABLED because
 *      live testing proved it's destructive.
 *   3. (Phase 5 NEW — DMF bootstrap) If the bootstrap has not yet run on
 *      this state, registers the 6 DMF dependencies as C functions / Lua
 *      tables via the C API. The dependencies are:
 *
 *        _G.__print               = c_print       (writes args to log file)
 *        _G.Mods.file.dofile      = c_dofile      (reads + execs a .lua file)
 *        _G.Mods.lua.loadstring   = c_loadstring  (compiles a Lua source)
 *        _G.Mods.lua.io           = {}             (empty table; minimal POC)
 *        _G.Mods.require_store    = {}             (DMF's require populates)
 *        _G.Mods.original_require = c_require_stub (logs + returns nil)
 *
 *      Built via `lua_createtable` + `lua_pushcclosure` + `lua_setfield`
 *      (each direct-call'd at the indicated RVA).
 *   4. Attempts to execute our bootstrap Lua chunk via `luaL_loadbuffer`
 *      (`base + 0xc7ad80`) + `lua_pcall(L, 0, 0, 0)`. The chunk is:
 *
 *        return Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")
 *
 *      which reads `dmf_loader.lua` from the staging dir via c_dofile and
 *      executes it. The loader uses Mods.file.dofile to load DMF's modules.
 *
 * Story 6 success criterion: `dmf_loader.lua` begins executing without
 * immediate errors (pcall_rc=0). It's OK if it doesn't fully initialize —
 * we just need to prove it can start.
 *
 * Threading model:
 *   - The detour runs on whatever thread the engine called lua_pcall on.
 *     That thread owns g_captured_L (per LuaJIT's single-thread-state
 *     contract). Our loadbuffer+pcall calls happen on that same thread.
 *     Safe by construction.
 *   - We do NOT call any LuaJIT API from any other thread.
 *
 * Reentry:
 *   - When our bootstrap chunk executes via `p_lua_pcall(L, 0, 0, 0)`, that
 *     call goes through the SAME hook (p_lua_pcall is the patched address),
 *     re-entering detour_lua_pcall. The g_injecting flag breaks the
 *     recursion: when g_injecting is 1, the detour calls the trampoline
 *     directly without attempting injection.
 *   - c_dofile and c_loadstring also call p_lua_pcall internally. When
 *     called from inside the bootstrap chunk's execution (g_injecting=1),
 *     the nested pcall re-enters the detour and is routed straight to the
 *     trampoline. When called from Lua outside our bootstrap (e.g., the
 *     engine later calls dmf_mod_object:init() which calls
 *     Mods.file.dofile), g_injecting is 0 but g_injected is 1 (latch set),
 *     so the detour skips injection logic — the nested pcall still goes
 *     through transparently.
 *
 * Stack hygiene:
 *   - lua_pushcclosure pushes +1; lua_setfield pops -1 (consumes the value
 *     being set). Net: 0 on each setup step.
 *   - lua_createtable pushes +1 (the new table).
 *   - luaL_loadbuffer pushes the chunk (+1). lua_pcall(L, 0, 0, 0) pops
 *     the chunk and pushes 0 results (-1). Net: 0 on success.
 *   - On failure (loadbuffer error OR pcall error), 1 error object is
 *     left above where the engine expects the stack. We restore L->top
 *     directly (write to `[L + 0x18]`, the `top` field — offset confirmed
 *     by lua_pcall's disasm `mov rcx, [rcx+0x18]`). This pops the error
 *     object without needing `lua_pop`/`lua_settop`. The engine sees its
 *     stack exactly as its own pcall left it — every retry is stack-neutral.
 *
 * All actions log to `darktide-poc.log` (poc_log.h).
 *
 * Test build (-DPHASE4_TEST_API):
 *   - No MinHook dependency; the test wires up the function pointers via
 *     inject_test_setup() + inject_test_set_cfunc_bootstrappers() +
 *     inject_test_set_dmf_api() + inject_test_set_staging_dir() and calls
 *     detour_lua_pcall() directly. This is the A1 strong gate.
 *   - c_print / c_dofile / c_loadstring / c_require_stub have test-only
 *     side effects (counter increments) exposed via inject_test_c_*_calls()
 *     accessors.
 */
#ifndef DARKTIDE_POC_PHASE5_INJECT_H
#define DARKTIDE_POC_PHASE5_INJECT_H

/* Production builds need windows.h for HMODULE. Test builds
 * (-DPHASE4_TEST_API) compile inject.c against the system LuaJIT without
 * any Windows headers, so the HMODULE-using declarations are hidden.
 * stddef.h is always needed for size_t. */
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
 * checks per second. Override at compile time with
 * -DPHASE4_INJECT_DELAY_MS=N (the A1 test uses 0 so the rate limiter is a
 * no-op).
 *
 * NOTE: the macro name retains "DELAY" for backward compatibility with the
 * test's compile flag (-DPHASE4_INJECT_DELAY_MS=0). */
#ifndef PHASE4_INJECT_DELAY_MS
#define PHASE4_INJECT_DELAY_MS 500
#endif

/* Maximum number of injection attempts before giving up. Bounds overhead
 * if the chunk can never succeed (e.g., dmf_loader.lua is missing from
 * the staging dir). At the default min-interval of 500ms, 200 attempts
 * span ~100s, which comfortably covers the engine's full startup window.
 * When the cap is hit, the latch is set (retrying stops) and a "giving
 * up" line is logged. */
#ifndef PHASE4_MAX_INJECT_ATTEMPTS
#define PHASE4_MAX_INJECT_ATTEMPTS 200
#endif

/* ---- Production API --------------------------------------------------*/

/* Install the lua_pcall hook (MinHook). Resolves all needed addresses
 * from `main_module`'s base + confirmed RVAs (lua_pcall, luaL_loadbuffer,
 * lua_pushcclosure, lua_setfield, lua_tolstring, lua_createtable,
 * lua_gettop), creates + enables a single hook on lua_pcall. Also reads
 * the staging directory from DARKTIDE_MOD_STAGING (or derives a default).
 * Returns 0 on success, nonzero on failure (logged). Safe to call from
 * DllMain under the loader lock — MinHook does no loader operations. Must
 * be called AFTER phase3_install() (which initializes MinHook and installs
 * lua_newstate). */
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
 * Pass real_openlibs = &luaL_openlibs to enable the openlibs call in the
 * detour, or NULL to disable it (production scenario). */
void inject_test_setup(void *real_pcall, void *real_loadbuffer,
                       void *real_openlibs,
                       void *captured_L, unsigned long long captured_tick);

/* Set the C-function bootstrap pointers (lua_pushcclosure + lua_setfield).
 * Pass the system LuaJIT's &lua_pushcclosure and &lua_setfield to ENABLE
 * the bootstrap, or NULL/NULL to DISABLE it. */
void inject_test_set_cfunc_bootstrappers(void *pushcclosure, void *setfield);

/* Set the Phase 5 DMF-bootstrap API pointers (lua_tolstring,
 * lua_createtable, lua_gettop, lua_type). Pass the system LuaJIT's
 * addresses to ENABLE the DMF bootstrap, or NULL/NULL/NULL/NULL to
 * DISABLE it. */
void inject_test_set_dmf_api(void *tolstring, void *createtable,
                              void *gettop, void *lua_type);

/* Override the staging directory (default "./staging" in test build). */
void inject_test_set_staging_dir(const char *path);

/* Override the chunk source (default is the bootstrap chunk that calls
 * Mods.file.dofile on dmf_loader). Pass NULL/0 to restore the default. */
void inject_test_set_inject_src(const char *src, size_t len);

/* Reset all detour state to initial. Used between sub-tests. */
void inject_test_reset(void);

/* Reset ONLY the openlibs one-shot latch (g_openlibs_called). */
void inject_test_reset_openlibs(void);

/* Reset the C-function bootstrap one-shot latch (g_cfuncs_registered)
 * AND the poc_print call counter. */
void inject_test_reset_cfunc_bootstrap(void);

/* Test introspection. */
int inject_test_last_load_rc(void);    /* last loadbuffer rc (-1 if never) */
int inject_test_last_pcall_rc(void);   /* last pcall rc (-1 if never) */
int inject_test_inject_count(void);    /* # of injection attempts */
int inject_test_detour_depth(void);    /* max recursion depth observed */
int inject_test_openlibs_called(void); /* 1 if openlibs latch is set */
int inject_test_cfuncs_registered(void); /* 1 if C-function bootstrap latch is set */
int inject_test_dmf_setup_done(void);  /* 1 if DMF Mods+__print setup ran */
int inject_test_poc_print_calls(void); /* # of times poc_print was called from Lua */
int inject_test_c_print_calls(void);    /* # of times c_print was called */
int inject_test_c_dofile_calls(void);   /* # of times c_dofile was called */
int inject_test_c_dofile_ok(void);      /* # of times c_dofile read+exec'd successfully */
int inject_test_c_loadstring_calls(void); /* # of times c_loadstring was called */
int inject_test_c_loadstring_ok(void);  /* # of times c_loadstring compiled successfully */
int inject_test_c_require_calls(void);  /* # of times c_require_stub was called */

/* Override the L->top offset used for stack save/restore. Production
 * hardcodes 0x18 (Darktide binary's non-GC64 LuaJIT). The system LuaJIT
 * used by A1 may be GC64 (different struct layout), so the test auto-
 * detects the correct offset and sets it here before triggering the
 * detour. See a1_inject_test.c's detect_L_top_offset(). */
void inject_test_set_L_top_offset(size_t offset);

#endif  /* PHASE4_TEST_API */

#endif /* DARKTIDE_POC_PHASE5_INJECT_H */
