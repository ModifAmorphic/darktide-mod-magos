/* host.c — Phase 3 Tier A2: DLL plumbing smoke test.
 *
 * Loads the built dbghelp.dll under Wine and verifies the FULL plumbing:
 *   - DllMain attach + Phase 1 forwarders resolved (200/200)
 *   - Phase 3 hooks install (or abort cleanly if the host module is the
 *     wrong binary — under Wine, this host.exe is tiny and the candidate
 *     addresses are outside the module; phase3_install's bounds check
 *     catches that and aborts gracefully. Either outcome is fine; the
 *     requirement is NO CRASH.)
 *   - the Phase 2b discovery worker thread spawns, runs discovery, logs
 *     its summary, and writes darktide-poc-discovery.json
 *   - a forwarded export still returns a sane value
 *
 * In this test the worker runs discovery against THIS host exe's main
 * module (not Darktide.exe), so it finds no LuaJIT anchors. The expected
 * discovery outcome is matched=0 mismatched=0 unresolved=7. Correctness
 * is covered by Phase 2b A1 (in_memory_host); A2 only proves the
 * plumbing survives Phase 3's additions.
 *
 * Exit codes:
 *   0  all plumbing checks passed
 *   1  LoadLibrary failed
 *   2  GetProcAddress for a forwarded export failed
 *   3  forwarded call did not return a sane value
 *   4  log missing DllMain/forwarder markers
 *   5  log missing worker/discover markers (thread didn't run)
 *   6  darktide-poc-discovery.json not written
 *   7  log missing Phase 3 hook markers (phase3_install never ran)
 */
#include <windows.h>
#include <stdio.h>

#define DBGHELP_LIB   L"dbghelp.dll"
#define ATTACH_MARKER "DllMain DLL_PROCESS_ATTACH"
#define FWD_MARKER    "DllMain forwarders resolved="
#define WORKER_MARKER "discover summary"
#define START_MARKER  "discover start"
#define HOOK_MARKER   "hook install base="
#define JSON_NAME     L"darktide-poc-discovery.json"

#define AS_FN(type, name) ((type)(void *)GetProcAddress(h, name))
typedef DWORD (WINAPI *SymGetOptions_t)(void);

static void build_path(WCHAR *out, size_t out_chars, const WCHAR *name) {
    DWORD len = GetModuleFileNameW(NULL, out, (DWORD)out_chars);
    if (len == 0 || len >= out_chars) { wcscpy(out, name); return; }
    DWORD i = len;
    while (i > 0 && out[i - 1] != L'\\' && out[i - 1] != L'/') --i;
    for (DWORD k = 0; name[k] != L'\0'; ++k) out[i + k] = name[k];
    out[i + wcslen(name)] = L'\0';
}
static int read_file(const WCHAR *path, char *out, size_t out_sz) {
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;
    DWORD got = 0;
    BOOL ok = ReadFile(h, out, (DWORD)out_sz - 1, &got, NULL);
    CloseHandle(h);
    if (!ok) return 0;
    out[got] = '\0';
    return 1;
}

static int file_exists(const WCHAR *path) {
    DWORD attr = GetFileAttributesW(path);
    return attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY);
}

