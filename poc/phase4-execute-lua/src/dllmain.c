/*
 * dllmain.c — dbghelp proxy DLL entry point (Phase 4).
 *
 * Composed layers (each verified in prior phases):
 *   Phase 1: dbghelp forwarding (verified Tier B)
 *   Phase 2b: discovery worker thread (verified Tier B)
 *   Phase 3 (composed with observers OFF): lua_newstate hook captures
 *            the engine's lua_State* + a GetTickCount64 timestamp.
 *            The 3 lua_pcall-candidate observers are compiled out
 *            (-DPHASE3_INCLUDE_PCALL_OBSERVERS=0) because lua_pcall is
 *            already identified (0xc744c0, re-identified in Phase 4 rev 2
 *            by source-pattern match — see inject.c + report.md) and
 *            Phase 4 needs to install its own hook on that exact address.
 *   Phase 4 (this file's addition): inject_install() installs the
 *            lua_pcall hook with our execution detour. Runs synchronously
 *            inside DllMain after phase3_install, so the hook is live
 *            before DllMain returns.
 *
 * Hook install order:
 *   phase3_install  → MH_Initialize, MH_CreateHook(lua_newstate),
 *                     MH_EnableHook(MH_ALL_HOOKS)
 *   inject_install  → MH_CreateHook(lua_pcall), MH_EnableHook(lua_pcall)
 *
 * Both must run before DllMain returns — the engine calls lua_newstate
 * and lua_pcall within the first few seconds of process startup, both
 * on its main thread. Installing in DllMain guarantees no race.
 *
 * Loader-lock constraints: see Phase 3's dllmain.c (unchanged — Phase 4
 * adds only one more MH_CreateHook + MH_EnableHook, both loader-safe).
 */

#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

#include "stubs.h"
#include "poc_log.h"
#include "phase3_hooks.h"
#include "inject.h"

/* The real dbghelp handle (loaded at attach). NULL until then. */
static HMODULE g_real_dbghelp = NULL;

/* See Phase 1's dllmain.c for the recursion rationale: we MUST use an
 * absolute path so the loader cannot re-enter our proxy. Under Wine with
 * WINEDLLOVERRIDES=dbghelp=native,builtin, C:\Windows\System32\dbghelp.dll
 * resolves to the prefix's builtin (the Wine implementation). */
#define REAL_DBGHELP_PATH L"C:\\Windows\\System32\\dbghelp.dll"

/* Forwarder resolver used by the generated stubs in stubs.c. */
FARPROC fwd_resolve(const char *name) {
    if (!g_real_dbghelp) {
        return NULL;
    }
    return GetProcAddress(g_real_dbghelp, name);
}

/* Worker thread entry (defined in discover_worker.c). */
typedef struct { HMODULE self; } worker_ctx_t;
DWORD WINAPI discover_worker(LPVOID param);

/* ---- DllMain --------------------------------------------------------- */

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved) {
    switch (fdwReason) {
        case DLL_PROCESS_ATTACH: {
            DisableThreadLibraryCalls(hinstDLL);
            poc_log_init(hinstDLL);
            poc_log_linef("DllMain DLL_PROCESS_ATTACH");

            /* Load the real dbghelp. */
            g_real_dbghelp = LoadLibraryW(REAL_DBGHELP_PATH);
            if (!g_real_dbghelp) {
                poc_log_linef("DllMain ERROR failed to load real dbghelp.dll err=%lu",
                              GetLastError());
                return TRUE;
            }

            /* Eagerly resolve every forwarder. */
            int resolved = 0, missing = 0;
            for (const struct fwd_entry *e = g_fwd_entries; e->name != NULL; ++e) {
                FARPROC p = GetProcAddress(g_real_dbghelp, e->name);
                if (p) {
                    *e->slot = p;
                    ++resolved;
                } else {
                    ++missing;
                }
            }
            poc_log_linef("DllMain forwarders resolved=%d missing=%d total=%d",
                          resolved, missing, resolved + missing);

            /* ---- Phase 3 (observers OFF): install lua_newstate hook -- *
             * Captures g_captured_L + g_captured_tick. Initializes MinHook.
             * If this fails, do NOT attempt Phase 4 (no MinHook session). */
            HMODULE main_mod = GetModuleHandleW(NULL);
            int hook_rc = phase3_install(main_mod);
            if (hook_rc != 0) {
                poc_log_linef("DllMain phase3_install rc=%d (hooks NOT active; "
                              "Phase 4 inject skipped, discovery still runs)",
                              hook_rc);
                /* Fall through to spawn the worker; we still want discovery. */
            } else {
                /* ---- Phase 4: install lua_pcall hook for execution ---- *
                 * Only attempt if Phase 3 succeeded (MinHook initialized).
                 * Failure here is non-fatal: forwarding + discovery +
                 * capture still work, we just don't execute Lua. */
                int inject_rc = inject_install(main_mod);
                if (inject_rc != 0) {
                    poc_log_linef("DllMain inject_install rc=%d "
                                  "(Lua execution NOT active; capture still works)",
                                  inject_rc);
                }
            }

            /* ---- Phase 2b: spawn the discovery worker thread. -------- *
             * Always runs (even if hooks failed) as cross-check. */
            worker_ctx_t *wc = (worker_ctx_t *)malloc(sizeof(*wc));
            if (!wc) {
                poc_log_linef("DllMain ERROR malloc(worker_ctx_t) failed; "
                              "discovery skipped");
                return TRUE;
            }
            wc->self = hinstDLL;
            /* 32 MB worker stack — see Phase 2b's dllmain.c note
             * (STATUS_STACK_OVERFLOW fix; do NOT reduce without re-running
             * Tier B against the live game). */
            HANDLE th = CreateThread(NULL, 32 * 1024 * 1024, discover_worker, wc, 0, NULL);
            if (!th) {
                poc_log_linef("DllMain ERROR CreateThread failed err=%lu; "
                              "discovery skipped", GetLastError());
                free(wc);
                return TRUE;
            }
            CloseHandle(th);
            poc_log_linef("DllMain spawned discover_worker thread");
            return TRUE;
        }

        case DLL_PROCESS_DETACH: {
            poc_log_linef("DllMain DLL_PROCESS_DETACH");
            if (g_real_dbghelp && lpvReserved == NULL) {
                FreeLibrary(g_real_dbghelp);
            }
            g_real_dbghelp = NULL;
            return TRUE;
        }

        default:
            return TRUE;
    }
}
