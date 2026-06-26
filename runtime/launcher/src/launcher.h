/*
 * launcher.h — Public interface for the launcher's testable seams.
 *
 * Extracted from launcher.c so tests can call set_steam_env() and
 * inject_and_resume() (and, under MAGOS_TEST_BUILD, the config helpers)
 * without going through main().
 */
#ifndef MAGOS_LAUNCHER_H
#define MAGOS_LAUNCHER_H

#include <windows.h>

/*
 * Resolved launcher configuration.
 *
 * Every setting follows the same precedence: flag > env > default.
 * See launcher.c for the full table of flags, env vars, and defaults.
 *
 * Pointers are owned by the resolver; values sourced from argv or string
 * literals are stable for the process lifetime, while env/default values
 * live in resolver-owned static buffers (valid until the next resolve).
 */
typedef struct {
    const char *game_binary;   /* required: no default (NULL if unresolved)  */
    const char *magos_shell;   /* default: <launcher-dir>\magos_shell.dll   */
    const char *enginseer_path;/* default: <launcher-dir>\enginseer         */
    const char *mod_path;      /* optional: NULL => trampoline skips        */
    const char *log_file;      /* default: <launcher-dir>\magos_enginseer.log */
    const char *log_level;     /* default: info                              */
    const char *steam_app_id;  /* default: 1361210                           */
} magos_config;

/*
 * Raw flag parse result; a field is NULL when its flag was not given.
 * Pointers alias argv (stable for the process lifetime). Used internally by
 * main(); exposed here because magos_config carries the resolved form.
 */
typedef struct {
    const char *game_binary;
    const char *magos_shell;
    const char *enginseer_path;
    const char *mod_path;
    const char *log_file;
    const char *log_level;
    const char *steam_app_id;
} magos_parsed_args;

/*
 * Set SteamAppId and SteamGameId to app_id in the current process
 * environment. The child created by CreateProcessA inherits this block.
 */
void set_steam_env(const char *app_id);

/*
 * Full injection + handshake + resume flow.
 * Creates game_exe in SUSPENDED state, injects dll_path via
 * CreateRemoteThread(LoadLibraryA), waits for the hook-ready event
 * (hook_timeout ms), then resumes the main thread.
 * Returns 0 on success, 1 on any failure. On failure the child process
 * is terminated and all handles are cleaned up.
 */
int inject_and_resume(const char *game_exe, const char *dll_path,
                      DWORD hook_timeout);

#ifdef MAGOS_TEST_BUILD
/*
 * Internal config helpers — exposed (non-static) only for unit tests.
 * Production builds keep these file-static in launcher.c, where main()
 * (same translation unit) reaches them directly without a header prototype.
 */

/* Parse --flag <value> pairs.
 * Returns 0 on success, -1 on a bad/unknown flag or missing value,
 * -2 on -h/--help (not an error). */
int magos_parse_args(int argc, char **argv, magos_parsed_args *out);

/* Resolve flag > env > default into cfg (uses resolver-owned buffers for
 * values not sourced from argv). */
void magos_resolve_config(const magos_parsed_args *args, magos_config *cfg);

#endif /* MAGOS_TEST_BUILD */

#endif /* MAGOS_LAUNCHER_H */
