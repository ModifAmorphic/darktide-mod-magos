/* dllmain.c — dbghelp proxy DLL entry point.
 *
 * Responsibilities (Phase 1, process-foothold only):
 *   1. On DLL_PROCESS_ATTACH:
 *      - LoadLibraryW the real dbghelp.dll from C:\Windows\System32\
 *        (under Wine/Proton this is the prefix's builtin; under native
 *        Windows it is the real Microsoft dbghelp). The absolute path
 *        bypasses the DLL search order, so the loader cannot re-enter
 *        our proxy (no recursion).
 *      - Eagerly resolve every forwarded export into its g_fwd_<NAME>
 *        cache entry so the stub fast-path always hits.
 *      - Append a timestamped attach line to darktide-poc.log next to
 *        this DLL.
 *   2. On DLL_PROCESS_DETACH: append a detach line, free the real handle.
 *
 * No hooks, no Lua, no function discovery here. Phase 1 is foothold-only.
 */

#include <windows.h>
#include <stdarg.h>
#include <stdio.h>

#include "stubs.h"

/* The real dbghelp handle (loaded at attach). NULL until then. */
static HMODULE g_real_dbghelp = NULL;

/* Absolute path of the real dbghelp.dll.
 *
 * Notes on the recursion concern:
 *   - The naive LoadLibraryW(L"dbghelp.dll") would re-enter us via the
 *     application directory entry of the search order. We MUST use an
 *     absolute path.
 *   - Under Wine/Proton with WINEDLLOVERRIDES=dbghelp=native, the engine
 *     loads US (the proxy) as "native" from the game's binaries/. When
 *     our DllMain then issues LoadLibraryW on this absolute path, Wine
 *     resolves C:\Windows\System32\dbghelp.dll to the prefix's builtin
 *     (the WINE implementation), because there is no "native" file at
 *     that location in the default prefix.
 *   - Under native Windows, this path is the real Microsoft dbghelp.
 *   - Either way, the result is a handle to a DIFFERENT module from us.
 */
#define REAL_DBGHELP_PATH L"C:\\Windows\\System32\\dbghelp.dll"

/* Forwarder resolver used by the generated stubs in stubs.c. */
FARPROC fwd_resolve(const char *name) {
    if (!g_real_dbghelp) {
        return NULL;
    }
    return GetProcAddress(g_real_dbghelp, name);
}

/* ---- Logging --------------------------------------------------------- */

/* Write a single line + "\n" to darktide-poc.log next to this DLL.
 *
 * DllMain runs inside the loader lock; file I/O is permitted there
 * (it does not re-enter the loader). We use the low-level CreateFileW
 * + WriteFile to avoid pulling in CRT stdio that might touch other
 * state. The file is opened APPEND + synchronous.
 *
 * The path is derived from GetModuleFileNameW on OUR hinstDLL — never
 * hardcoded. The result is <dir of our DLL>\darktide-poc.log.
 */
static void log_line(HMODULE self, const char *message) {
    WCHAR dll_path[MAX_PATH];
    DWORD len = GetModuleFileNameW(self, dll_path, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) {
        return;  /* path too long or failed; nothing safe to do */
    }

    /* Strip the trailing filename, keep the directory. */
    DWORD i = len;
    while (i > 0 && dll_path[i - 1] != L'\\' && dll_path[i - 1] != L'/') {
        --i;
    }
    if (i + 20 >= MAX_PATH) {
        return;  /* no room to append the log filename */
    }
    /* i now points just past the last separator; copy the log name in. */
    static const WCHAR log_name[] = L"darktide-poc.log";
    for (DWORD j = 0; log_name[j] != L'\0'; ++j) {
        dll_path[i + j] = log_name[j];
    }
    dll_path[i + (sizeof(log_name) / sizeof(WCHAR) - 1)] = L'\0';

    /* Build the formatted log line as ASCII (pid/ts/message are ASCII). */
    SYSTEMTIME st;
    GetSystemTime(&st);
    char buf[256];
    int n = snprintf(
        buf, sizeof(buf),
        "[darktide-poc] %s pid=%lu ts=%04d-%02d-%02dT%02d:%02d:%02dZ\n",
        message,
        (unsigned long)GetCurrentProcessId(),
        st.wYear, st.wMonth, st.wDay,
        st.wHour, st.wMinute, st.wSecond);
    if (n <= 0) {
        return;
    }
    /* snprintf returns the would-be byte count. If >= size, truncation
     * occurred and there is no trailing '\n'. Clamp to size-1 and add a
     * '\n'. Our lines are short enough that this never triggers in
     * practice, but handle it defensively. */
    DWORD to_write;
    if ((DWORD)n >= sizeof(buf)) {
        to_write = (DWORD)sizeof(buf) - 1;
        buf[to_write - 1] = '\n';  /* overwrite partial last char with newline */
    } else {
        to_write = (DWORD)n;  /* includes the trailing '\n' */
    }

    HANDLE h = CreateFileW(dll_path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        return;
    }
    DWORD written = 0;
    WriteFile(h, buf, to_write, &written, NULL);
    CloseHandle(h);
}

/* ---- DllMain --------------------------------------------------------- */

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved) {
    switch (fdwReason) {
        case DLL_PROCESS_ATTACH: {
            /* Disable thread-library-calls so we don't see DLL_THREAD_*
             * notifications (keeps the log clean and avoids the loader
             * lock on thread attach). */
            DisableThreadLibraryCalls(hinstDLL);

            /* Log FIRST, so even if LoadLibrary fails we have evidence
             * the DLL was loaded and DllMain ran. */
            log_line(hinstDLL, "DllMain DLL_PROCESS_ATTACH");

            /* Load the real dbghelp. This is the only call from DllMain
             * that could be considered loader-lock-risky; loading a
             * system DLL that is typically already mapped is a well-
             * established proxy-DLL practice and does not deadlock in
             * practice (the loader fast-paths already-mapped modules). */
            g_real_dbghelp = LoadLibraryW(REAL_DBGHELP_PATH);
            if (!g_real_dbghelp) {
                log_line(hinstDLL, "DllMain ERROR failed to load real dbghelp.dll");
                /* We deliberately do NOT return FALSE here. Returning FALSE
                 * from DLL_PROCESS_ATTACH aborts the entire process, which
                 * would crash the game on launch. Instead we leave the
                 * forwarders as lazy resolvers (which will return NULL when
                 * called); the game will degrade gracefully or error out,
                 * not crash. */
                return TRUE;
            }

            /* Eagerly resolve every forwarder so the stub fast-path is
             * always a clean tail jump. We use the per-export name table
             * that gen_stubs.py emits. */
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
            char summary[96];
            snprintf(summary, sizeof(summary),
                     "DllMain forwarders resolved=%d missing=%d total=%d",
                     resolved, missing, resolved + missing);
            log_line(hinstDLL, summary);
            return TRUE;
        }

        case DLL_PROCESS_DETACH: {
            log_line(hinstDLL, "DllMain DLL_PROCESS_DETACH");
            /* Per the MSDN DllMain contract: if lpvReserved is non-NULL,
             * the process is exiting and the loader will tear down all
             * modules on its own. Calling FreeLibrary in that state can
             * run the real dbghelp's DLL_PROCESS_DETACH against an
             * already-torn-down CRT heap, so we only free on explicit
             * unload (lpvReserved == NULL). */
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
