/*
 * trampoline.h — pure helpers for the Phase-4 trampoline prototype.
 *
 * The trampoline chunk (io.open a staged file -> read -> loadstring -> run) is
 * the definitive engine-context mechanism test (see dllmain.c's Phase-4 note).
 * These helpers build that chunk from a test-file path, escaping the path for a
 * Lua double-quoted string. Kept separate from the hook-heavy dllmain.c so the
 * pure logic is unit-testable (compiled directly into the C test exes, like
 * launcher.c's testable seams).
 */
#ifndef MAGOS_TRAMPOLINE_H
#define MAGOS_TRAMPOLINE_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Escape `path` (length `path_len`) into a Lua double-quoted-string-safe form:
 * backslash and double-quote are doubled (so the Lua parser yields the original
 * byte sequence). Forward slashes and all other bytes pass through unchanged.
 * Writes up to (out_cap - 1) chars + NUL to `out`. Returns the number of chars
 * written (excluding NUL), or -1 on a NULL arg, zero cap, or overflow.
 *
 * Pure and side-effect-free.
 */
int trampoline_escape_path(const char *path, size_t path_len,
                           char *out, size_t out_cap);

/*
 * Build the trampoline Lua chunk with `path` baked in (escaped). The chunk:
 *
 *   local f, err = io.open("<path>", "r")
 *   if not f then return "FAIL io.open: " .. tostring(err) end
 *   local data = f:read("*all"); f:close()
 *   local fn, lerr = loadstring(data)
 *   if not fn then return "FAIL loadstring: " .. tostring(lerr) end
 *   local ok, rerr = pcall(fn)
 *   if not ok then return "FAIL run: " .. tostring(rerr) end
 *   return "OK"
 *
 * It returns a status string: "OK" if every step succeeded, else "FAIL <step>:
 * <err>" identifying which step broke. Writes the NUL-terminated chunk to `out`.
 * Returns the chunk length (excluding NUL), or -1 on a NULL arg, zero cap,
 * empty path, or overflow. Pure and side-effect-free.
 */
int trampoline_build_chunk(const char *path, char *out, size_t out_cap);

#ifdef __cplusplus
}
#endif

#endif /* MAGOS_TRAMPOLINE_H */
