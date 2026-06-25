/*
 * trampoline.c — pure helpers for the runtime trampoline.
 *
 * Implementation of the helpers declared in trampoline.h. The trampoline chunk
 * (set MAGOS_MOD_PATH -> io.open a staged file -> read -> loadstring -> run) is
 * the proven engine-context mechanism (see dllmain.c's Phase-4 + production
 * notes). The production path joins DARKTIDE_MOD_PATH + enginseer.lua into
 * the entry path and feeds both the raw mod path AND the joined entry path
 * to trampoline_build_chunk; the chunk sets MAGOS_MOD_PATH first, then opens the
 * entry file. This file has NO Windows, Lua, or hook dependencies — only string
 * ops — so it compiles directly into both the shell DLL and the C unit-test exes.
 */
#include "trampoline.h"

#include <stdio.h>
#include <string.h>

/* The trampoline chunk template. The first `%s` receives the escaped mod path
 * (set as MAGOS_MOD_PATH); the second `%s` receives the escaped entry-file
 * path (opened + loaded + run). The chunk returns "OK" or a "FAIL <step>:
 * <err>" status string. Verbatim step order from the Phase-4 spec (io.open ->
 * read -> loadstring -> run), guarded at each step so the only way it
 * propagates an error is an unguarded step (e.g. f:read, which the outer
 * pcall then catches and reports as CHUNK PCALL FAILED). The MAGOS_MOD_PATH
 * assignment is the only addition over the Phase-4 prototype — a path handoff
 * to the Enginseer, not a Lua-facility shim. */
static const char TRAMPOLINE_CHUNK_FMT[] =
    "MAGOS_MOD_PATH = \"%s\"\n"
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

int trampoline_build_chunk(const char *staging, const char *entry_path,
                           char *out, size_t out_cap) {
    if (!staging || !entry_path || !out || out_cap == 0) return -1;
    size_t staging_len = strlen(staging);
    size_t entry_len = strlen(entry_path);
    if (staging_len == 0 || entry_len == 0) return -1;  /* empty is a misconfig */

    /* Escape both args for a Lua double-quoted string. */
    char esc_staging[2048];
    int sn = trampoline_escape_path(staging, staging_len, esc_staging, sizeof(esc_staging));
    if (sn < 0) return -1;

    char esc_entry[2048];
    int en = trampoline_escape_path(entry_path, entry_len, esc_entry, sizeof(esc_entry));
    if (en < 0) return -1;

    int n = snprintf(out, out_cap, TRAMPOLINE_CHUNK_FMT, esc_staging, esc_entry);
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
