/* dllmain.c — dbghelp proxy DLL entry point (Phase 2b).
 *
 * Phase 1 responsibilities (unchanged, verified working in the live game):
 *   - On DLL_PROCESS_ATTACH: LoadLibraryW the real dbghelp.dll from
 *     C:\Windows\System32\ (absolute path bypasses DLL search order,
 *     prevents re-entry), eagerly resolve every forwarded export, log
 *     the attach line.
 *   - On DLL_PROCESS_DETACH: log the detach line, free the real handle.
 *
 * Phase 2b addition:
 *   - After forwarding is set up, spawn a worker thread that runs the
 *     discovery engine against the host process's main module and logs
 *     the discovered addresses. The worker runs OUTSIDE DllMain (after
 *     it returns), so the loader lock is not held during capstone disasm.
 *
 * Why CreateThread from DllMain is safe here:
 *   - CreateThread itself does not acquire the loader lock in a way that
 *     conflicts with the caller already holding it; the new thread is
 *     queued and starts executing only after DllMain returns (the loader
 *     releases the lock).
 *   - The worker thread (discover_worker in discover_worker.c) avoids ALL
 *     loader APIs except GetModuleHandleW(NULL) (already-loaded, no lock).
 *     Capstone is statically linked; the engine does no LoadLibrary.
 *
 * No hooks, no Lua, no function calls into the target. Discovery + log only.
 */

#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

#include "stubs.h"
#include "poc_log.h"

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
            /* Disable thread-library-calls: keeps the log clean and avoids
             * taking the loader lock on thread attach/detach. */
            DisableThreadLibraryCalls(hinstDLL);

            /* Init the thread-safe logger FIRST (needed by everything below). */
            poc_log_init(hinstDLL);

            /* Log attach FIRST, so even if LoadLibrary fails we have evidence. */
            poc_log_linef("DllMain DLL_PROCESS_ATTACH");

            /* Load the real dbghelp. Loading a system DLL that is typically
             * already mapped is well-established proxy-DLL practice. */
            g_real_dbghelp = LoadLibraryW(REAL_DBGHELP_PATH);
            if (!g_real_dbghelp) {
                poc_log_linef("DllMain ERROR failed to load real dbghelp.dll err=%lu",
                              GetLastError());
                /* Do NOT return FALSE: that aborts the whole process. Leave
                 * forwarders as lazy resolvers; the game degrades gracefully. */
                return TRUE;
            }

            /* Eagerly resolve every forwarder so the stub fast-path is a
             * clean tail jump. */
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

            /* ---- Phase 2b: spawn the discovery worker thread. ---------- *
             * The thread runs after DllMain returns, holding no loader lock.
             * We close the handle immediately (we don't join — the worker
             * is fire-and-forget; it writes its log lines and JSON, then
             * exits). The worker_ctx_t is heap-allocated and freed by the
             * worker itself. */
            worker_ctx_t *wc = (worker_ctx_t *)malloc(sizeof(*wc));
            if (!wc) {
                poc_log_linef("DllMain ERROR malloc(worker_ctx_t) failed; "
                              "discovery skipped");
                return TRUE;
            }
            wc->self = hinstDLL;
            /* Large worker stack: the discovery engine + capstone use heavy
             * stack frames (large local cs_insn[] arrays + nested disasm of
             * the call graph). The default 1 MB thread stack overflows during
             * real discovery against the full Darktide image (verified:
             * STATUS_STACK_OVERFLOW 0xC00000FD in the live game). 32 MB is
             * generous but cheap (virtual, committed-on-touch). Do NOT reduce
             * this without re-running Tier B against the live game. */
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
            /* Per MSDN: if lpvReserved is non-NULL the process is exiting
             * and the loader tears down modules itself. Only free on
             * explicit unload. */
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
