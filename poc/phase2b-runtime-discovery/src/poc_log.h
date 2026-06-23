/* poc_log.h — thread-safe line logger shared by DllMain and the worker.
 *
 * Phase 1 wrote to darktide-poc.log only from DllMain (single-threaded,
 * under the loader lock). Phase 2b adds a worker thread that also writes
 * discovery results to the same log, so writes must be serialized. This
 * module wraps the line-write in a CRITICAL_SECTION and exposes the DLL's
 * directory so the worker can place darktide-poc-discovery.json next to it.
 *
 * Constraints (see discover_worker.c):
 *   - poc_log_init runs once, inside DllMain (loader lock held). It only
 *     does InitializeCriticalSection and GetModuleFileNameW — both safe.
 *   - poc_log_line / poc_log_linef run from any thread, including the
 *     worker (which holds no loader lock). Each call opens the file
 *     APPEND, writes one line, closes — line-atomic and crash-safe.
 */
#ifndef DARKTIDE_POC_LOG_H
#define DARKTIDE_POC_LOG_H

#include <windows.h>

/* Capture the DLL module handle and initialize the log mutex.
 * Call exactly once from DllMain on DLL_PROCESS_ATTACH. */
void poc_log_init(HMODULE self);

/* Append one formatted line + "\n" to darktide-poc.log. Thread-safe.
 * The line is prefixed with "[darktide-poc] " and suffixed with
 * "pid=<pid> ts=<ISO8601-UTC>". fmt must produce <= ~200 chars. */
void poc_log_linef(const char *fmt, ...);

/* Copy the DLL's directory (with trailing separator) into out.
 * Returns the length written (excluding NUL), or 0 on failure.
 * Call AFTER poc_log_init. Thread-safe (the path is fixed at init). */
size_t poc_log_dir(WCHAR *out, size_t out_chars);

#endif /* DARKTIDE_POC_LOG_H */
