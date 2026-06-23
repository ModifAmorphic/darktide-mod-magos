/* poc_log.c — thread-safe line logger (see poc_log.h). */
#include "poc_log.h"

#include <stdarg.h>
#include <stdio.h>

static CRITICAL_SECTION g_log_cs;
static int              g_log_cs_inited = 0;
static WCHAR            g_dll_dir[MAX_PATH];      /* directory with trailing sep */
static size_t           g_dll_dir_chars = 0;

void poc_log_init(HMODULE self) {
    InitializeCriticalSection(&g_log_cs);
    g_log_cs_inited = 1;

    WCHAR dll_path[MAX_PATH];
    DWORD len = GetModuleFileNameW(self, dll_path, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) {
        g_dll_dir[0] = L'\0';
        g_dll_dir_chars = 0;
        return;
    }
    /* Strip the trailing filename, keep the directory + separator. */
    DWORD i = len;
    while (i > 0 && dll_path[i - 1] != L'\\' && dll_path[i - 1] != L'/') {
        --i;
    }
    if (i + 1 >= MAX_PATH) {
        g_dll_dir[0] = L'\0';
        g_dll_dir_chars = 0;
        return;
    }
    for (DWORD k = 0; k < i; ++k) g_dll_dir[k] = dll_path[k];
    g_dll_dir[i] = L'\0';
    g_dll_dir_chars = i;
}

size_t poc_log_dir(WCHAR *out, size_t out_chars) {
    if (!g_dll_dir_chars || out_chars == 0) return 0;
    size_t n = g_dll_dir_chars;
    if (n >= out_chars) n = out_chars - 1;
    for (size_t k = 0; k < n; ++k) out[k] = g_dll_dir[k];
    out[n] = L'\0';
    return n;
}

/* Build "<dir>darktide-poc.log" into out. Returns total length, 0 on fail. */
static size_t build_log_path(WCHAR *out, size_t out_chars) {
    if (g_dll_dir_chars + 20 >= out_chars) return 0;
    for (size_t k = 0; k < g_dll_dir_chars; ++k) out[k] = g_dll_dir[k];
    static const WCHAR name[] = L"darktide-poc.log";
    for (size_t k = 0; name[k] != L'\0'; ++k)
        out[g_dll_dir_chars + k] = name[k];
    out[g_dll_dir_chars + (sizeof(name) / sizeof(WCHAR) - 1)] = L'\0';
    return g_dll_dir_chars + (sizeof(name) / sizeof(WCHAR) - 1);
}

void poc_log_linef(const char *fmt, ...) {
    if (!g_log_cs_inited) return;

    char body[384];
    va_list ap;
    va_start(ap, fmt);
    int body_n = vsnprintf(body, sizeof(body), fmt, ap);
    va_end(ap);
    if (body_n < 0) return;
    if ((size_t)body_n >= sizeof(body)) body_n = (int)sizeof(body) - 1;

    SYSTEMTIME st;
    GetSystemTime(&st);
    char line[512];
    int n = snprintf(
        line, sizeof(line),
        "[darktide-poc] %.*s pid=%lu ts=%04d-%02d-%02dT%02d:%02d:%02dZ\n",
        body_n, body,
        (unsigned long)GetCurrentProcessId(),
        st.wYear, st.wMonth, st.wDay,
        st.wHour, st.wMinute, st.wSecond);
    if (n <= 0) return;
    DWORD to_write;
    if ((DWORD)n >= sizeof(line)) {
        to_write = (DWORD)sizeof(line) - 1;
        line[to_write - 1] = '\n';
    } else {
        to_write = (DWORD)n;
    }

    WCHAR log_path[MAX_PATH];
    if (build_log_path(log_path, MAX_PATH) == 0) return;

    /* CRITICAL_SECTION around the open/write/close so concurrent lines
     * from DllMain and the worker never interleave. (WriteFile to a
     * FILE_APPEND_DATA handle is already atomic for the offset, but the
     * mutex makes the line a clean atomic unit regardless of FS quirks.) */
    EnterCriticalSection(&g_log_cs);
    HANDLE h = CreateFileW(log_path, FILE_APPEND_DATA,
                           FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        WriteFile(h, line, to_write, &written, NULL);
        CloseHandle(h);
    }
    LeaveCriticalSection(&g_log_cs);
}