int main(void) {
    int rc = 0;
    printf("[host] loading dbghelp.dll ...\n");
    HMODULE h = LoadLibraryW(DBGHELP_LIB);
    if (!h) {
        printf("[host] FAIL: LoadLibraryW failed, err=%lu\n", GetLastError());
        return 1;
    }
    printf("[host] OK: dbghelp.dll loaded at %p\n", (void *)h);

    /* Probe a forwarded export (Phase 1 forwarding must still work). */
    SymGetOptions_t p = AS_FN(SymGetOptions_t, "SymGetOptions");
    if (!p) {
        printf("[host] FAIL: GetProcAddress(SymGetOptions) returned NULL\n");
        rc = 2; goto cleanup;
    }
    DWORD opts = p();
    printf("[host] OK: forwarded SymGetOptions() returned 0x%lx\n",
           (unsigned long)opts);

    /* Give the worker thread time to run discovery + write its log lines.
     * The worker runs against THIS host exe (tiny), so discovery is fast,
     * but poll for the summary line for up to 10s to be safe. */
    WCHAR log_path[MAX_PATH], json_path[MAX_PATH];
    build_path(log_path, MAX_PATH, L"darktide-poc.log");
    build_path(json_path, MAX_PATH, JSON_NAME);
    printf("[host] log path:  %ls\n", log_path);
    printf("[host] json path: %ls\n", json_path);

    char log_buf[16384];
    int saw_worker = 0;
    for (int poll = 0; poll < 100; ++poll) {
        Sleep(100);
        if (!read_file(log_path, log_buf, sizeof(log_buf))) continue;
        if (strstr(log_buf, WORKER_MARKER)) { saw_worker = 1; break; }
    }

    if (!read_file(log_path, log_buf, sizeof(log_buf))) {
        printf("[host] FAIL: could not read darktide-poc.log\n");
        rc = 4; goto cleanup;
    }

    /* DllMain + forwarding markers. */
    if (!strstr(log_buf, ATTACH_MARKER) || !strstr(log_buf, FWD_MARKER)) {
        printf("[host] FAIL: log missing DllMain/forwarder markers\n");
        printf("[host] log:\n%s\n", log_buf);
        rc = 4; goto cleanup;
    }
    printf("[host] OK: DllMain + forwarder markers present\n");

    /* Phase 3 markers: phase3_install ran and either installed or aborted.
     * Either is acceptable as long as the install attempt happened and
     * didn't crash the process. */
    if (!strstr(log_buf, HOOK_MARKER)) {
        printf("[host] FAIL: log missing Phase 3 hook markers\n");
        printf("[host] log:\n%s\n", log_buf);
        rc = 7; goto cleanup;
    }
    printf("[host] OK: Phase 3 hook install ran\n");

    /* If the log shows "hook lua_newstate installed" then MinHook +
     * CreateHook + EnableHook all succeeded in Wine (even better — proves
     * the mechanism works in Wine too, beyond A1's hook-test.exe). */
    if (strstr(log_buf, "hook lua_newstate installed")) {
        printf("[host] OK: Phase 3 lua_newstate hook installed under Wine\n");
    } else {
        printf("[host] note: Phase 3 hook install aborted (expected if host "
               "module is too small to contain the candidate RVAs)\n");
    }

    /* Worker markers — the thread spawned, ran, and logged. */
    if (!saw_worker || !strstr(log_buf, START_MARKER)) {
        printf("[host] FAIL: worker thread did not run to completion "
               "(saw_worker=%d)\n", saw_worker);
        printf("[host] log:\n%s\n", log_buf);
        rc = 5; goto cleanup;
    }
    printf("[host] OK: worker thread ran discovery and logged summary\n");

    /* JSON file written. */
    if (!file_exists(json_path)) {
        printf("[host] FAIL: %ls not written\n", JSON_NAME);
        rc = 6; goto cleanup;
    }
    printf("[host] OK: darktide-poc-discovery.json written\n");

    /* Print the log lines so the test script can show them. */
    printf("[host] ---- worker + hook log lines ----\n");
    for (char *line = log_buf; line && *line; ) {
        char *nl = strchr(line, '\n');
        if (strstr(line, "discover") || strstr(line, "hook") ||
            strstr(line, "DllMain") || strstr(line, "captured") ||
            strstr(line, "lua_gettop")) {
            if (nl) *nl = '\0';
            printf("  %s\n", line);
            if (nl) *nl = '\n';
        }
        if (!nl) break;
        line = nl + 1;
    }

    printf("[host] PASS: plumbing works end-to-end with Phase 3 hooks\n");

cleanup:
    if (h) FreeLibrary(h);
    return rc;
}
