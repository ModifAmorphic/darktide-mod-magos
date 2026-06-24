/*
 * trampoline.c — pure helpers for the Phase-4 trampoline prototype.
 *
 * Implementation of the helpers declared in trampoline.h. See that header (and
 * dllmain.c's Phase-4 note) for what the trampoline proves. This file has NO
 * Windows, Lua, or hook dependencies — only string ops — so it compiles
 * directly into both the shell DLL and the C unit-test exes.
 */
#include "trampoline.h"

#include <stdio.h>
#include <string.h>

/* The trampoline chunk template. `%s` receives the escaped test-file path. The
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
