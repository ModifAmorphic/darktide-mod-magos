/* host.c — minimal Wine test harness for the dbghelp proxy DLL.
 *
 * Loads our dbghelp.dll, exercises a forwarded export, and verifies the
 * DllMain attach log line was written. This is the Tier A standalone
 * test (run under system wine, not the live game).
 *
 * Exit codes:
 *   0  all checks passed
 *   1  LoadLibrary of dbghelp.dll failed
 *   2  GetProcAddress for a required export failed (proxy didn't export it)
 *   3  forwarded call did not return a sane DWORD value
 *   4  SymInitialize forward returned FALSE on a NULL-hProcess probe
 *      (NULL is a valid handle meaning "current process" per the MS
 *      docs, so a forwarded call should succeed — FALSE means arg
 *      registers were corrupted by the tail jump)
 *   5  log file not found / did not contain expected attach marker
 */

#include <windows.h>
#include <stdio.h>

#define LOG_FILENAME L"darktide-poc.log"
#define ATTACH_MARKER "DllMain DLL_PROCESS_ATTACH"
#define DBGHELP_LIB   L"dbghelp.dll"

/* Cast GetProcAddress result to a specific function pointer type, going
 * through void* to silence -Wcast-function-type (the warning is correct
 * in general but here we are deliberately reinterpreting a function
 * pointer). */
#define AS_FN(type, name) ((type)(void *)GetProcAddress(h, name))

typedef DWORD (WINAPI *SymGetOptions_t)(void);
typedef BOOL  (WINAPI *SymInitialize_t)(HANDLE, PCWSTR, BOOL);

/* Resolve the path to darktide-poc.log next to this exe. The proxy
 * writes the log next to the DLL, and in the test layout the DLL sits
 * alongside the exe, so we look in the exe's directory rather than
 * relying on CWD (wine's CWD doesn't always match the exe's location). */
static void build_log_path(WCHAR *out, size_t out_sz) {
    DWORD len = GetModuleFileNameW(NULL, out, (DWORD)out_sz);
    if (len == 0 || len >= out_sz) {
        /* Fall back to a relative name if path resolution fails. */
        wcscpy(out, LOG_FILENAME);
        return;
    }
    /* Strip trailing filename, keep directory + separator. */
    DWORD i = len;
    while (i > 0 && out[i - 1] != L'\\' && out[i - 1] != L'/') {
        --i;
    }
    static const WCHAR log_name[] = L"darktide-poc.log";
    for (DWORD j = 0; log_name[j] != L'\0'; ++j) {
        out[i + j] = log_name[j];
    }
    out[i + (sizeof(log_name) / sizeof(WCHAR) - 1)] = L'\0';
}

static int read_log_text(const WCHAR *path, char *out, size_t out_sz) {
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        return 0;  /* not present / unreadable */
    }
    DWORD got = 0;
    BOOL ok = ReadFile(h, out, (DWORD)out_sz - 1, &got, NULL);
    CloseHandle(h);
    if (!ok) {
        return 0;
    }
    out[got] = '\0';
    return 1;
}

int main(void) {
    /* The log file is written next to the DLL itself (dbghelp.dll sits in
     * the same directory as this exe in the test layout), so we look for
     * darktide-poc.log in the current working directory. */
    char log_buf[4096];
    int rc = 0;

    printf("[host] loading dbghelp.dll ...\n");
    HMODULE h = LoadLibraryW(DBGHELP_LIB);
    if (!h) {
        printf("[host] FAIL: LoadLibraryW failed, GetLastError=%lu\n", GetLastError());
        return 1;
    }
    printf("[host] OK: dbghelp.dll loaded at %p\n", (void *)h);

    /* Probe 1: a no-arg forwarded export that returns a DWORD and is
     * guaranteed not to crash. SymGetOptions just reads a global. */
    SymGetOptions_t p_SymGetOptions = AS_FN(SymGetOptions_t, "SymGetOptions");
    if (!p_SymGetOptions) {
        printf("[host] FAIL: GetProcAddress(SymGetOptions) returned NULL\n");
        rc = 2; goto cleanup;
    }
    DWORD opts = p_SymGetOptions();
    printf("[host] OK: forwarded SymGetOptions() returned 0x%lx\n",
           (unsigned long)opts);

    /* Probe 2: a multi-arg forwarded export. SymInitialize(NULL, NULL, FALSE)
     * must return TRUE: per the MS docs, NULL is a valid hProcess meaning
     * "current process", and Wine's builtin honors that. If argument
     * registers were corrupted by the tail jump, the call would either
     * crash or return FALSE — TRUE proves RCX/RDX/R8/XMM carried through. */
    SymInitialize_t p_SymInitialize = AS_FN(SymInitialize_t, "SymInitialize");
    if (!p_SymInitialize) {
        printf("[host] FAIL: GetProcAddress(SymInitialize) returned NULL\n");
        rc = 2; goto cleanup;
    }
    BOOL init_ok = p_SymInitialize(NULL, NULL, FALSE);
    printf("[host] OK: forwarded SymInitialize(NULL,NULL,FALSE) returned %d\n",
           (int)init_ok);
    if (!init_ok) {
        printf("[host] FAIL: SymInitialize unexpectedly returned FALSE\n");
        rc = 4; goto cleanup;
    }

    /* Verify the log line was written by DllMain. The log lives next to
     * the DLL, which in the test layout is alongside this exe. */
    WCHAR log_path[MAX_PATH];
    build_log_path(log_path, sizeof(log_path) / sizeof(WCHAR));
    printf("[host] looking for log at: %ls\n", log_path);
    if (!read_log_text(log_path, log_buf, sizeof(log_buf))) {
        printf("[host] FAIL: could not read log file darktide-poc.log\n");
        rc = 5; goto cleanup;
    }
    if (!strstr(log_buf, ATTACH_MARKER)) {
        printf("[host] FAIL: log does not contain attach marker\n");
        printf("[host] log content:\n%s\n", log_buf);
        rc = 5; goto cleanup;
    }
    if (!strstr(log_buf, "[darktide-poc]")) {
        printf("[host] FAIL: log missing [darktide-poc] literal\n");
        rc = 5; goto cleanup;
    }

    printf("[host] OK: log contains attach marker.\n");
    printf("[host] log content:\n%s\n", log_buf);
    printf("[host] PASS: all checks succeeded\n");

cleanup:
    /* Freeing the library triggers DLL_PROCESS_DETACH, which appends the
     * detach line. The test script verifies that too. */
    if (h) {
        FreeLibrary(h);
    }
    return rc;
}

