/*
 * launcher.h — Public interface for the launcher's testable seams.
 *
 * Extracted from launcher.c so tests can call set_steam_env() and
 * inject_and_resume() without going through main().
 */
#ifndef MAGOS_LAUNCHER_H
#define MAGOS_LAUNCHER_H

#include <windows.h>

/* Set SteamAppId and SteamGameId to Darktide's appID in the current process
 * environment. The child created by CreateProcessA inherits this block. */
void set_steam_env(void);

/* Full injection + handshake + resume flow.
 * Creates game_exe in SUSPENDED state, injects dll_path via
 * CreateRemoteThread(LoadLibraryA), waits for the hook-ready event
 * (hook_timeout ms), then resumes the main thread.
 * Returns 0 on success, 1 on any failure. On failure the child process
 * is terminated and all handles are cleaned up. */
int inject_and_resume(const char *game_exe, const char *dll_path,
                      DWORD hook_timeout);

#endif /* MAGOS_LAUNCHER_H */
