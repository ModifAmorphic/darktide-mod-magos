/*
 * launcher.c — CreateRemoteThread DLL injector.
 *
 * Creates Darktide.exe in a SUSPENDED state, injects magos_shell.dll via
 * CreateRemoteThread(LoadLibraryA, <dllpath>), waits for the DLL to signal
 * that the lua_newstate hook is ready, then resumes. Zero files land in the
 * game directory (the DLL is loaded from a staging path).
 *
 * Production "Launch Modded" flow (a production-launcher requirement, not
 * production requirement): CreateProcess(SUSPENDED) → inject → wait for hook-ready
 * → ResumeThread, with the correct Steam appID set in the child environment.
 * The hook-ready wait is essential: DllMain returns instantly (it only spawns
 * a worker), and the worker doesn't enable the lua_newstate hook until after
 * discovery completes — resuming before the hook is ready means the engine
 * calls lua_newstate before the hook is installed, so the hook never fires.
 * (The production-summary's CreateProcess→inject→ResumeThread is incomplete
 * without the hook-ready wait.)
 *
 * Configuration model: every setting is flag > env > default.
 *   --game-binary <path>   [MAGOS_ENGINSEER_GAME_BINARY]     REQUIRED
 *   --magos-shell <path>   [MAGOS_ENGINSEER_SHELL]           <launcher-dir>\magos_shell.dll
 *   --mod-path <path>      [DARKTIDE_MOD_PATH]               (optional; trampoline skips if unset)
 *   --log-file <path>      [MAGOS_ENGINSEER_LOG_FILE]        <launcher-dir>\magos_enginseer.log
 *   --log-level <level>    [MAGOS_ENGINSEER_LOG_LEVEL]       info
 *   --steam-app-id <id>    [MAGOS_ENGINSEER_STEAM_APP_ID]    1361210
 *
 * Windows native: build with cl.exe or x86_64-w64-mingw32-gcc.
 * Proton: build the launcher for Windows (mingw) and run it under Wine inside
 * the Steam Proton prefix (see docs/decisions/0001-component-a-language-and-structure.md).
 */
#include "launcher.h"
#include <stdio.h>
#include <string.h>

/* ---- config: defaults + env var names ------------------------------------ */

#define MAGOS_DEFAULT_STEAM_APPID "1361210"
#define MAGOS_DEFAULT_LOG_LEVEL   "info"
#define MAGOS_DEFAULT_SHELL_NAME  "magos_shell.dll"
#define MAGOS_DEFAULT_LOG_NAME    "magos_enginseer.log"

#define ENV_GAME_BINARY  "MAGOS_ENGINSEER_GAME_BINARY"
#define ENV_SHELL        "MAGOS_ENGINSEER_SHELL"
#define ENV_MOD_PATH     "DARKTIDE_MOD_PATH"
#define ENV_LOG_FILE     "MAGOS_ENGINSEER_LOG_FILE"
#define ENV_LOG_LEVEL    "MAGOS_ENGINSEER_LOG_LEVEL"
#define ENV_STEAM_APP_ID "MAGOS_ENGINSEER_STEAM_APP_ID"

/* Named event for the launcher<->shell hook-ready handshake. Created
 * session-local (no Global\ prefix — avoids SeCreateGlobalPrivilege; launcher
 * and target run in the same session) before injection; the shell signals it
 * after MH_EnableHook succeeds. Must match shell/src/dllmain.c. */
#define MAGOS_HOOK_READY_EVENT "magos_hook_ready"

/* Buffer size for a resolved path. Generously above MAX_PATH (260) so long
 * staging paths and extended-length paths don't silently truncate. */
#define MAGOS_PATH_MAX 1024

/* testable helpers get external linkage under the test build so unit tests
 * can call them; production keeps them file-static. */
#ifdef MAGOS_TEST_BUILD
#define MAGOS_INTERNAL
#else
#define MAGOS_INTERNAL static
#endif

/* ---- testable seams (extracted for unit testing) ---- */

void set_steam_env(const char *app_id) {
    SetEnvironmentVariableA("SteamAppId", app_id);
    SetEnvironmentVariableA("SteamGameId", app_id);
}

