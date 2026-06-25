/*
 * test_trampoline.c — Unit tests for the Phase-4 trampoline pure helpers.
 *
 * Covers trampoline_escape_path (the Windows-path -> Lua-string escape) and
 * trampoline_build_chunk (the full chunk assembly). These run via wine like the
 * other C tests; they compile trampoline.c directly (no Lua/Windows deps).
 */
#include "test_runner.h"
#include "../shell/src/trampoline.c"  /* compile the pure impl directly */
#include <stdio.h>
#include <string.h>

/* ---- trampoline_escape_path ---- */

void test_escape_plain_path(void) {
    char out[64];
    int n = trampoline_escape_path("C:/tmp/x.lua", 12, out, sizeof(out));
    ASSERT_EQ(12, n);
    ASSERT_STREQ("C:/tmp/x.lua", out);  /* forward slashes unchanged */
}

void test_escape_backslashes_doubled(void) {
    /* Windows path: every backslash doubles in a Lua double-quoted string. */
    char out[64];
    int n = trampoline_escape_path("Z:\\foo\\bar.lua", 14, out, sizeof(out));
    ASSERT_EQ(16, n);  /* 14 + 2 extra (two backslashes doubled) */
    ASSERT_STREQ("Z:\\\\foo\\\\bar.lua", out);
}

void test_escape_quote_doubled(void) {
    char out[64];
    int n = trampoline_escape_path("a\"b", 3, out, sizeof(out));
    ASSERT_EQ(4, n);
    ASSERT_STREQ("a\\\"b", out);
}

void test_escape_empty_path(void) {
    char out[8];
    int n = trampoline_escape_path("", 0, out, sizeof(out));
    ASSERT_EQ(0, n);
    ASSERT_STREQ("", out);
}

void test_escape_overflow_returns_neg1(void) {
    /* 2 backslashes -> 4 escaped bytes + NUL = 5; cap of 4 must reject. */
    char out[4];
    int n = trampoline_escape_path("\\\\", 2, out, sizeof(out));
    ASSERT_EQ(-1, n);
}

void test_escape_null_args(void) {
    char out[8];
    ASSERT_EQ(-1, trampoline_escape_path(NULL, 0, out, sizeof(out)));
    ASSERT_EQ(-1, trampoline_escape_path("a", 1, NULL, sizeof(out)));
    ASSERT_EQ(-1, trampoline_escape_path("a", 1, out, 0));
}

/* ---- trampoline_build_chunk ---- */

void test_build_chunk_sets_mod_path_global(void) {
    /* The chunk sets MAGOS_MOD_PATH (escaped mod path) then opens the entry
     * file (escaped joined path). Both must appear. */
    char out[1024];
    int n = trampoline_build_chunk("Z:\\staging", "Z:\\staging\\t.lua", out, sizeof(out));
    ASSERT_TRUE(n > 0);

    /* MAGOS_MOD_PATH global set from the mod path, escaped. */
    ASSERT_NOTNULL(strstr(out, "MAGOS_MOD_PATH = \"Z:\\\\staging\""));
    /* Entry path baked into io.open(...), escaped. */
    ASSERT_NOTNULL(strstr(out, "io.open(\"Z:\\\\staging\\\\t.lua\", \"r\")"));
    /* Each FAIL step label is present (defines the status vocabulary). */
    ASSERT_NOTNULL(strstr(out, "FAIL io.open:"));
    ASSERT_NOTNULL(strstr(out, "FAIL loadstring:"));
    ASSERT_NOTNULL(strstr(out, "FAIL run:"));
    /* Success path returns OK. */
    ASSERT_NOTNULL(strstr(out, "return \"OK\""));
}

void test_build_chunk_plain_entry_path(void) {
    /* Forward-slash mod path + entry need no escaping. */
    char out[1024];
    int n = trampoline_build_chunk("/tmp", "/tmp/x.lua", out, sizeof(out));
    ASSERT_TRUE(n > 0);
    ASSERT_NOTNULL(strstr(out, "MAGOS_MOD_PATH = \"/tmp\""));
    ASSERT_NOTNULL(strstr(out, "io.open(\"/tmp/x.lua\", \"r\")"));
}

void test_build_chunk_empty_staging_rejected(void) {
    char out[64];
    ASSERT_EQ(-1, trampoline_build_chunk("", "Z:\\t.lua", out, sizeof(out)));
}

void test_build_chunk_empty_entry_rejected(void) {
    char out[64];
    ASSERT_EQ(-1, trampoline_build_chunk("Z:\\staging", "", out, sizeof(out)));
}

void test_build_chunk_null_args(void) {
    char out[64];
    ASSERT_EQ(-1, trampoline_build_chunk(NULL, "Z:\\t.lua", out, sizeof(out)));
    ASSERT_EQ(-1, trampoline_build_chunk("Z:\\staging", NULL, out, sizeof(out)));
    ASSERT_EQ(-1, trampoline_build_chunk("Z:\\staging", "Z:\\t.lua", NULL, sizeof(out)));
    ASSERT_EQ(-1, trampoline_build_chunk("Z:\\staging", "Z:\\t.lua", out, 0));
}

void test_build_chunk_overflow(void) {
    /* A tiny buffer cannot hold the chunk -> reject, no partial write relied on. */
    char out[8];
    int n = trampoline_build_chunk("Z:\\staging", "Z:\\staging\\t.lua", out, sizeof(out));
    ASSERT_EQ(-1, n);
}

