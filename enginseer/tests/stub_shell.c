/*
 * stub_shell.c — Minimal stub DLL for injection testing.
 *
 * On DLL_PROCESS_ATTACH, signals the magos_hook_ready event (matching the
 * real shell's behavior after MH_EnableHook). No real discovery or hooking
 * is performed — this is purely a test double.
 */
#include <windows.h>

#define MAGOS_HOOK_READY_EVENT "magos_hook_ready"

BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID reserved) {
    (void)hInst; (void)reserved;

    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hInst);

        /* Signal hook-ready immediately (the real shell does this after
         * discovery + MH_EnableHook). */
        HANDLE ready = OpenEventA(EVENT_MODIFY_STATE, FALSE,
                                  MAGOS_HOOK_READY_EVENT);
        if (ready) {
            SetEvent(ready);
            CloseHandle(ready);
        }
    }
    return TRUE;
}