int inject_and_resume(const char *game_exe, const char *dll_path,
                      DWORD hook_timeout) {
    HANDLE hook_ready = NULL;
    STARTUPINFOA si = { .cb = sizeof(si) };
    PROCESS_INFORMATION pi = {0};
    char cmdline[1024];

    /* 0. Fail-fast pre-checks: verify both paths exist before creating any
     *    process. A bad path would otherwise have CreateRemoteThread run
     *    LoadLibraryA on a NULL/missing module (LoadLibraryA returns NULL
     *    fast) and then wait out hook_timeout on the hook-ready event, which
     *    never fires. Catch typos / bad paths up front instead. */
    if (GetFileAttributesA(game_exe) == INVALID_FILE_ATTRIBUTES) {
        fprintf(stderr, "[launcher] error: game exe not found: %s\n", game_exe);
        return 1;
    }
    if (GetFileAttributesA(dll_path) == INVALID_FILE_ATTRIBUTES) {
        fprintf(stderr, "[launcher] error: DLL not found: %s\n", dll_path);
        return 1;
    }

    /* 1. CreateProcess(SUSPENDED). The hook (lua_newstate) must be installed
     *    before the engine's main() runs; SUSPENDED + inject + wait-hook-ready
     *    + resume gives that timing guarantee. */
    snprintf(cmdline, sizeof(cmdline), "\"%s\"", game_exe);
    if (!CreateProcessA(game_exe, cmdline, NULL, NULL, FALSE,
                        CREATE_SUSPENDED, NULL, NULL, &si, &pi)) {
        fprintf(stderr, "[launcher] error: CreateProcess(SUSPENDED) "
                "(GetLastError=%lu)\n", GetLastError());
        return 1;
    }
    printf("[launcher] created %s pid=%lu (suspended)\n",
           game_exe, pi.dwProcessId);

    /* 2. Allocate + write the DLL path into the target process. */
    size_t path_len = strlen(dll_path) + 1;
    LPVOID remote = VirtualAllocEx(pi.hProcess, NULL, path_len,
                                   MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) {
        fprintf(stderr, "[launcher] error: VirtualAllocEx "
                "(GetLastError=%lu)\n", GetLastError());
        goto kill;
    }
    if (!WriteProcessMemory(pi.hProcess, remote, dll_path, path_len, NULL)) {
        fprintf(stderr, "[launcher] error: WriteProcessMemory "
                "(GetLastError=%lu)\n", GetLastError());
        goto kill;
    }

    /* 3. Create the hook-ready event before CreateRemoteThread, so it exists
     *    before the DLL's worker can OpenEvent it (manual-reset, initially
     *    non-signaled; session-local name). */
    hook_ready = CreateEventA(NULL, TRUE, FALSE, MAGOS_HOOK_READY_EVENT);
    if (!hook_ready) {
        fprintf(stderr, "[launcher] error: CreateEvent(hook_ready) "
                "(GetLastError=%lu)\n", GetLastError());
        goto kill;
    }

    /* 4. CreateRemoteThread(LoadLibraryA, remote_dll_path). */
    HMODULE k32 = GetModuleHandleA("kernel32.dll");
    FARPROC load_lib = GetProcAddress(k32, "LoadLibraryA");
    if (!load_lib) {
        fprintf(stderr, "[launcher] error: GetProcAddress LoadLibraryA "
                "(GetLastError=%lu)\n", GetLastError());
        goto kill;
    }
    HANDLE th = CreateRemoteThread(pi.hProcess, NULL, 0,
                                   (LPTHREAD_START_ROUTINE)load_lib, remote,
                                   0, NULL);
    if (!th) {
        fprintf(stderr, "[launcher] error: CreateRemoteThread "
                "(GetLastError=%lu)\n", GetLastError());
        goto kill;
    }
    printf("[launcher] injected %s via CreateRemoteThread\n", dll_path);

    /* 5. Wait for LoadLibraryA to return (DllMain ran). DllMain returns
     *    immediately — it only spawns a worker — so this just confirms the
     *    DLL is loaded; the hook is NOT ready yet. A timeout here means DllMain
     *    hung (e.g. the worker blocked under the loader lock); the LoadLibraryA
     *    thread may still be running and referencing `remote`, so we must NOT
     *    free it out from under the thread — terminate the process instead. */
    DWORD lt = WaitForSingleObject(th, 10000);
    if (lt != WAIT_OBJECT_0) {
        fprintf(stderr, "[launcher] error: LoadLibraryA thread %s (GetLastError=%lu)\n",
                lt == WAIT_TIMEOUT ? "timed out (DllMain hung)" : "wait failed",
                GetLastError());
        CloseHandle(th);
        goto kill;
    }
    /* LoadLibraryA returned: the remote thread's exit code is its return
     * value — the loaded module handle, or 0 on failure. A 0 here means the
     * DLL exists but failed to load (missing dependencies, wrong arch, etc.).
     * Fail fast instead of waiting out the hook-ready timeout, which would
     * never fire for a DLL whose DllMain never ran. Must read before
     * CloseHandle(th) — the exit code is gone once the handle is. */
    DWORD exit_code = 0;
    if (!GetExitCodeThread(th, &exit_code)) {
        fprintf(stderr, "[launcher] error: GetExitCodeThread "
                "(GetLastError=%lu)\n", GetLastError());
        CloseHandle(th);
        goto kill;
    }
    if (exit_code == 0) {
        fprintf(stderr, "[launcher] error: DLL load failed: %s "
                "(LoadLibraryA returned NULL — missing dependencies? "
                "wrong architecture?)\n", dll_path);
        CloseHandle(th);
        goto kill;
    }
    CloseHandle(th);
    VirtualFreeEx(pi.hProcess, remote, 0, MEM_RELEASE);

    /* 6. Wait for the worker to install + enable the lua_newstate hook before
     *    letting the engine's main() run. The worker signals hook_ready after
     *    MH_EnableHook succeeds. On timeout/failure we terminate rather than
     *    resume with an unready hook. */
    DWORD w = WaitForSingleObject(hook_ready, hook_timeout);
    if (w != WAIT_OBJECT_0) {
        fprintf(stderr, "[launcher] error: %s (GetLastError=%lu)\n",
                w == WAIT_TIMEOUT ? "hook-ready timeout"
                                   : "hook-ready wait failed",
                GetLastError());
        goto kill;
    }
    printf("[launcher] hook ready; resuming\n");

    /* 7. Resume the engine's main thread — the lua_newstate hook is armed. */
    if (ResumeThread(pi.hThread) == (DWORD)-1) {
        fprintf(stderr, "[launcher] error: ResumeThread "
                "(GetLastError=%lu)\n", GetLastError());
        goto kill;
    }

    /* The resolved log file is published to the child env by main() before
     * this call; read it back so the "where are the logs" hint is accurate. */
    {
        char log_buf[MAGOS_PATH_MAX];
        DWORD ln = GetEnvironmentVariableA(ENV_LOG_FILE, log_buf,
                                           sizeof(log_buf));
        if (ln > 0 && ln < sizeof(log_buf)) {
            printf("[launcher] resumed; game should reach main menu. "
                   "Logs -> %s\n", log_buf);
        } else {
            printf("[launcher] resumed; game should reach main menu.\n");
        }
    }
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

/* ---- config resolution (flag > env > default) ---------------------------- */

/* Resolver-owned stable storage for values not sourced from argv. One buffer
 * per setting so a resolve never clobbers another setting mid-call. Reused
 * across resolve_config() calls — callers must copy out before re-resolving. */
static char g_game_binary_buf[MAGOS_PATH_MAX];
static char g_shell_buf[MAGOS_PATH_MAX];
static char g_mod_path_buf[MAGOS_PATH_MAX];
static char g_log_file_buf[MAGOS_PATH_MAX];
static char g_log_level_buf[32];
static char g_steam_app_id_buf[32];

/* Reads env_name into out. Returns 1 if the var is set and fit in outsz, 0 if
 * unset, empty, too long, or errored (treated as "not provided" so the next
 * precedence level — default — applies). */
static int read_env(const char *env_name, char *out, size_t outsz) {
    DWORD n = GetEnvironmentVariableA(env_name, out, (DWORD)outsz);
    if (n == 0 || n >= outsz) return 0;  /* unset/error, or truncated */
    return 1;
}

/* Fills dir_buf with the launcher exe's directory (no trailing backslash), or
 * "." if it can't be determined (GetModuleFileNameA failed/truncated, or no
 * path separator present). */
static void get_launcher_dir(char *dir_buf, size_t dir_sz) {
    char self[MAGOS_PATH_MAX];
    DWORD n = GetModuleFileNameA(NULL, self, sizeof(self));
    if (n == 0 || n >= sizeof(self)) {
        snprintf(dir_buf, dir_sz, ".");
        return;
    }
    char *slash = strrchr(self, '\\');
    if (!slash) {
        snprintf(dir_buf, dir_sz, ".");
        return;
    }
    size_t dirlen = (size_t)(slash - self);
    if (dirlen + 1 > dir_sz) {
        snprintf(dir_buf, dir_sz, ".");
        return;
    }
    memcpy(dir_buf, self, dirlen);
    dir_buf[dirlen] = '\0';
}

/* Builds "<launcher-dir>\<leaf>" into out (the default for path settings). */
static void build_default_path(char *out, size_t outsz, const char *leaf) {
    char dir[MAGOS_PATH_MAX];
    get_launcher_dir(dir, sizeof(dir));
    snprintf(out, outsz, "%s\\%s", dir, leaf);
}

MAGOS_INTERNAL int magos_parse_args(int argc, char **argv,
                                    magos_parsed_args *out) {
    memset(out, 0, sizeof(*out));
    for (int i = 1; i < argc; i++) {
        const char *flag = argv[i];
        const char **target;

        if (strcmp(flag, "-h") == 0 || strcmp(flag, "--help") == 0) return -2;

        if      (strcmp(flag, "--game-binary")  == 0) target = &out->game_binary;
        else if (strcmp(flag, "--magos-shell")  == 0) target = &out->magos_shell;
        else if (strcmp(flag, "--mod-path")     == 0) target = &out->mod_path;
        else if (strcmp(flag, "--log-file")     == 0) target = &out->log_file;
        else if (strcmp(flag, "--log-level")    == 0) target = &out->log_level;
        else if (strcmp(flag, "--steam-app-id") == 0) target = &out->steam_app_id;
        else {
            fprintf(stderr, "[launcher] error: unknown flag: %s\n", flag);
            return -1;
        }

        if (i + 1 >= argc) {
            fprintf(stderr, "[launcher] error: missing value for %s\n", flag);
            return -1;
        }
        *target = argv[++i];
    }
    return 0;
}

MAGOS_INTERNAL void magos_resolve_config(const magos_parsed_args *args,
                                         magos_config *cfg) {
    /* game_binary: required — no default, NULL if both flag and env are unset
     * (main() rejects this with usage). */
    cfg->game_binary = args->game_binary
        ? args->game_binary
        : (read_env(ENV_GAME_BINARY, g_game_binary_buf, sizeof(g_game_binary_buf))
              ? g_game_binary_buf : NULL);

    /* magos_shell: default <launcher-dir>\magos_shell.dll */
    if (args->magos_shell) {
        cfg->magos_shell = args->magos_shell;
    } else if (read_env(ENV_SHELL, g_shell_buf, sizeof(g_shell_buf))) {
        cfg->magos_shell = g_shell_buf;
    } else {
        build_default_path(g_shell_buf, sizeof(g_shell_buf),
                           MAGOS_DEFAULT_SHELL_NAME);
        cfg->magos_shell = g_shell_buf;
    }

    /* mod_path: optional — NULL (unset) means the trampoline skips. */
    if (args->mod_path) {
        cfg->mod_path = args->mod_path;
    } else if (read_env(ENV_MOD_PATH, g_mod_path_buf, sizeof(g_mod_path_buf))) {
        cfg->mod_path = g_mod_path_buf;
    } else {
        cfg->mod_path = NULL;
    }

    /* log_file: default <launcher-dir>\magos_enginseer.log */
    if (args->log_file) {
        cfg->log_file = args->log_file;
    } else if (read_env(ENV_LOG_FILE, g_log_file_buf, sizeof(g_log_file_buf))) {
        cfg->log_file = g_log_file_buf;
    } else {
        build_default_path(g_log_file_buf, sizeof(g_log_file_buf),
                           MAGOS_DEFAULT_LOG_NAME);
        cfg->log_file = g_log_file_buf;
    }

    /* log_level: default info */
    cfg->log_level = args->log_level
        ? args->log_level
        : (read_env(ENV_LOG_LEVEL, g_log_level_buf, sizeof(g_log_level_buf))
              ? g_log_level_buf : MAGOS_DEFAULT_LOG_LEVEL);

    /* steam_app_id: default 1361210 */
    cfg->steam_app_id = args->steam_app_id
        ? args->steam_app_id
        : (read_env(ENV_STEAM_APP_ID, g_steam_app_id_buf, sizeof(g_steam_app_id_buf))
              ? g_steam_app_id_buf : MAGOS_DEFAULT_STEAM_APPID);
}

/* ---- usage --------------------------------------------------------------- */

static void print_usage(FILE *out, const char *prog) {
    fprintf(out,
        "Usage: %s --game-binary <path> [options]\n"
        "\n"
        "Create Darktide.exe in a suspended state, inject magos_shell.dll via\n"
        "CreateRemoteThread, wait for the lua_newstate hook to arm, then resume.\n"
        "Zero files land in the game directory.\n"
        "\n"
        "Every setting follows: flag > env var > default.\n"
        "\n"
        "Required:\n"
        "  --game-binary <path>   Darktide.exe\n"
        "                         [env: MAGOS_ENGINSEER_GAME_BINARY]\n"
        "\n"
        "Optional:\n"
        "  --magos-shell <path>   magos_shell.dll to inject\n"
        "                         [env: MAGOS_ENGINSEER_SHELL]\n"
        "                         [default: <launcher-dir>\\magos_shell.dll]\n"
        "  --mod-path <path>      staged mods dir; if unset the trampoline skips\n"
        "                         [env: DARKTIDE_MOD_PATH] [default: unset]\n"
        "  --log-file <path>      launcher/shell log file\n"
        "                         [env: MAGOS_ENGINSEER_LOG_FILE]\n"
        "                         [default: <launcher-dir>\\magos_enginseer.log]\n"
        "  --log-level <level>    one of: error warn info debug trace\n"
        "                         [env: MAGOS_ENGINSEER_LOG_LEVEL] [default: info]\n"
        "  --steam-app-id <id>    Steam app id\n"
        "                         [env: MAGOS_ENGINSEER_STEAM_APP_ID]\n"
        "                         [default: 1361210]\n"
        "\n"
        "  -h, --help             show this help and exit\n"
        "\n"
        "<launcher-dir> is the directory of this exe (fall back to '.' if the\n"
        "launcher path can't be resolved).\n",
        prog);
}

/* ---- entry point (excluded when building test objects) ---- */

#ifndef MAGOS_TEST_BUILD
int main(int argc, char **argv) {
    magos_parsed_args args;
    int pr = magos_parse_args(argc, argv, &args);
    if (pr == -2) {
        print_usage(stdout, argv[0]);
        return 0;
    }
    if (pr == -1) {
        print_usage(stderr, argv[0]);
        return 2;
    }

    magos_config cfg;
    magos_resolve_config(&args, &cfg);

    if (!cfg.game_binary) {
        fprintf(stderr, "[launcher] error: --game-binary is required "
                "(or set %s)\n", ENV_GAME_BINARY);
        print_usage(stderr, argv[0]);
        return 2;
    }

    /* Publish the resolved config to the child env so CreateProcessA(NULL
     * env) inherits it: Steam identity, shell logging, and the staged mod
     * path. DARKTIDE_MOD_PATH is set only when configured — leaving it unset
     * means the shell's trampoline skips (same as today). */
    set_steam_env(cfg.steam_app_id);
    SetEnvironmentVariableA(ENV_LOG_FILE, cfg.log_file);
    SetEnvironmentVariableA(ENV_LOG_LEVEL, cfg.log_level);
    if (cfg.mod_path) {
        SetEnvironmentVariableA(ENV_MOD_PATH, cfg.mod_path);
    }

    /* Existence of game_binary + magos_shell is validated by the fail-fast
     * pre-checks inside inject_and_resume. */
    return inject_and_resume(cfg.game_binary, cfg.magos_shell, 60000);
}
#endif /* MAGOS_TEST_BUILD */
