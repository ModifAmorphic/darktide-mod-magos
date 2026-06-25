/*
 * test_config.c — Unit tests for the launcher's config model.
 *
 * Validates the two guarantees of the rewrite:
 *   1. magos_parse_args: --flag <value> pairs populate the right fields;
 *      unknown flag / missing value / -h / --help return the right codes.
 *   2. magos_resolve_config: every setting follows flag > env > default,
 *      and DARKTIDE_MOD_PATH resolves to NULL when unset.
 *
 * resolve_config() writes env/default values into resolver-owned static
 * buffers that are reused on each call, so each test copies the value into a
 * local before re-resolving.
 */
#include "test_runner.h"
#include "../launcher/src/launcher.h"
#include <windows.h>
#include <stdio.h>
#include <string.h>

/* Env var names mirrored from launcher.c (kept private there, so redefine here
 * only to clean up state between resolve tests). */
#define ENV_GAME_BINARY  "MAGOS_ENGINSEER_GAME_BINARY"
#define ENV_SHELL        "MAGOS_ENGINSEER_SHELL"
#define ENV_MOD_PATH     "DARKTIDE_MOD_PATH"
#define ENV_LOG_FILE     "MAGOS_ENGINSEER_LOG_FILE"
#define ENV_LOG_LEVEL    "MAGOS_ENGINSEER_LOG_LEVEL"
#define ENV_STEAM_APP_ID "MAGOS_ENGINSEER_STEAM_APP_ID"

static void clear_env(void) {
    SetEnvironmentVariableA(ENV_GAME_BINARY, NULL);
    SetEnvironmentVariableA(ENV_SHELL, NULL);
    SetEnvironmentVariableA(ENV_MOD_PATH, NULL);
    SetEnvironmentVariableA(ENV_LOG_FILE, NULL);
    SetEnvironmentVariableA(ENV_LOG_LEVEL, NULL);
    SetEnvironmentVariableA(ENV_STEAM_APP_ID, NULL);
}

/* ---- parse_args ---- */

void test_parse_all_flags(void) {
    char *argv[] = {"prog",
        "--game-binary", "G", "--magos-shell", "S", "--mod-path", "M",
        "--log-file", "L", "--log-level", "trace", "--steam-app-id", "42"};
    magos_parsed_args a;
    ASSERT_EQ(0, magos_parse_args(13, argv, &a));
    ASSERT_STREQ("G", a.game_binary);
    ASSERT_STREQ("S", a.magos_shell);
    ASSERT_STREQ("M", a.mod_path);
    ASSERT_STREQ("L", a.log_file);
    ASSERT_STREQ("trace", a.log_level);
    ASSERT_STREQ("42", a.steam_app_id);
}

void test_parse_none(void) {
    char *argv[] = {"prog"};
    magos_parsed_args a;
    ASSERT_EQ(0, magos_parse_args(1, argv, &a));
    ASSERT_TRUE(a.game_binary == NULL);
    ASSERT_TRUE(a.magos_shell == NULL);
    ASSERT_TRUE(a.mod_path == NULL);
    ASSERT_TRUE(a.log_file == NULL);
    ASSERT_TRUE(a.log_level == NULL);
    ASSERT_TRUE(a.steam_app_id == NULL);
}

void test_parse_help_short(void) {
    char *argv[] = {"prog", "-h"};
    magos_parsed_args a;
    ASSERT_EQ(-2, magos_parse_args(2, argv, &a));
}

void test_parse_help_long(void) {
    char *argv[] = {"prog", "--help"};
    magos_parsed_args a;
    ASSERT_EQ(-2, magos_parse_args(2, argv, &a));
}

void test_parse_unknown_flag(void) {
    char *argv[] = {"prog", "--bogus", "x"};
    magos_parsed_args a;
    ASSERT_EQ(-1, magos_parse_args(3, argv, &a));
}

void test_parse_missing_value(void) {
    char *argv[] = {"prog", "--game-binary"};
    magos_parsed_args a;
    ASSERT_EQ(-1, magos_parse_args(2, argv, &a));
}

/* ---- resolve_config: flag > env > default ---- */

