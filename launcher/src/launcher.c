/*
 * launcher.c — CreateRemoteThread DLL injector (Spike 001 step 3).
 *
 * Creates Darktide.exe in a SUSPENDED state, injects magos_shell.dll via
 * CreateRemoteThread(LoadLibraryA, <dllpath>), then resumes. Zero files land
 * in the game directory (the DLL is loaded from a staging path).
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

    /* 1. CreateProcess(SUSPENDED). The hook (lua_newstate) must be installed
     *    before the engine's main() runs; SUSPENDED + inject + resume gives
     *    that timing guarantee (the proxy-DLL POC used DllMain for the same). */
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

    /* 3. CreateRemoteThread(LoadLibraryA, remote_dll_path). */
    HMODULE k32 = GetModuleHandleA("kernel32.dll");
    FARPROC load_lib = GetProcAddress(k32, "LoadLibraryA");
    if (!load_lib) { fail("GetProcAddress LoadLibraryA", GetLastError()); goto kill; }
    HANDLE th = CreateRemoteThread(pi.hProcess, NULL, 0,
                                   (LPTHREAD_START_ROUTINE)load_lib, remote,
                                   0, NULL);
    if (!th) { fail("CreateRemoteThread", GetLastError()); goto kill; }
    printf("[launcher] injected %s via CreateRemoteThread\n", dll_path);

    /* 4. Wait for LoadLibraryA to return (DllMain ran), then resume the game. */
    WaitForSingleObject(th, 10000);
    CloseHandle(th);
    VirtualFreeEx(pi.hProcess, remote, 0, MEM_RELEASE);

    if (ResumeThread(pi.hThread) == (DWORD)-1) {
        fail("ResumeThread", GetLastError()); goto kill;
    }
    printf("[launcher] resumed; game should reach main menu. Logs -> magos_spike.log\n");
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;

kill:
    TerminateProcess(pi.hProcess, 1);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 1;
}
