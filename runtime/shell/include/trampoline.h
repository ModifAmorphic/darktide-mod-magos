/*
 * trampoline.h — pure helpers for the runtime trampoline.
 *
 * The trampoline chunk (io.open a staged file -> read -> loadstring -> run) is
 * the proven engine-context mechanism (see dllmain.c's Phase-4 + production
 * notes). The production path joins DARKTIDE_MOD_STAGING + dml.lua into the
 * entry-file path; trampoline_build_chunk bakes that path into the chunk. Kept
 * separate from the hook-heavy dllmain.c so the pure logic is unit-testable
 * (compiled directly into the C test exes, like launcher.c's testable seams).
 */
#ifndef MAGOS_TRAMPOLINE_H
#define MAGOS_TRAMPOLINE_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Join a directory `dir` and a filename `name` into one Windows-canonical path
 * with exactly one backslash separator: if `dir` already ends in a backslash or
 * forward slash no separator is added, otherwise a single backslash is inserted.
 * (Backslash is the documented canonical separator — works on native Windows
 * and Proton alike.) Writes up to (out_cap - 1) chars + NUL to `out`. Returns
 * the path length (excluding NUL), or -1 on a NULL arg, zero cap, empty `dir`,
 * empty `name`, or overflow. Pure and side-effect-free.
 */
int trampoline_join_path(const char *dir, const char *name,
                         char *out, size_t out_cap);

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