void test_resolve_flag_wins(void) {
    clear_env();
    SetEnvironmentVariableA(ENV_SHELL, "ENV_SHELL");
    SetEnvironmentVariableA(ENV_LOG_LEVEL, "warn");
    SetEnvironmentVariableA(ENV_STEAM_APP_ID, "111");

    magos_parsed_args a = {0};
    a.magos_shell = "FLAG_SHELL";
    a.log_level = "FLAG_LEVEL";
    a.steam_app_id = "FLAG_ID";
    a.game_binary = "FLAG_GAME";

    magos_config cfg;
    magos_resolve_config(&a, &cfg);
    ASSERT_STREQ("FLAG_GAME", cfg.game_binary);
    ASSERT_STREQ("FLAG_SHELL", cfg.magos_shell);
    ASSERT_STREQ("FLAG_LEVEL", cfg.log_level);
    ASSERT_STREQ("FLAG_ID", cfg.steam_app_id);

    clear_env();
}

void test_resolve_env_when_no_flag(void) {
    clear_env();
    SetEnvironmentVariableA(ENV_GAME_BINARY, "ENV_GAME");
    SetEnvironmentVariableA(ENV_SHELL, "ENV_SHELL");
    SetEnvironmentVariableA(ENV_MOD_PATH, "ENV_MOD");
    SetEnvironmentVariableA(ENV_LOG_FILE, "ENV_LOG");
    SetEnvironmentVariableA(ENV_LOG_LEVEL, "debug");
    SetEnvironmentVariableA(ENV_STEAM_APP_ID, "222");

    magos_parsed_args a = {0};
    magos_config cfg;
    magos_resolve_config(&a, &cfg);
    ASSERT_STREQ("ENV_GAME", cfg.game_binary);
    ASSERT_STREQ("ENV_SHELL", cfg.magos_shell);
    ASSERT_STREQ("ENV_MOD", cfg.mod_path);
    ASSERT_STREQ("ENV_LOG", cfg.log_file);
    ASSERT_STREQ("debug", cfg.log_level);
    ASSERT_STREQ("222", cfg.steam_app_id);

    clear_env();
}

void test_resolve_defaults_when_nothing_set(void) {
    clear_env();
    magos_parsed_args a = {0};
    magos_config cfg;
    magos_resolve_config(&a, &cfg);
    /* game_binary has no default: must be NULL (main() rejects this). */
    ASSERT_TRUE(cfg.game_binary == NULL);
    /* mod_path is optional: NULL when unset. */
    ASSERT_TRUE(cfg.mod_path == NULL);
    /* log_level + steam_app_id have literal defaults. */
    ASSERT_STREQ("info", cfg.log_level);
    ASSERT_STREQ("1361210", cfg.steam_app_id);
    /* shell + log_file default to <launcher-dir>\<name>: can't know the dir
     * here, but they must end with the right leaf and be non-empty. */
    ASSERT_TRUE(cfg.magos_shell != NULL);
    ASSERT_TRUE(strlen(cfg.magos_shell) > 0);
    ASSERT_TRUE(strstr(cfg.magos_shell, "magos_shell.dll") != NULL);
    ASSERT_TRUE(cfg.log_file != NULL);
    ASSERT_TRUE(strlen(cfg.log_file) > 0);
    ASSERT_TRUE(strstr(cfg.log_file, "magos_enginseer.log") != NULL);
    clear_env();
}

void test_resolve_mod_path_unset_is_null_with_flag_present(void) {
    /* mod_path stays optional even when other settings come from flags. */
    clear_env();
    magos_parsed_args a = {0};
    a.game_binary = "G";
    magos_config cfg;
    magos_resolve_config(&a, &cfg);
    ASSERT_TRUE(cfg.mod_path == NULL);
    clear_env();
}

int main(void) {
    test_register("parse_all_flags", test_parse_all_flags);
    test_register("parse_none", test_parse_none);
    test_register("parse_help_short", test_parse_help_short);
    test_register("parse_help_long", test_parse_help_long);
    test_register("parse_unknown_flag", test_parse_unknown_flag);
    test_register("parse_missing_value", test_parse_missing_value);
    test_register("resolve_flag_wins", test_resolve_flag_wins);
    test_register("resolve_env_when_no_flag", test_resolve_env_when_no_flag);
    test_register("resolve_defaults_when_nothing_set",
                  test_resolve_defaults_when_nothing_set);
    test_register("resolve_mod_path_unset_is_null_with_flag_present",
                  test_resolve_mod_path_unset_is_null_with_flag_present);
    return test_summary();
}
