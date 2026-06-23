/*
 * launcher.c — CreateRemoteThread DLL injector (Spike 001 step 3).
 *
 * Creates Darktide.exe in a SUSPENDED state, injects magos_shell.dll via
 * CreateRemoteThread(LoadLibraryA, <dllpath>), waits for the DLL to signal
 * that the lua_newstate hook is ready, then resumes. Zero files land in the
 * game directory (the DLL is loaded from a staging path).
 *
 * Production "Launch Modded" flow (a production-launcher requirement, not
 * just a spike fix): CreateProcess(SUSPENDED) → inject → wait for hook-ready
 * → ResumeThread, with the correct Steam appID set in the child environment.
 * The hook-ready wait is essential: DllMain returns instantly (it only spawns
 * a worker), and the worker doesn't enable the lua_newstate hook until after
 * discovery completes — resuming before the hook is ready means the engine
 * calls lua_newstate before the hook is installed, so the hook never fires.
 * (The production-summary's CreateProcess→inject→ResumeThread is incomplete
 * without the hook-ready wait.)
 *
 * Usage: magos_launcher <game_exe> <dll_path>
 *   e.g. magos_launcher \
 *          "/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe" \
 *          "/path/to/magos_shell.dll"
 *
 * Windows native: build with cl.exe or x86_64-w64-mingw32-gcc.
 * Proton: build the launcher for Windows (mingw) and run it under Wine inside
 * the Steam Proton prefix (see runbook.md).
 */
#include <windows.h>
#include <stdio.h>
#include <string.h>

/* Darktide's Steam appID. Set in the launcher env (inherited by the child via
 * CreateProcess) so the correct appID reaches steamclient. When launched via a
 * Steam non-Steam-game shortcut the shortcut's *hashed* SteamAppId is
 * inherited instead and SteamAPI_Init is denied. Spike: constant; production:
 * configured. */
#define DARKTIDE_STEAM_APPID "1361210"

/* Named event for the launcher<->shell hook-ready handshake. Created
 * session-local (no Global\ prefix — avoids SeCreateGlobalPrivilege; launcher
 * and target run in the same session) before injection; the shell signals it
 * after MH_EnableHook succeeds. Must match shell/src/dllmain.c. */
#define MAGOS_HOOK_READY_EVENT "magos_hook_ready"

static int fail(const char *msg, DWORD gle) {
    fprintf(stderr, "[launcher] error: %s (GetLastError=%lu)\n", msg, gle);
    return 1;
}

int main(int argc, char **argv) {
    if (argc < 3) {
        fprintf(stderr, "usage: %s <game_exe> <dll_path>\n", argv[0]);
        return 2;
    }
    const char *game_exe = argv[1];
    const char *dll_path = argv[2];
    HANDLE hook_ready = NULL;

    /* 0. Set Darktide's Steam appID in our env so the child inherits it
     *    (CreateProcessA below passes NULL for lpEnvironment → the child
     *    inherits our environment block). SteamAppId is what steamclient
     *    reads; SteamGameId is the companion Steam sets natively — set both. */
    SetEnvironmentVariableA("SteamAppId", DARKTIDE_STEAM_APPID);
    SetEnvironmentVariableA("SteamGameId", DARKTIDE_STEAM_APPID);

    /* 1. CreateProcess(SUSPENDED). The hook (lua_newstate) must be installed
     *    before the engine's main() runs; SUSPENDED + inject + wait-hook-ready
     *    + resume gives that timing guarantee (the proxy-DLL POC used DllMain
     *    for the same). */
    STARTUPINFOA si = { .cb = sizeof(si) };
    PROCESS_INFORMATION pi = {0};
    char cmdline[1024];
    snprintf(cmdline, sizeof(cmdline), "\"%s\"", game_exe);
    if (!CreateProcessA(game_exe, cmdline, NULL, NULL, FALSE,
                        CREATE_SUSPENDED, NULL, NULL, &si, &pi)) {
        return fail("CreateProcess(SUSPENDED)", GetLastError());
    }
    printf("[launcher] created %s pid=%lu (suspended)\n", game_exe, pi.dwProcessId);

    /* 2. Allocate + write the DLL path into the target process. */
    size_t path_len = strlen(dll_path) + 1;
    LPVOID remote = VirtualAllocEx(pi.hProcess, NULL, path_len,
                                   MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) { fail("VirtualAllocEx", GetLastError()); goto kill; }
    if (!WriteProcessMemory(pi.hProcess, remote, dll_path, path_len, NULL)) {
        fail("WriteProcessMemory", GetLastError()); goto kill;
    }

    /* 3. Create the hook-ready event before CreateRemoteThread, so it exists
     *    before the DLL's worker can OpenEvent it (manual-reset, initially
     *    non-signaled; session-local name). */
    hook_ready = CreateEventA(NULL, TRUE, FALSE, MAGOS_HOOK_READY_EVENT);
    if (!hook_ready) { fail("CreateEvent(hook_ready)", GetLastError()); goto kill; }

    /* 4. CreateRemoteThread(LoadLibraryA, remote_dll_path). */
    HMODULE k32 = GetModuleHandleA("kernel32.dll");
    FARPROC load_lib = GetProcAddress(k32, "LoadLibraryA");
    if (!load_lib) { fail("GetProcAddress LoadLibraryA", GetLastError()); goto kill; }
    HANDLE th = CreateRemoteThread(pi.hProcess, NULL, 0,
                                   (LPTHREAD_START_ROUTINE)load_lib, remote,
                                   0, NULL);
    if (!th) { fail("CreateRemoteThread", GetLastError()); goto kill; }
    printf("[launcher] injected %s via CreateRemoteThread\n", dll_path);

    /* 5. Wait for LoadLibraryA to return (DllMain ran). DllMain returns
     *    immediately — it only spawns a worker — so this just confirms the
     *    DLL is loaded; the hook is NOT ready yet. */
    WaitForSingleObject(th, 10000);
    CloseHandle(th);
    VirtualFreeEx(pi.hProcess, remote, 0, MEM_RELEASE);

    /* 6. Wait for the worker to install + enable the lua_newstate hook before
     *    letting the engine's main() run. The worker signals hook_ready after
     *    MH_EnableHook succeeds. On timeout/failure we terminate rather than
     *    resume with an unready hook (the engine would call lua_newstate
     *    before the hook is armed → hook never fires). */
    DWORD w = WaitForSingleObject(hook_ready, 60000);
    if (w != WAIT_OBJECT_0) {
        fail(w == WAIT_TIMEOUT ? "hook-ready timeout (60s)"
                               : "hook-ready wait failed",
             GetLastError());
        goto kill;  /* do NOT resume with an unready hook */
    }
    printf("[launcher] hook ready; resuming\n");

    /* 7. Resume the engine's main thread — the lua_newstate hook is armed. */
    if (ResumeThread(pi.hThread) == (DWORD)-1) {
        fail("ResumeThread", GetLastError()); goto kill;
    }
    printf("[launcher] resumed; game should reach main menu. Logs -> magos_spike.log\n");
    CloseHandle(hook_ready);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;

kill:
    TerminateProcess(pi.hProcess, 1);
    if (hook_ready) CloseHandle(hook_ready);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 1;
}
