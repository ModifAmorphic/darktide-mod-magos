/*
 * phase3_hooks.h — Phase 3 hooking interface.
 *
 * Installed from DllMain (DLL_PROCESS_ATTACH) AFTER Phase 1 forwarding
 * is set up and BEFORE the discovery worker is spawned. The hooks are
 * live by the time DllMain returns, so they catch the engine's first
 * `lua_newstate` call (early in `main()`).
 *
 * What this module does:
 *   1. Resolves the LuaJIT function addresses from the host module base
 *      using the Phase 0 / Phase 2b-confirmed RVAs.
 *   2. Initializes MinHook.
 *   3. Hooks `lua_newstate` (thunk @ 0xc7c000). The detour captures the
 *      returned `lua_State*` AND a GetTickCount64 timestamp (the moment
 *      the engine created its VM), verifies the state via
 *      `lua_gettop(L) == 0`, and returns it to the engine transparently.
 *   4. WHEN `PHASE3_INCLUDE_PCALL_OBSERVERS` is non-zero (the default):
 *      hooks the three viable `lua_pcall` clustering candidates
 *      (0xc748d0, 0xc74f30, 0xc754d0). Each detour observes whether its
 *      first-arg (`L`) matches the captured state. The first candidate
 *      to fire with a matching `L` is identified as `lua_pcall`; the
 *      other two candidate hooks are then removed.
 *
 * Phase 4 composes this module with `PHASE3_INCLUDE_PCALL_OBSERVERS=0`
 * because lua_pcall is already identified (0xc74f30) and Phase 4 needs
 * to install its OWN hook there (MinHook disallows two hooks on the
 * same target). With observers compiled out, Phase 3 contributes only
 * the lua_newstate capture + timestamp.
 *
 * What this module does NOT do:
 *   - Execute Lua (Phase 4).
 *   - Re-run discovery (the worker thread does that, in parallel).
 *   - Call any LuaJIT function other than the single `lua_gettop(L)`
 *     verification call inside the lua_newstate detour.
 *
 * All actions log to `darktide-poc.log` (poc_log.h).
 */
#ifndef DARKTIDE_POC_PHASE3_HOOKS_H
#define DARKTIDE_POC_PHASE3_HOOKS_H

#include <windows.h>

/* Compile-time switch: include the 3 lua_pcall-candidate observer hooks.
 * Default ON (preserves Phase 3's standalone behavior). Phase 4 sets
 * this to 0 to avoid conflict with its own lua_pcall hook. */
#ifndef PHASE3_INCLUDE_PCALL_OBSERVERS
#define PHASE3_INCLUDE_PCALL_OBSERVERS 1
#endif

/* Install all Phase 3 hooks. Returns 0 on success, nonzero on failure
 * (logged). Safe to call from DllMain under the loader lock — MinHook
 * does no loader operations. `main_module` is the host EXE base
 * (GetModuleHandleW(NULL) at runtime); it is read for PE headers and
 * to compute target addresses, no loader interaction. */
int phase3_install(HMODULE main_module);

/* ---- Capture access (added for Phase 4) -----------------------------
 *
 * Phase 3 owns the captured lua_State* and the GetTickCount64 timestamp
 * at which it was captured. Both are set atomically by detour_lua_newstate.
 * Phase 4's injection logic reads them via these getters to decide WHEN
 * to inject (it waits PHASE4_INJECT_DELAY_MS after capture so the engine
 * has registered its globals).
 *
 * `phase3_get_captured_state()` returns NULL until lua_newstate fires.
 * `phase3_get_capture_tick()` returns 0 until lua_newstate fires.
 * Both are safe to call from any thread (the underlying reads are
 * atomic on x64 — aligned 64-bit + InterlockedCompareExchange for the
 * pointer read). */
void                  *phase3_get_captured_state(void);
unsigned long long    phase3_get_capture_tick(void);

#endif /* DARKTIDE_POC_PHASE3_HOOKS_H */
