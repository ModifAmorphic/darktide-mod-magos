/*
 * trampoline.c — pure helpers for the runtime trampoline.
 *
 * Implementation of the helpers declared in trampoline.h. The trampoline chunk
 * (io.open a staged file -> read -> loadstring -> run) is the proven engine-
 * context mechanism (see dllmain.c's Phase-4 + production notes). The production
 * path joins DARKTIDE_MOD_STAGING + enginseer.lua and feeds it to trampoline_build_
 * chunk; the chunk template itself is unchanged from the Phase-4 prototype that
 * validated the mechanism live. This file has NO Windows, Lua, or hook
 * dependencies — only string ops — so it compiles directly into both the shell
 * DLL and the C unit-test exes.
 */
#include "trampoline.h"

#include <stdio.h>
#include <string.h>

/* The trampoline chunk template. `%s` receives the escaped entry-file path. The
 * chunk returns "OK" or a "FAIL <step>: <err>" status string. Verbatim from the
 * Phase-4 spec (io.open -> read -> loadstring -> run), guarded at each step so
 * the only way it propagates an error is an unguarded step (e.g. f:read, which
 * the outer pcall then catches and reports as CHUNK PCALL FAILED). */
static const char TRAMPOLINE_CHUNK_FMT[] =
    "local f, err = io.open(\"%s\", \"r\")\n"
    "if not f then return \"FAIL io.open: \" .. tostring(err) end\n"
    "local data = f:read(\"*all\"); f:close()\n"
    "local fn, lerr = loadstring(data)\n"
    "if not fn then return \"FAIL loadstring: \" .. tostring(lerr) end\n"
    "local ok, rerr = pcall(fn)\n"
    "if not ok then return \"FAIL run: \" .. tostring(rerr) end\n"
    "return \"OK\"\n";

int trampoline_escape_path(const char *path, size_t path_len,
                           char *out, size_t out_cap) {
    if (!path || !out || out_cap == 0) return -1;
    size_t off = 0;
    for (size_t i = 0; i < path_len; i++) {
        char c = path[i];
        size_t need = (c == '\\' || c == '"') ? 2 : 1;
        if (off + need + 1 > out_cap) return -1;  /* +1 for the NUL */
        if (c == '\\' || c == '"') out[off++] = '\\';
        out[off++] = c;
    }
    out[off] = '\0';
    return (int)off;
}

int trampoline_build_chunk(const char *path, char *out, size_t out_cap) {
    if (!path || !out || out_cap == 0) return -1;
    size_t path_len = strlen(path);
    if (path_len == 0) return -1;  /* empty path is a misconfig */

    /* Escape the path for a Lua double-quoted string. */
    char escaped[2048];
    int en = trampoline_escape_path(path, path_len, escaped, sizeof(escaped));
    if (en < 0) return -1;

    int n = snprintf(out, out_cap, TRAMPOLINE_CHUNK_FMT, escaped);
    if (n < 0 || (size_t)n >= out_cap) return -1;  /* encoding error or overflow */
    return n;
}

int trampoline_join_path(const char *dir, const char *name,
                         char *out, size_t out_cap) {
    if (!dir || !name || !out || out_cap == 0) return -1;
    size_t dlen = strlen(dir);
    size_t nlen = strlen(name);
    if (dlen == 0 || nlen == 0) return -1;  /* empty dir/name is a misconfig */

    /* Exactly one separator: skip it if `dir` already ends in one. Backslash is
     * the canonical Windows form (also accepted by Proton); a trailing forward
     * slash is tolerated as an already-present separator. */
    int has_sep = (dir[dlen - 1] == '\\' || dir[dlen - 1] == '/');
    size_t need = dlen + (has_sep ? 0 : 1) + nlen;
    if (need + 1 > out_cap) return -1;  /* +1 for the NUL */

    size_t off = 0;
    memcpy(out + off, dir, dlen);   off += dlen;
    if (!has_sep) out[off++] = '\\';
    memcpy(out + off, name, nlen);  off += nlen;
    out[off] = '\0';
    return (int)off;
}
