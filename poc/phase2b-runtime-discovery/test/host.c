/* host.c — Phase 2b Tier A2: DLL plumbing smoke test.
 *
 * Loads the built dbghelp.dll under Wine and verifies the FULL plumbing:
 *   - DllMain attach + forwarders resolved (Phase 1 forwarding intact)
 *   - a forwarded export still returns a sane value
 *   - the worker thread spawns, runs discovery, and writes its log lines
 *   - darktide-poc-discovery.json is written
 *
 * In this test the worker runs discovery against THIS host exe's main
 * module (not Darktide.exe), so it finds no LuaJIT anchors. The expected
 * outcome is: discovery runs to completion without crashing, logs a
 * summary with 0 matches (all unresolved), and writes the JSON file.
 * Correctness is covered by A1 (in_memory_host); A2 only proves the
 * plumbing (DllMain → CreateThread → dt_discover → log + JSON).
 *
 * Exit codes:
 *   0  all plumbing checks passed
 *   1  LoadLibrary failed
 *   2  GetProcAddress for a forwarded export failed
 *   3  forwarded call did not return a sane value
 *   4  log file missing required DllMain/forwarder markers
 *   5  log file missing worker/discover markers (thread didn't run)
 *   6  darktide-poc-discovery.json not written
 */
#include <windows.h>
#include <stdio.h>

#define DBGHELP_LIB   L"dbghelp.dll"
#define ATTACH_MARKER "DllMain DLL_PROCESS_ATTACH"
#define FWD_MARKER    "DllMain forwarders resolved="
#define WORKER_MARKER "discover summary"
#define START_MARKER  "discover start"
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
     * The worker runs against THIS host exe (tiny), so discovery is fast
     * (<<1s), but poll for the summary line for up to 10s to be safe. */
    WCHAR log_path[MAX_PATH], json_path[MAX_PATH];
    build_path(log_path, MAX_PATH, L"darktide-poc.log");
    build_path(json_path, MAX_PATH, JSON_NAME);
    printf("[host] log path:  %ls\n", log_path);
    printf("[host] json path: %ls\n", json_path);

    char log_buf[8192];
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

    /* Print the worker's lines so the test script can show them. */
    printf("[host] ---- worker log lines ----\n");
    for (char *line = log_buf; line && *line; ) {
        char *nl = strchr(line, '\n');
        if (strstr(line, "discover")) {
            if (nl) *nl = '\0';
            printf("  %s\n", line);
            if (nl) *nl = '\n';
        }
        if (!nl) break;
        line = nl + 1;
    }

    printf("[host] PASS: plumbing works end-to-end\n");

cleanup:
    if (h) FreeLibrary(h);
    return rc;
}
