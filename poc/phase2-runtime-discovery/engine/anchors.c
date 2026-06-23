/*
 * anchors.c - Anchor tables (Phase 0 spec, verbatim transcription).
 *
 * Source: poc/phase0-offline-discovery/discover.py ANCHORS_S3 and
 * ERROR_STRINGS_S5. Pinned to the 18,715,784-byte build; the offsets are
 * file offsets within Darktide.exe on disk.
 */
#include "engine.h"

const dt_anchor_def_t dt_anchors_s3[] = {
    {"LuaJIT version",         0x00E8B108, "LuaJIT 2.1.1771479498"},
    {"lua_panic",              0x00F1F698, "stingray::LuaEnvironment::Internal::lua_panic"},
    {"default_error_callback", 0x00F1F910, "stingray::LuaEnvironment::Internal::default_error_callback"},
    {"clear_temp_variables",   0x00F1FA48, "stingray::LuaEnvironment::clear_temp_variables"},
    {"dump_state",             0x00F1FC98, "LuaEnvironment::dump_state"},
    {"lua_resource::bytecode", 0x00F1D9D0, "stingray::lua_resource::bytecode"},
    {"copy_lua_variable_to_c", 0x00F266C8, "stingray::script_interface::copy_lua_variable_to_c"},
    {"push_c_variable_to_lua", 0x00F267F8, "stingray::script_interface::push_c_variable_to_lua"},
    {"Bundle::open",           0x00F50BC0, "stingray::Bundle::open"},
    {"Lua->update",            0x00F520B8, "Lua->update"},
    {"load_script_data",       0x00F51EF8, "load_script_data"},
    {"bundle_database.data",   0x00F4E298, "bundle_database.data"},
    {"lua_environment_api",    0x00F1F4F8, "lua_environment_api"},
};
const int dt_anchors_s3_count =
    (int)(sizeof(dt_anchors_s3) / sizeof(dt_anchors_s3[0]));

const dt_anchor_def_t dt_error_strings_s5[] = {
    {"attempt_to_call",  0x00E89B86, "attempt to call a %s value"},
    {"bad_argument",     0x00E89C97, "bad argument #%d to '%s'"},
    {"loop_in_gettable", 0x00E89C1C, "loop in gettable"},
    {"invalid_key_next", 0x00E89B70, "invalid key to 'next'"},
};
const int dt_error_strings_s5_count =
    (int)(sizeof(dt_error_strings_s5) / sizeof(dt_error_strings_s5[0]));