void test_build_chunk_round_trips_long_path(void) {
    /* A realistically long Windows staging + entry still fits the default cap. */
    const char *staging = "Z:\\very\\deep\\path\\to\\a\\staged\\mod\\dir";
    const char *entry   = "Z:\\very\\deep\\path\\to\\a\\staged\\mod\\dir\\file.lua";
    char out[1024];
    int n = trampoline_build_chunk(staging, entry, out, sizeof(out));
    ASSERT_TRUE(n > 0);
    /* Every backslash in the original is doubled in the baked chunk. */
    ASSERT_NOTNULL(strstr(out, "Z:\\\\very\\\\deep\\\\path"));
}

/* ---- trampoline_join_path ---- */

void test_join_basic_no_trailing_sep(void) {
    char out[64];
    int n = trampoline_join_path("Z:\\staging", "dml.lua", out, sizeof(out));
    ASSERT_EQ(18, n);  /* "Z:\staging"(10) + "\"(1) + "dml.lua"(7) */
    ASSERT_STREQ("Z:\\staging\\dml.lua", out);  /* one backslash inserted */
}

void test_join_trailing_backslash_idempotent(void) {
    /* dir already ends in backslash -> no double separator. */
    char out[64];
    int n = trampoline_join_path("Z:\\staging\\", "dml.lua", out, sizeof(out));
    ASSERT_EQ(18, n);  /* "Z:\staging\"(11) + "dml.lua"(7), no extra sep */
    ASSERT_STREQ("Z:\\staging\\dml.lua", out);
}

void test_join_trailing_fwdslash_accepted(void) {
    /* A trailing forward slash is tolerated as an already-present separator. */
    char out[64];
    int n = trampoline_join_path("Z:/staging/", "dml.lua", out, sizeof(out));
    ASSERT_EQ(18, n);  /* "Z:/staging/"(11) + "dml.lua"(7), no extra sep */
    ASSERT_STREQ("Z:/staging/dml.lua", out);
}

void test_join_empty_dir_rejected(void) {
    char out[8];
    ASSERT_EQ(-1, trampoline_join_path("", "dml.lua", out, sizeof(out)));
}

void test_join_empty_name_rejected(void) {
    char out[8];
    ASSERT_EQ(-1, trampoline_join_path("Z:\\staging", "", out, sizeof(out)));
}

void test_join_null_args(void) {
    char out[8];
    ASSERT_EQ(-1, trampoline_join_path(NULL, "dml.lua", out, sizeof(out)));
    ASSERT_EQ(-1, trampoline_join_path("Z:\\staging", NULL, out, sizeof(out)));
    ASSERT_EQ(-1, trampoline_join_path("Z:\\staging", "dml.lua", NULL, sizeof(out)));
    ASSERT_EQ(-1, trampoline_join_path("Z:\\staging", "dml.lua", out, 0));
}

void test_join_overflow_returns_neg1(void) {
    /* need = "Z:\staging"(10) + "\"(1) + "dml.lua"(7) = 18; +NUL = 19. cap 18 rejects. */
    char out[18];
    int n = trampoline_join_path("Z:\\staging", "dml.lua", out, sizeof(out));
    ASSERT_EQ(-1, n);
}

void test_join_feeds_build_chunk(void) {
    /* End-to-end: join -> build_chunk must bake the joined, escaped entry path.
     * The staging dir is passed separately (and is also the join prefix of the
     * entry path — intentional). */
    char path[128];
    int jn = trampoline_join_path("Z:\\staging", "dml.lua", path, sizeof(path));
    ASSERT_TRUE(jn > 0);

    char chunk[1024];
    int cn = trampoline_build_chunk("Z:\\staging", path, chunk, sizeof(chunk));
    ASSERT_TRUE(cn > 0);
    ASSERT_NOTNULL(strstr(chunk, "io.open(\"Z:\\\\staging\\\\dml.lua\", \"r\")"));
}

int main(void) {
    test_register("escape_plain_path", test_escape_plain_path);
    test_register("escape_backslashes_doubled", test_escape_backslashes_doubled);
    test_register("escape_quote_doubled", test_escape_quote_doubled);
    test_register("escape_empty_path", test_escape_empty_path);
    test_register("escape_overflow_returns_neg1", test_escape_overflow_returns_neg1);
    test_register("escape_null_args", test_escape_null_args);
    test_register("build_chunk_sets_mod_path_global", test_build_chunk_sets_mod_path_global);
    test_register("build_chunk_plain_entry_path", test_build_chunk_plain_entry_path);
    test_register("build_chunk_empty_staging_rejected", test_build_chunk_empty_staging_rejected);
    test_register("build_chunk_empty_entry_rejected", test_build_chunk_empty_entry_rejected);
    test_register("build_chunk_null_args", test_build_chunk_null_args);
    test_register("build_chunk_overflow", test_build_chunk_overflow);
    test_register("build_chunk_round_trips_long_path", test_build_chunk_round_trips_long_path);
    test_register("join_basic_no_trailing_sep", test_join_basic_no_trailing_sep);
    test_register("join_trailing_backslash_idempotent", test_join_trailing_backslash_idempotent);
    test_register("join_trailing_fwdslash_accepted", test_join_trailing_fwdslash_accepted);
    test_register("join_empty_dir_rejected", test_join_empty_dir_rejected);
    test_register("join_empty_name_rejected", test_join_empty_name_rejected);
    test_register("join_null_args", test_join_null_args);
    test_register("join_overflow_returns_neg1", test_join_overflow_returns_neg1);
    test_register("join_feeds_build_chunk", test_join_feeds_build_chunk);
    return test_summary();
}
