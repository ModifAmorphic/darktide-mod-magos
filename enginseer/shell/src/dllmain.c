/*
 * dllmain.c — Component A C shell.
 *
 * Linked with the Rust `magos-discovery` staticlib (C-ABI) + MinHook into one
 * PE DLL, delivered by CreateRemoteThread. The minimal validation slice did:
 *   - DLL_PROCESS_ATTACH spawns a worker that calls the Rust `magos_discover`
 *     seam on the live Darktide.exe image.
 *   - Installs a MinHook detour on `lua_newstate`; captures `lua_State*`;
 *     calls `lua_gettop(L)` once.
 *
 * Phase-2 engine-context probe (this build): the worker additionally installs
 * detours on four Lua-lifecycle points and reads a fixed list of engine
 * globals via the LuaJIT C API (`lua_getfield` + `lua_type`). This extends the
 * Phase-1 probe (which established the stdlib is present at the
 * `luaL_loadbuffer`/`lua_pcall` points, so the POC's "sandboxed `_G`" was a
 * timing artifact). Phase 2 resolved the two things Phase 1 left open:
 *
 *   1. WHEN do CLASS + Managers appear? They were `nil` through loadbuffer
 *      call #20 — engine globals set later (when `class.lua` runs, ~pcall #10+,
 *      and during engine init). Phase 1's pcall sampling (calls 1, 2) was too
 *      early. Phase 2 checks CLASS + Managers on EVERY pcall (2× lua_getfield,
 *      cheap) and logs the call# at which each first transitions nil→non-nil
 *      (one-shot per global), plus the full 16-globals snapshot at pcall calls
 *      1, 10, 50, 100, and the first pcall where both CLASS+Managers are
 *      non-nil. That both-present call# is the engine-context lifecycle point.
 *
 *   2. Does an injected CHUNK actually see those globals (rule out setfenv)?
 *      Phase 1 measured the globals *table* from C; a chunk's env defaults to
 *      that table, so it should match — but the definitive test is to inject a
 *      chunk and read what *it* sees. Phase 2 injects the read-only chunk
 *      `return print, require, loadstring, io, CLASS, Managers` via
 *      luaL_loadbuffer + lua_pcall at the both-present point (fallback: pcall
 *      #50/#100) and logs the 6 return-value types. Comparing those to the
 *      C-side lua_getfield(LUA_GLOBALSINDEX) snapshot at the same point is the
 *      definitive H2 answer: match ⇒ chunk env = globals table (engine-context
 *      confirmed for injected chunks); differ ⇒ a setfenv/env difference.
 *
 * Phase 2 RESULT: H2 CONFIRMED — the chunk sees `loadstring=nil`, `io=nil`
 * while the C-side `LUA_GLOBALSINDEX` (measured at the `luaL_openlibs`/early-
 * `lua_pcall` points) has `loadstring=function`, `io=table`. So an injected
 * chunk's env is NOT the globals table. (By pcall #10 the globals table ITSELF
 * also lost io/loadstring — the table changed between pcall #1 and #10.)
 *
 * Phase-3 mechanism-cracker (this build) cracks WHY: is the chunk `setfenv`'d
 * to a sandbox table, is `LUA_GLOBALSINDEX` dynamically swapped/rebound per-
 * phase, or something else? Three measurements in one live run:
 *
 *   1. Hook `lua_setfenv` (high-value). On each call (first ~20), BEFORE the
 *      original, inspect the env table at the top (io/loadstring/require/print/
 *      Managers via lua_getfield) + log the object type (a THREAD setfenv =
 *      globals rebind, since `LUA_GLOBALSINDEX` = `tabref(L->env)`). Reveals
 *      what env the engine assigns scripts — and whether it has io/loadstring
 *      (the bundle-script env patch_999 uses).
 *   2. The chunk's actual env (`lua_getfenv`). At the both-present point, after
 *      `luaL_loadbuffer`, call `lua_getfenv(L, chunk_idx)` and inspect the
 *      chunk's env. If it lacks io while `LUA_GLOBALSINDEX` has it → the chunk
 *      is `setfenv`'d to a sandbox.
 *   3. Dynamic-swap check. Measure io/loadstring in `LUA_GLOBALSINDEX`
 *      immediately before AND immediately after the chunk's `lua_pcall`. If
 *      they flip around execution → the globals table is dynamically swapped/
 *      rebound per-phase (our detour-entry read caught a different phase).
 *
 * This is READ-ONLY recon — no engine global is *called*, no mods; the only
 * side effects are the one-shot chunk injection (a read-only chunk that reads
 * 6 globals and returns them) and log output. The log identifies the
 * sandboxing mechanism and points to the fix (setfenv our chunk to the
 * bundle-script env / run during the full-env phase / load via the engine's
 * path).
 *
 * Phase-4 trampoline prototype (this build) — the DEFINITIVE engine-context
 * validation. The probes established:
 *   - LUA_GLOBALSINDEX has the full stdlib (io, loadstring, ...) from
 *     luaL_openlibs through lua_pcall #1.
 *   - The engine REMOVES io + loadstring from globals between pcall #1 and #10.
 *   - A chunk's env IS the globals table (no setfenv sandbox at pcall #1).
 *   => a chunk injected at pcall #1 sees io/loadstring and can use them.
 * (POC/probe-v2 reported io/loadstring=nil because they injected LATE — pcall
 * #50 / retry-on-error firing after io was stripped.)
 *
 * This build proves it: on the FIRST lua_pcall (pcall #1) — one-shot — and
 * BEFORE calling g_orig_pcall (so it runs in the io-present window), inject a
 * trampoline chunk that does what patch_999's trampoline does (plus setting the
 * two root globals first so the Enginseer can root its own loads + Mods.file):
 *
 *   MAGOS_ENGINSEER_PATH = "<ENGINSEER_PATH>"
 *   MAGOS_MOD_PATH = "<MOD_PATH>"
 *   local f, err = io.open("<ENTRY_PATH>", "r")
 *   if not f then return "FAIL io.open: " .. tostring(err) end
 *   local data = f:read("*all"); f:close()
 *   local fn, lerr = loadstring(data)
 *   if not fn then return "FAIL loadstring: " .. tostring(lerr) end
 *   local ok, rerr = pcall(fn)
 *   if not ok then return "FAIL run: " .. tostring(rerr) end
 *   return "OK"
 *
 * PRODUCTION PATH: <ENTRY_PATH> = <MAGOS_ENGINSEER_PATH>\enginseer.lua. The two
 * roots are read from the child env: MAGOS_ENGINSEER_PATH (the Enginseer dir —
 * runtime-controlled, REQUIRED; if unset the trampoline is SKIPPED, same as
 * today's unset behavior) and DARKTIDE_MOD_PATH (the mod dir — user/mod-manager-
 * controlled, OPTIONAL; mods just won't load if unset). The C side joins the
 * Enginseer dir + enginseer.lua into the entry path, bakes all three into the
 * chunk (luaL_loadbuffer + lua_pcall(0,1,0)), reads the returned status string
 * via lua_tolstring, and logs one line:
 *
 *   [trampoline] @ pcall#1: OK                       <- engine-context PROVEN
 *   [trampoline] @ pcall#1: FAIL io.open: <err>      <- io.open issue (path?)
 *   [trampoline] @ pcall#1: FAIL loadstring: <err>   <- loadstring issue
 *   [trampoline] @ pcall#1: FAIL run: <err>          <- staged file errored
 *
 * Game-safe (same discipline as the probe chunks): one-shot (Interlocked
 * guard), synchronous on the engine's Lua thread, g_in_probe set for the
 * duration (the chunk's internal Lua pcall re-enters lua_pcall but is skipped
 * by the guard), stack-clean (gettop saved / settop restored — zero net effect;
 * the engine's pcall args below base are untouched), lua_pcall never longjmps
 * (errors are returned), and the staged file is benign (e.g. `return 42`).
 *
 * NOTE: the prior probe hooks are retained for context (the pcall#1 globals
 * snapshot, etc.); the trampoline is the focus and runs first at pcall#1.
 *
 * Hook points (all use the single captured lua_State*):
 *   - lua_newstate      (1-shot, right after capture)            — VM fresh, baseline
 *   - luaL_openlibs     (1-shot, after the engine call returns)  — stdlib registered
 *   - luaL_loadbuffer   (calls 1..150 name log; n==1 snapshot)   — script loads
 *   - lua_pcall         (every call)                             — POC injection point
 *       · Phase-4 trampoline @ pcall#1 (one-shot, BEFORE orig pcall) — the focus
 *       · per-call CLASS/Managers transition (one-shot per global)
 *       · per-call 7-global recon transition (one-shot per global: class/CLASS/Managers/Main/StateRequireScripts/StateGame/GameStateMachine) + defensive CLASS.X dump
 *       · full-16 snapshot @ calls {1, 10, 50, 100, both-present}
 *       · chunk-injection (v3: getfenv env + before/after globals + returns) @ both-present (fallback #50/#100)
 *   - lua_setfenv       (first 20 calls)                         — Phase-3: env the engine assigns
 *       · log obj_type (thread = globals rebind) + env globals (io/loadstring/require/print/Managers)
 *
 * NOTE on lua_resource::bytecode: the spec lists it as the primary hook point,
 * but it is an engine C++ function with an UNKNOWN signature/return convention
 * — hooking it with a forwarding detour risks stack/return corruption (cannot
 * safely forward >4 args or a struct-by-value return on x64), which is not
 * game-safe for a read-only probe. Discovery still resolves and logs its
 * address (the `stingray::lua_resource::bytecode` string-anchor's containing
 * function that calls `luaL_loadbuffer`). The probe hooks `luaL_loadbuffer`
 * instead: it is the known-signature LuaJIT C-API function the bytecode loader
 * CALLS (discovery finds `luaL_loadbuffer` precisely by tracing from the
 * bytecode anchor), so it fires at the same lifecycle point, takes `lua_State*`
 * as arg 1, and its `name` arg (arg 4) identifies which script is loading.
 *
 * Out of scope: DMF bootstrap, multi-shot injection, mod-manager UI. Logging
 * goes to OutputDebugString + a log file (MAGOS_ENGINSEER_LOG_FILE env, or
 * magos_enginseer.log beside the game exe). The trampoline entry path comes from
 * MAGOS_ENGINSEER_PATH + enginseer.lua; if that env var is unset, the trampoline
 * is SKIPPED (logged) and the build degrades to the Phase-3 recon probes.
 */
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>

#include "magos_discovery.h"
#include "trampoline.h"
#include "MinHook.h"

/* Named event for the launcher<->shell hook-ready handshake. The launcher
 * creates this (session-local; no Global\ prefix) before injecting the DLL
 * and waits on it before resuming the main thread. Must match launcher.c. */
#define MAGOS_HOOK_READY_EVENT "magos_hook_ready"

/* lua_getglobal(L, s) is a macro: lua_getfield(L, LUA_GLOBALSINDEX, s). */
#define LUA_GLOBALSINDEX (-10002)

/* ---- minimal LuaJIT type stubs (only what the shell touches) ---- */
typedef struct lua_State lua_State;
typedef void *(*lua_Alloc)(void *ud, void *ptr, size_t osize, size_t nsize);
typedef lua_State *(*newstate_t)(lua_Alloc f, void *ud);
typedef int  (*gettop_t)(lua_State *L);
typedef void (*settop_t)(lua_State *L, int idx);
typedef int  (*type_t)(lua_State *L, int idx);
typedef void (*getfield_t)(lua_State *L, int idx, const char *k);
typedef void (*getfenv_t)(lua_State *L, int idx);
typedef int  (*setfenv_t)(lua_State *L, int idx);
typedef void (*openlibs_t)(lua_State *L);
typedef int  (*loadbuffer_t)(lua_State *L, const char *buf, size_t size, const char *name);
typedef int  (*pcall_t)(lua_State *L, int nargs, int nresults, int errfunc);
typedef const char *(*tolstring_t)(lua_State *L, int idx, size_t *len);

/* ---- resolved from the discovered RVAs ---- */
static newstate_t   g_orig_newstate = NULL;
static openlibs_t   g_orig_openlibs = NULL;
static loadbuffer_t g_orig_loadbuffer = NULL;
static pcall_t      g_orig_pcall = NULL;
static setfenv_t    g_orig_setfenv = NULL;
static gettop_t     g_lua_gettop = NULL;
static settop_t     g_lua_settop = NULL;
static type_t       g_lua_type = NULL;
static getfield_t   g_lua_getfield = NULL;
static getfenv_t    g_lua_getfenv = NULL;
static loadbuffer_t g_lua_loadbuffer = NULL;  /* direct (post-discovery) — fallback if the loadbuffer hook didn't install */
static tolstring_t  g_lua_tolstring = NULL;   /* read the trampoline's status-string return */

static uint8_t   *g_module_base = NULL;      /* Darktide.exe base */
static FILE      *g_log = NULL;
static lua_State *g_L = NULL;                /* the single captured VM */

/* ---- probe state ----
 * Lua is single-threaded (the engine drives it on its main thread), so plain
 * volatile flags suffice. The re-entrancy guard `g_in_probe` is set inside
 * probe_log_globals so that any (hypothetical) nested hook fire during a
 * globals read skips its own probing but still calls the original — keeps the
 * probe game-safe. Counters use Interlocked as the cheap Win32 idiom. */
static volatile LONG g_loadbuffer_calls = 0;
static volatile LONG g_pcall_calls = 0;
static volatile LONG g_setfenv_calls = 0;
static volatile int  g_openlibs_logged = 0;
static volatile int  g_newstate_logged = 0;
static volatile int  g_in_probe = 0;
/* Phase-2: CLASS/Managers transition tracking + one-shot chunk injection. */
static volatile int  g_class_logged = 0;     /* one-shot: CLASS nil→non-nil logged */
static volatile int  g_managers_logged = 0;  /* one-shot: Managers nil→non-nil logged */
static volatile int  g_both_logged = 0;      /* one-shot: both non-nil at the same call */
static volatile int  g_chunk_done = 0;       /* one-shot: chunk-injection test performed */

/* ---- Production trampoline state ----
 * The staged chunk (built once at worker startup from the two roots) and the
 * one-shot guard that fires it at pcall#1. Lua is single-threaded on the
 * engine's main thread, so these are only touched from that thread; the guard
 * is Interlocked anyway as the cheap Win32 one-shot idiom.
 *
 * Two roots (both read from the child env the launcher publishes):
 *   - ENGINSEER_DIR_ENV (MAGOS_ENGINSEER_PATH): the Enginseer dir — where
 *     enginseer.lua + its modules live. Runtime-controlled. REQUIRED: if unset,
 *     staging logs why and the trampoline is SKIPPED.
 *   - MOD_PATH_ENV (DARKTIDE_MOD_PATH): the mod dir — where DMF + user mods +
 *     mod_load_order live. User/mod-manager-controlled. OPTIONAL: if unset, the
 *     chunk emits an empty MAGOS_MOD_PATH and mods just won't load.
 * The entry path = <ENGINSEER_DIR_ENV> + enginseer.lua (joined + baked below). */
#define ENGINSEER_DIR_ENV    "MAGOS_ENGINSEER_PATH"  /* Enginseer dir (runtime-controlled; required) */
#define MOD_PATH_ENV         "DARKTIDE_MOD_PATH"     /* mod root dir (user/mod-manager-controlled; optional) */
#define ENGINSEER_ENTRY_FILENAME "enginseer.lua"     /* the Enginseer bootstrap entry (patch_999's mod_loader analogue) */
static char            g_trampoline_chunk[4096];   /* NUL-terminated chunk; len 0 => not staged */
static size_t          g_trampoline_chunk_len = 0;
static volatile LONG   g_trampoline_done = 0;      /* one-shot: trampoline fired at pcall#1 */

/* The fixed list of globals the probe checks at each lifecycle point. */
static const char *PROBE_GLOBALS[] = {
    "print", "require", "dofile", "loadfile", "load",
    "loadstring", "io", "pcall", "pairs", "table",
    "string", "math", "CLASS", "Managers", "_G", "_VERSION",
};
#define PROBE_NGLOBALS (sizeof(PROBE_GLOBALS) / sizeof(PROBE_GLOBALS[0]))

/* ---- structured logging ----
 * Every line: "<UTC ts> <LEVEL> <component>: <message>\n" to both
 * OutputDebugStringA and g_log. Levels filter via g_log_level (resolved once
 * at worker startup from MAGOS_ENGINSEER_LOG_LEVEL; default INFO). The filter
 * check happens BEFORE any formatting/clock read, so filtered levels (the bulk
 * of the probe output at default INFO) cost a single compare — important since
 * the probe paths run on the engine's Lua thread. */
enum { MAGOS_LOG_ERROR = 1, MAGOS_LOG_WARN = 2, MAGOS_LOG_INFO = 3,
       MAGOS_LOG_DEBUG = 4, MAGOS_LOG_TRACE = 5 };
static int g_log_level = MAGOS_LOG_INFO;

/* Case-insensitive ASCII equality (level names are ASCII; avoids a CRT
 * _stricmp dependency). */
static int log_name_ieq(const char *a, const char *b) {
    for (;;) {
        char ca = a[0], cb = b[0];
        if (ca >= 'a' && ca <= 'z') ca = (char)(ca - 32);
        if (cb >= 'a' && cb <= 'z') cb = (char)(cb - 32);
        if (ca != cb) return 0;
        if (ca == '\0') return 1;
        a++; b++;
    }
}

/* Resolve g_log_level from MAGOS_ENGINSEER_LOG_LEVEL (case-insensitive name →
 * enum). Unset, overflow, or unknown name ⇒ INFO. */
static int resolve_log_level(void) {
    char buf[16];
    DWORD n = GetEnvironmentVariableA("MAGOS_ENGINSEER_LOG_LEVEL", buf, sizeof(buf));
    if (n == 0 || n >= sizeof(buf)) return MAGOS_LOG_INFO;
    if (log_name_ieq(buf, "error")) return MAGOS_LOG_ERROR;
    if (log_name_ieq(buf, "warn"))  return MAGOS_LOG_WARN;
    if (log_name_ieq(buf, "info"))  return MAGOS_LOG_INFO;
    if (log_name_ieq(buf, "debug")) return MAGOS_LOG_DEBUG;
    if (log_name_ieq(buf, "trace")) return MAGOS_LOG_TRACE;
    return MAGOS_LOG_INFO;
}

static void magos_log(int level, const char *component, const char *fmt, ...) {
    if (level > g_log_level) return;

    /* UTC timestamp YYYY-MM-DDThh:mm:ssZ */
    SYSTEMTIME st;
    GetSystemTime(&st);
    char ts[24];
    snprintf(ts, sizeof(ts), "%04d-%02d-%02dT%02d:%02d:%02dZ",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);

    /* Level name right-padded to 5 chars uppercase (keeps columns aligned). */
    const char *lname;
    switch (level) {
        case MAGOS_LOG_ERROR: lname = "ERROR"; break;
        case MAGOS_LOG_WARN:  lname = "WARN "; break;
        case MAGOS_LOG_INFO:  lname = "INFO "; break;
        case MAGOS_LOG_DEBUG: lname = "DEBUG"; break;
        case MAGOS_LOG_TRACE: lname = "TRACE"; break;
        default:              lname = "?????"; break;
    }

    char msg[1024];
    va_list ap;
    va_start(ap, fmt);
    int mlen = vsnprintf(msg, sizeof(msg), fmt, ap);
    va_end(ap);
    if (mlen < 0) return;

    /* fmt already ends in \n at every call site, so the line is terminated. */
    char line[1280];
    int n = snprintf(line, sizeof(line), "%s %s %s: %s", ts, lname, component, msg);
    if (n < 0) return;

    OutputDebugStringA(line);
    if (g_log) {
        fputs(line, g_log);
        fflush(g_log);
    }
}

static void open_log(void) {
    char path[MAX_PATH];
    static const char logname[] = "magos_enginseer.log";
    DWORD n = GetEnvironmentVariableA("MAGOS_ENGINSEER_LOG_FILE", path, sizeof(path));
    if (n == 0 || n >= sizeof(path)) {
        /* default: beside the game exe. GetModuleFileNameA may fail (return 0)
         * or truncate (return >= sizeof(path)); in either case fall back to a
         * relative log name rather than using a bogus/truncated path. */
        DWORD m = GetModuleFileNameA(NULL, path, sizeof(path));
        char *slash = (m > 0 && m < sizeof(path)) ? strrchr(path, '\\') : NULL;
        if (slash && (size_t)(slash + 1 - path) + sizeof(logname) <= sizeof(path)) {
            strcpy(slash + 1, logname);
        } else {
            /* no separator, unresolvable path, or too long to append: fall
             * back to a relative log name in the current directory. */
            snprintf(path, sizeof(path), "%s", logname);
        }
    }
    /* "w" (not "a"): truncate so each game start gets a fresh log. The worker
     * opens the log once per game process (DllMain -> worker thread), so this
     * recreates the file on every launch — no unbounded growth across runs. */
    g_log = fopen(path, "w");
    magos_log(MAGOS_LOG_INFO, "shell", "log -> %s\n", path);
}

/* Lua type code -> name (LUA_TNIL..LUA_TTHREAD). */
static const char *lua_type_name(int t) {
    switch (t) {
        case 0: return "nil";
        case 1: return "boolean";
        case 2: return "lightuserdata";
        case 3: return "number";
        case 4: return "string";
        case 5: return "table";
        case 6: return "function";
        case 7: return "userdata";
        case 8: return "thread";
        default: return "?";
    }
}

/*
 * Read the fixed globals list on `L` and log presence/type. READ-ONLY: each
 * check pushes one value via lua_getfield, reads its type, then the stack is
 * restored to its entry top via lua_settop — zero net effect, so the engine
 * observes an untouched stack. lua_getfield on _G (a plain table) does a raw
 * hash lookup with no metamethod, so it cannot longjmp. `g_in_probe` is set
 * for the duration to suppress any nested probe.
 */
static void probe_log_globals(lua_State *L, const char *point, int call_num) {
    g_in_probe = 1;
    if (!L || !g_lua_getfield || !g_lua_type || !g_lua_gettop || !g_lua_settop) {
        magos_log(MAGOS_LOG_DEBUG, "probe", "%s call#%d  (skipped: probe C-API not resolved)\n", point, call_num);
        g_in_probe = 0;
        return;
    }
    if (g_L && L != g_L) {
        magos_log(MAGOS_LOG_DEBUG, "probe", "%s call#%d  (note: L=%p differs from captured g_L=%p)\n",
                  point, call_num, (void *)L, (void *)g_L);
    }
    magos_log(MAGOS_LOG_DEBUG, "probe", "%s call#%d\n", point, call_num);
    int base = g_lua_gettop(L);
    char line[512];
    int off = 0;
    int per_line = 0;
    for (int i = 0; i < (int)PROBE_NGLOBALS; i++) {
        g_lua_getfield(L, LUA_GLOBALSINDEX, PROBE_GLOBALS[i]);
        int t = g_lua_type(L, -1);
        const char *sep = (per_line == 0) ? "   " : "  ";
        int n = snprintf(line + off, sizeof(line) - (size_t)off, "%s%s=%s",
                         sep, PROBE_GLOBALS[i], lua_type_name(t));
        if (n < 0 || (size_t)n >= sizeof(line) - (size_t)off) {
            /* line buffer full (defensive — never hits at 5/line): flush + stop */
            if (per_line > 0) magos_log(MAGOS_LOG_DEBUG, "probe", "%s\n", line);
            break;
        }
        off += n;
        if (++per_line >= 5) {
            magos_log(MAGOS_LOG_DEBUG, "probe", "%s\n", line);
            off = 0;
            per_line = 0;
        }
    }
    if (per_line > 0) {
        magos_log(MAGOS_LOG_DEBUG, "probe", "%s\n", line);
    }
    g_lua_settop(L, base);  /* restore stack — read-only, zero net effect */
    g_in_probe = 0;
}

/*
 * Per-pcall CLASS/Managers transition check. On every lua_pcall (until both
 * globals are seen), read CLASS + Managers via lua_getfield (2 cheap raw hash
 * lookups on the globals table — no metamethod, no longjmp) and log the call#
 * at which each first transitions nil→non-nil (one-shot per global). Returns 1
 * iff BOTH are non-nil at this call (i.e. this is the first call where the
 * engine globals have fully materialized), else 0.
 *
 * Stack-clean: saves lua_gettop, restores it (pops both lookups) — zero net
 * effect on the engine stack. READ-ONLY.
 */
static int probe_check_class_managers(lua_State *L, LONG call_num) {
    g_in_probe = 1;
    int base = g_lua_gettop(L);

    g_lua_getfield(L, LUA_GLOBALSINDEX, "CLASS");
    int class_t = g_lua_type(L, -1);
    if (!g_class_logged && class_t != 0 /* nil */) {
        g_class_logged = 1;
        magos_log(MAGOS_LOG_DEBUG, "probe", "CLASS first non-nil at lua_pcall call#%ld (type=%s)\n",
                  call_num, lua_type_name(class_t));
    }

    g_lua_getfield(L, LUA_GLOBALSINDEX, "Managers");
    int managers_t = g_lua_type(L, -1);
    if (!g_managers_logged && managers_t != 0 /* nil */) {
        g_managers_logged = 1;
        magos_log(MAGOS_LOG_DEBUG, "probe", "Managers first non-nil at lua_pcall call#%ld (type=%s)\n",
                  call_num, lua_type_name(managers_t));
    }

    g_lua_settop(L, base);  /* pop both lookups — restore engine stack */
    g_in_probe = 0;
    return (class_t != 0 && managers_t != 0);
}

/*
 * Recon: map the engine init timeline — the lua_pcall call# at which each of
 * 7 engine globals first transitions nil→non-nil. Same read-only, stack-clean
 * discipline as probe_check_class_managers: lua_getfield on LUA_GLOBALSINDEX
 * (a plain table → raw hash lookup, no metamethod, cannot longjmp) + lua_type
 * read, one-shot per global (g_recon_logged[]), stack restored to the saved
 * top. g_in_probe is save/restored (like probe_inspect_env) so this is safe
 * from inside an already-guarded path as well as from a fresh detour entry.
 *
 * The 7 targets de-risk the Enginseer v2 design: when do class/CLASS/Managers/
 * Main/StateRequireScripts/StateGame/GameStateMachine materialize relative to
 * pcall#? (v2 must defer work until those globals exist.) Called on EVERY
 * pcall; the one-shot flags bound the logging.
 *
 * Defensive CLASS.X dump (one-shot, Change 1b): the first pcall# where CLASS is
 * non-nil, additionally read CLASS then lua_getfield on it for each of
 * {Main, StateRequireScripts, StateGame, GameStateMachine} and log their types.
 * In the unmodified game CLASS is loader-provided and will likely NEVER appear,
 * so this probably logs nothing — that is expected; it is defensive coverage
 * for the v2 CLASS monkey-patch timing. Stack-clean (each member lookup is
 * popped before the next; CLASS stays on top until the final settop).
 */
static void probe_log_transitions(lua_State *L, LONG call_num) {
    static const char *RECON_GLOBALS[] = {
        "class", "CLASS", "Managers", "Main",
        "StateRequireScripts", "StateGame", "GameStateMachine",
    };
    static volatile int g_recon_logged[7] = {0};
    static const char *CLASS_MEMBERS[] = {
        "Main", "StateRequireScripts", "StateGame", "GameStateMachine",
    };
    static volatile int g_class_dump_logged = 0;

    int saved = g_in_probe;
    g_in_probe = 1;

    if (!L || !g_lua_getfield || !g_lua_type || !g_lua_gettop || !g_lua_settop) {
        g_in_probe = saved;
        return;
    }

    int base = g_lua_gettop(L);

    for (int i = 0; i < 7; i++) {
        if (g_recon_logged[i]) continue;
        g_lua_getfield(L, LUA_GLOBALSINDEX, RECON_GLOBALS[i]);
        int t = g_lua_type(L, -1);
        if (t != 0 /* non-nil */) {
            g_recon_logged[i] = 1;
            magos_log(MAGOS_LOG_DEBUG, "probe", "%s first non-nil at lua_pcall call#%ld (type=%s)\n",
                      RECON_GLOBALS[i], call_num, lua_type_name(t));

            /* CLASS is currently on top (absolute idx = gettop). Dump its
             * members one-shot, popping each so CLASS stays on top. */
            if (strcmp(RECON_GLOBALS[i], "CLASS") == 0 && !g_class_dump_logged) {
                g_class_dump_logged = 1;
                int class_idx = g_lua_gettop(L);
                char line[256];
                int off = snprintf(line, sizeof(line),
                                   "CLASS dump @ pcall#%ld:", call_num);
                for (int j = 0; j < 4; j++) {
                    g_lua_getfield(L, class_idx, CLASS_MEMBERS[j]);
                    int mt = g_lua_type(L, -1);
                    int w = snprintf(line + off, sizeof(line) - (size_t)off,
                                     "  %s=%s", CLASS_MEMBERS[j], lua_type_name(mt));
                    if (w < 0 || (size_t)w >= sizeof(line) - (size_t)off) break;
                    off += w;
                    g_lua_settop(L, class_idx);  /* pop member; CLASS back on top */
                }
                magos_log(MAGOS_LOG_DEBUG, "probe", "%s\n", line);
            }
        }
        g_lua_settop(L, base);  /* pop this global's lookup — stack-clean per iter */
    }

    g_lua_settop(L, base);  /* belt-and-suspenders: zero net stack effect */
    g_in_probe = saved;
}

/*
 * Inspect a table at absolute stack index `env_idx`: read io/loadstring/
 * require/print/Managers via lua_getfield + lua_type, log the types. READ-ONLY
 * and stack-clean: each getfield pushes one value, we read its type, then
 * lua_settop back to the entry top pops it — the env table stays at env_idx
 * throughout, zero net effect on the engine stack.
 *
 * Save/restore g_in_probe so this is safe to call from inside an already-
 * guarded probe path (e.g. the chunk-injection test) as well as from a fresh
 * detour entry.
 *
 * Residual longjmp risk: lua_getfield follows __index metamethods. The engine's
 * script-env tables are plain tables (or __index=_G, a plain-table fallback →
 * no function call), so in practice this cannot longjmp. If an env had a
 * function __index that errored it could longjmp — same residual category as
 * probe v2's loadbuffer-OOM longjmp, and far less likely. Acceptable for a
 * read-only probe (v1/v2 ran clean).
 */
static void probe_inspect_env(lua_State *L, int env_idx, const char *label, int call_num) {
    static const char *env_globals[] = { "io", "loadstring", "require", "print", "Managers" };
    int saved = g_in_probe;
    g_in_probe = 1;
    int base = g_lua_gettop(L);
    char line[512];
    int off = snprintf(line, sizeof(line), "%s call#%d env:", label, call_num);
    for (int i = 0; i < (int)(sizeof(env_globals) / sizeof(env_globals[0])); i++) {
        g_lua_getfield(L, env_idx, env_globals[i]);
        int t = g_lua_type(L, -1);
        int w = snprintf(line + off, sizeof(line) - (size_t)off, "  %s=%s",
                         env_globals[i], lua_type_name(t));
        if (w < 0 || (size_t)w >= sizeof(line) - (size_t)off) {
            break;
        }
        off += w;
        g_lua_settop(L, base);  /* pop the getfield result; env table stays at env_idx */
    }
    magos_log(MAGOS_LOG_DEBUG, "probe", "%s\n", line);
    g_in_probe = saved;
}

/*
 * Read io + loadstring in LUA_GLOBALSINDEX (= tabref(L->env), the thread's
 * globals table) and log their types. READ-ONLY, stack-clean (save/restore
 * top). Used for the before/after dynamic-swap check around the chunk's pcall:
 * if these flip (e.g. function→nil) across the chunk's execution, the globals
 * table is dynamically swapped/rebound per-phase. LUA_GLOBALSINDEX resolves to
 * the thread globals (a plain table) → lua_getfield does a raw lookup, no
 * metamethod, cannot longjmp.
 */
static void probe_globals_io_loadstring(lua_State *L, const char *label, int call_num) {
    int saved = g_in_probe;
    g_in_probe = 1;
    int base = g_lua_gettop(L);
    g_lua_getfield(L, LUA_GLOBALSINDEX, "io");
    int io_t = g_lua_type(L, -1);
    g_lua_getfield(L, LUA_GLOBALSINDEX, "loadstring");
    int ls_t = g_lua_type(L, -1);
    magos_log(MAGOS_LOG_DEBUG, "probe", "globals %-6s @ pcall#%d:  io=%s  loadstring=%s\n",
              label, call_num, lua_type_name(io_t), lua_type_name(ls_t));
    g_lua_settop(L, base);  /* pop both lookups */
    g_in_probe = saved;
}

/*
 * The mechanism-cracker test (extends probe v2's H2 test). Injects the same
 * read-only chunk `return print, require, loadstring, io, CLASS, Managers`
 * via luaL_loadbuffer + lua_pcall, but now resolves WHY the chunk's env may
 * differ from LUA_GLOBALSINDEX via three measurements:
 *
 *   #2 (chunk's actual env): after luaL_loadbuffer (chunk function on stack),
 *      call lua_getfenv(L, chunk_idx) to push the chunk's env table and
 *      inspect it (io/loadstring/require/print/Managers). Compare to the
 *      C-side LUA_GLOBALSINDEX snapshot. If the chunk's env lacks io while
 *      LUA_GLOBALSINDEX has it → the chunk is setfenv'd to a sandbox table
 *      (NOT the globals). (lua_getfenv on a valid idx is longjmp-safe: it
 *      calls index2adr_check + type checks + settabV/setnilV + incr_top, no
 *      metamethods.)
 *
 *   #3 (dynamic-swap check): measure io/loadstring in LUA_GLOBALSINDEX
 *      immediately BEFORE and immediately AFTER the chunk's lua_pcall. If they
 *      flip around execution → the globals table is dynamically swapped/
 *      rebound per-phase (and our detour-entry lua_getfield caught a different
 *      phase than the chunk runs in).
 *
 *   (chunk return types): the v2 H2 result — what the chunk ACTUALLY sees.
 *      Combined with #2/#3 this localizes the cause: sandbox setfenv vs.
 *      per-phase globals swap vs. timing.
 *
 * SAFETY (game-safe, like probe v2):
 *   - Same thread: the detour runs synchronously on the engine's Lua thread.
 *   - Stack discipline: lua_gettop saved before, lua_settop restored after —
 *     the engine observes an untouched stack (zero net effect). getfenv pushes
 *     one table (popped before pcall); the before/after checks are stack-clean.
 *   - lua_pcall with errfunc=0: a runtime error is RETURNED (pcall never
 *     longjmps), so the chunk cannot escape our control.
 *   - No re-entrancy: trampolines bypass our detours; g_in_probe is set for
 *     the duration (save/restored by the helpers).
 *   - Residual longjmp risk: luaL_loadbuffer can longjmp on OOM during parse
 *     of the 60-byte chunk — negligibly unlikely on a running game (probe v2
 *     used this exact pattern).
 */
static void probe_inject_chunk(lua_State *L, int call_num) {
    g_in_probe = 1;

    static const char chunk[] = "return print, require, loadstring, io, CLASS, Managers";
    static const char *names[6] = { "print", "require", "loadstring", "io", "CLASS", "Managers" };

    magos_log(MAGOS_LOG_DEBUG, "probe", "--- chunk-injection (v3) test @ pcall#%d ---\n", call_num);
    magos_log(MAGOS_LOG_DEBUG, "probe", "chunk: `%s`\n", chunk);

    if (!g_orig_pcall || !g_lua_gettop || !g_lua_settop || !g_lua_type || !g_lua_getfenv) {
        magos_log(MAGOS_LOG_DEBUG, "probe", "chunk-injection SKIPPED: pcall/C-API (need getfenv) not resolved\n");
        g_in_probe = 0;
        return;
    }
    /* Prefer the loadbuffer trampoline (bypasses our detour → no re-entrancy
     * when the hook is installed); fall back to the direct discovered address
     * if the loadbuffer hook didn't install (no patch → no detour to bypass). */
    loadbuffer_t lb = g_orig_loadbuffer ? g_orig_loadbuffer : g_lua_loadbuffer;
    if (!lb) {
        magos_log(MAGOS_LOG_DEBUG, "probe", "chunk-injection SKIPPED: luaL_loadbuffer not resolved\n");
        g_in_probe = 0;
        return;
    }

    int base = g_lua_gettop(L);

    /* Load the chunk (pushes a function on top at base+1). */
    int rc = lb(L, chunk, sizeof(chunk) - 1, "magos_probe_v3");
    if (rc != 0) {
        int t = g_lua_type(L, -1);
        magos_log(MAGOS_LOG_DEBUG, "probe", "chunk LOAD FAILED (rc=%d, err_type=%s) — not injected\n",
                  rc, lua_type_name(t));
        g_lua_settop(L, base);
        g_in_probe = 0;
        return;
    }

    /* Measurement #2: the chunk's ACTUAL env via lua_getfenv. Pushes the chunk
     * function's env table at base+2; inspect it; pop it (back to base+1, the
     * chunk function still on top). */
    g_lua_getfenv(L, base + 1);   /* push chunk's env at base+2 */
    probe_inspect_env(L, base + 2, "chunk env (getfenv)", call_num);
    g_lua_settop(L, base + 1);    /* pop the env — chunk function back on top */
    magos_log(MAGOS_LOG_DEBUG, "probe", "compare 'chunk env (getfenv)' to the C-side globals snapshot:\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "  chunk env lacks io but globals has io → chunk is setfenv'd to a sandbox.\n");

    /* Measurement #3 (before): io/loadstring in LUA_GLOBALSINDEX immediately
     * before the chunk's pcall. */
    probe_globals_io_loadstring(L, "BEFORE", call_num);

    /* Run the chunk: 0 args, 6 results, errfunc=0. pcall RETURNS on error
     * (never longjmps), so this is contained. On success, 6 values at
     * base+1..base+6. */
    int prc = g_orig_pcall(L, 0, 6, 0);
    if (prc != 0) {
        int t = g_lua_type(L, -1);
        magos_log(MAGOS_LOG_DEBUG, "probe", "chunk PCALL FAILED (rc=%d, err_type=%s) — chunk errored\n",
                  prc, lua_type_name(t));
        g_lua_settop(L, base);
        g_in_probe = 0;
        return;
    }

    /* Measurement #3 (after): io/loadstring in LUA_GLOBALSINDEX immediately
     * after the chunk's pcall. If BEFORE != AFTER → globals swapped per-phase. */
    probe_globals_io_loadstring(L, "AFTER", call_num);

    /* The chunk's 6 return-value types (what it ACTUALLY sees). */
    char line[512];
    int off = snprintf(line, sizeof(line), "chunk sees @ pcall#%d:", call_num);
    for (int i = 0; i < 6; i++) {
        int t = g_lua_type(L, base + 1 + i);
        int w = snprintf(line + off, sizeof(line) - (size_t)off, "  %s=%s",
                         names[i], lua_type_name(t));
        if (w < 0 || (size_t)w >= sizeof(line) - (size_t)off) {
            break;
        }
        off += w;
    }
    magos_log(MAGOS_LOG_DEBUG, "probe", "%s\n", line);
    magos_log(MAGOS_LOG_DEBUG, "probe", "v3 summary: chunk env (#2) + before/after globals (#3) + chunk sees (above)\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "  localize the mechanism: sandbox setfenv vs. per-phase globals swap vs. timing.\n");

    g_lua_settop(L, base);  /* pop the 6 returns — restore engine stack */
    g_in_probe = 0;
}

/* ---- Production trampoline ----
 * See the file header's Phase-4 + production notes. The staging step reads the
 * Enginseer dir (required) + the mod dir (optional) from the child env, joins
 * the Enginseer dir + enginseer.lua into the entry path, and builds the chunk
 * once (worker startup); the run step executes it one-shot at pcall#1, BEFORE
 * g_orig_pcall, so it runs in the io/loadstring-present window (the engine
 * strips io/loadstring from globals between pcall #1 and #10). If the Enginseer
 * dir env var is unset, staging logs why and the run step logs SKIPPED at
 * pcall#1 — the build degrades to the recon probes. */

/*
 * Read the two roots, join the Enginseer dir + enginseer.lua into the entry
 * path, and build g_trampoline_chunk. The Enginseer dir (MAGOS_ENGINSEER_PATH)
 * is REQUIRED: on any failure (var unset/too long, join overflow, escape/
 * overflow) the chunk len stays 0 and trampoline_run will log SKIPPED. The mod
 * dir (DARKTIDE_MOD_PATH) is OPTIONAL: unset/too long is logged and treated as
 * unset (mod_path = NULL -> the chunk emits an empty MAGOS_MOD_PATH). Idempotent:
 * called once from the worker.
 */
static void trampoline_stage_chunk(void) {
    /* Enginseer root (required). */
    char enginseer_dir[1024];
    DWORD eg = GetEnvironmentVariableA(ENGINSEER_DIR_ENV, enginseer_dir, sizeof(enginseer_dir));
    if (eg == 0) {
        DWORD e = GetLastError();
        if (e == ERROR_ENVVAR_NOT_FOUND) {
            magos_log(MAGOS_LOG_INFO, "trampoline", "%s not set; trampoline will be SKIPPED at pcall#1\n",
                      ENGINSEER_DIR_ENV);
        } else {
            magos_log(MAGOS_LOG_INFO, "trampoline", "%s read error (lu=%lu); trampoline will be SKIPPED\n",
                      ENGINSEER_DIR_ENV, e);
        }
        return;
    }
    if (eg >= sizeof(enginseer_dir)) {
        magos_log(MAGOS_LOG_INFO, "trampoline", "%s too long (%lu chars, max %zu); trampoline will be SKIPPED\n",
                  ENGINSEER_DIR_ENV, eg, sizeof(enginseer_dir) - 1);
        return;
    }

    /* Mod root (optional). Unset/too-long => NULL (the chunk emits an empty
     * MAGOS_MOD_PATH; mods just won't load, the rite degrades gracefully). */
    char mod_dir[1024];
    const char *mod_path = NULL;
    DWORD mg = GetEnvironmentVariableA(MOD_PATH_ENV, mod_dir, sizeof(mod_dir));
    if (mg == 0) {
        DWORD e = GetLastError();
        if (e != ERROR_ENVVAR_NOT_FOUND) {
            magos_log(MAGOS_LOG_INFO, "trampoline", "%s read error (lu=%lu); treating as unset\n",
                      MOD_PATH_ENV, e);
        }
    } else if (mg >= sizeof(mod_dir)) {
        magos_log(MAGOS_LOG_INFO, "trampoline", "%s too long (%lu chars, max %zu); treating as unset\n",
                  MOD_PATH_ENV, mg, sizeof(mod_dir) - 1);
    } else {
        mod_path = mod_dir;
    }

    /* Join <Enginseer dir> + enginseer.lua into the production entry path
     * (Windows-canonical: exactly one backslash separator, idempotent on a
     * trailing separator). */
    char path[1024];
    int jn = trampoline_join_path(enginseer_dir, ENGINSEER_ENTRY_FILENAME, path, sizeof(path));
    if (jn < 0) {
        magos_log(MAGOS_LOG_INFO, "trampoline", "enginseer-path+entry join failed (overflow); trampoline will be SKIPPED\n");
        return;
    }

    int n = trampoline_build_chunk(enginseer_dir, mod_path, path,
                                   g_trampoline_chunk, sizeof(g_trampoline_chunk));
    if (n < 0) {
        magos_log(MAGOS_LOG_INFO, "trampoline", "chunk build failed (escape/overflow); trampoline will be SKIPPED\n");
        return;
    }
    g_trampoline_chunk_len = (size_t)n;
    magos_log(MAGOS_LOG_INFO, "trampoline", "%s=%s\n", ENGINSEER_DIR_ENV, enginseer_dir);
    if (mod_path) {
        magos_log(MAGOS_LOG_INFO, "trampoline", "%s=%s\n", MOD_PATH_ENV, mod_path);
    } else {
        magos_log(MAGOS_LOG_INFO, "trampoline", "%s unset (mods will not load)\n", MOD_PATH_ENV);
    }
    magos_log(MAGOS_LOG_INFO, "trampoline", "entry path=%s\n", path);
    magos_log(MAGOS_LOG_INFO, "trampoline", "chunk staged (%zu bytes); will run one-shot at pcall#1 (before orig pcall)\n",
              g_trampoline_chunk_len);
}

/*
 * Execute the staged trampoline chunk on the engine's Lua thread at pcall#1.
 * Reads back the chunk's status-string return and logs it as
 *   [trampoline] @ pcall#1: <status>
 * Stack-clean (gettop saved / settop restored) and contained (lua_pcall returns
 * on error, never longjmps). See the file header's Phase-4 safety note.
 */
static void trampoline_run(lua_State *L) {
    g_in_probe = 1;
    magos_log(MAGOS_LOG_INFO, "trampoline", "--- production trampoline @ pcall#1 ---\n");

    if (!g_orig_pcall || !g_lua_gettop || !g_lua_settop || !g_lua_tolstring) {
        magos_log(MAGOS_LOG_INFO, "trampoline", "@ pcall#1: SKIPPED (C-API not resolved)\n");
        g_in_probe = 0;
        return;
    }
    if (g_trampoline_chunk_len == 0) {
        /* staging already logged why (env var unset / too long / build fail). */
        magos_log(MAGOS_LOG_INFO, "trampoline", "@ pcall#1: SKIPPED (chunk not staged — see startup log)\n");
        g_in_probe = 0;
        return;
    }
    /* Prefer the loadbuffer trampoline (bypasses our detour -> no re-entrancy
     * when the loadbuffer hook is installed); fall back to the direct discovered
     * address if the loadbuffer hook didn't install. */
    loadbuffer_t lb = g_orig_loadbuffer ? g_orig_loadbuffer : g_lua_loadbuffer;
    if (!lb) {
        magos_log(MAGOS_LOG_INFO, "trampoline", "@ pcall#1: SKIPPED (luaL_loadbuffer not resolved)\n");
        g_in_probe = 0;
        return;
    }

    int base = g_lua_gettop(L);

    /* Load the chunk (pushes a function at base+1). On parse failure the error
     * message is on top; log it and bail with a clean stack. */
    int rc = lb(L, g_trampoline_chunk, g_trampoline_chunk_len, "magos_trampoline");
    if (rc != 0) {
        const char *e = g_lua_tolstring(L, -1, NULL);
        magos_log(MAGOS_LOG_ERROR, "trampoline", "CHUNK LOAD FAILED (rc=%d): %s\n", rc, e ? e : "<no msg>");
        g_lua_settop(L, base);
        g_in_probe = 0;
        return;
    }

    /* Run the chunk: 0 args, 1 result, no errfunc. pcall RETURNS on error
     * (never longjmps), so the chunk is fully contained. On success the status
     * string is at base+1. The chunk's internal Lua pcall(fn) re-enters our
     * lua_pcall hook, but g_in_probe=1 makes detour_pcall skip its probe block
     * and forward — so it does not perturb the counters or re-run the trampoline. */
    int prc = g_orig_pcall(L, 0, 1, 0);
    if (prc != 0) {
        const char *e = g_lua_tolstring(L, -1, NULL);
        magos_log(MAGOS_LOG_ERROR, "trampoline", "CHUNK PCALL FAILED (rc=%d): %s\n", prc, e ? e : "<no msg>");
        magos_log(MAGOS_LOG_ERROR, "trampoline", "  (the chunk itself errored before returning a status string)\n");
        g_lua_settop(L, base);
        g_in_probe = 0;
        return;
    }

    /* Read + log the status string. The chunk always returns a string ("OK" or
     * "FAIL <step>: <err>"); lua_tolstring returns NULL only if the top value
     * isn't a string/number — defensive. */
    size_t len = 0;
    const char *status = g_lua_tolstring(L, -1, &len);
    magos_log(MAGOS_LOG_INFO, "trampoline", "@ pcall#1: %s\n", status ? status : "<null status>");
    if (status && strncmp(status, "OK", 2) == 0) {
        magos_log(MAGOS_LOG_INFO, "trampoline", "  engine-context PROVEN: io.open + loadstring + staged run succeeded at pcall#1\n");
    }

    g_lua_settop(L, base);  /* pop the status string — restore engine stack */
    g_in_probe = 0;
}

/* ---- lifecycle detours ---- */

/* lua_newstate — capture L (baseline; VM fresh, no stdlib/globals yet). */
static lua_State *detour_newstate(lua_Alloc f, void *ud) {
    lua_State *L = g_orig_newstate(f, ud);
    if (L) {
        magos_log(MAGOS_LOG_INFO, "shell", "step4: lua_newstate hook fired; L = %p\n", (void *)L);
        if (!g_L) g_L = L;
        /* step 5: one LuaJIT C-API call against the documented offsets.
         * lua_gettop(L) = (L->top - L->base) >> 3; for a fresh state top==base
         * so it must return 0, confirming the LJ_64 non-GC64 struct layout. */
        if (g_lua_gettop) {
            int top = g_lua_gettop(L);
            magos_log(MAGOS_LOG_INFO, "shell", "step5: lua_gettop(L) = %d (expect 0 for fresh state)\n", top);
        }
        if (!g_newstate_logged) {
            g_newstate_logged = 1;
            probe_log_globals(L, "lua_newstate", 1);
        }
    } else {
        magos_log(MAGOS_LOG_ERROR, "shell", "step4: lua_newstate returned NULL\n");
    }
    return L;
}

/* luaL_openlibs — after the engine's call returns, stdlib is registered. */
static void detour_openlibs(lua_State *L) {
    g_orig_openlibs(L);
    if (!g_in_probe && !g_openlibs_logged && L) {
        g_openlibs_logged = 1;
        if (!g_L) g_L = L;
        probe_log_globals(L, "luaL_openlibs", 1);
    }
}

/* Per-call loadbuffer name-log cap (recon: map the script-compilation order).
 * Every call with n <= this cap logs its script name + the current pcall# (read
 * from g_pcall_calls for correlation). g_orig_loadbuffer is ALWAYS called. */
#define RECON_LOADBUFFER_LOG_CAP 150

/* luaL_loadbuffer — the bytecode-load proxy (see file header NOTE). Recon: log
 * every script name for the first RECON_LOADBUFFER_LOG_CAP calls (maps the
 * script-compilation order, correlated with the pcall#), plus the full 16-
 * globals snapshot once at n==1 (the per-call name log supersedes the old
 * 5/20 sampling). g_orig_loadbuffer is always called regardless of the cap. */
static int detour_loadbuffer(lua_State *L, const char *buff, size_t size, const char *name) {
    LONG n = 0;
    int do_name_log = 0;
    int do_snapshot = 0;
    if (!g_in_probe) {
        n = InterlockedIncrement(&g_loadbuffer_calls);
        do_name_log = (n <= RECON_LOADBUFFER_LOG_CAP);
        do_snapshot = (n == 1);
    }
    if (do_name_log) {
        if (!g_L) g_L = L;
        char namebuf[128];
        snprintf(namebuf, sizeof(namebuf), "%s", name ? name : "<null>");
        magos_log(MAGOS_LOG_TRACE, "probe", "luaL_loadbuffer call#%ld (pcall#%ld) name=%s size=%zu\n",
                  n, g_pcall_calls, namebuf, size);
    }
    if (do_snapshot) {
        probe_log_globals(L, "luaL_loadbuffer", (int)n);
    }
    return g_orig_loadbuffer(L, buff, size, name);
}

/* lua_pcall — the POC's injection point (POC found executed chunks sandboxed).
 * Phase 2: on every call, track the CLASS/Managers nil→non-nil transition
 * (one-shot per global); take the full 16-globals snapshot at calls 1, 10, 50,
 * 100, and the first call where both CLASS+Managers are non-nil; and at that
 * both-present point (fallback: #50/#100) inject a read-only chunk to test
 * whether an injected chunk sees the same globals the C-side table read sees
 * (H2: rules out a setfenv / env difference for injected chunks).
 * Phase 4: at pcall#1 (one-shot, BEFORE g_orig_pcall) run the trampoline
 * prototype — the definitive io.open+loadstring+run test in the io-present
 * window. See the file header's Phase-4 note. */
static int detour_pcall(lua_State *L, int nargs, int nresults, int errfunc) {
    if (!g_in_probe) {
        LONG n = InterlockedIncrement(&g_pcall_calls);
        if (!g_L) g_L = L;

        /* 0. Phase-4 trampoline prototype — one-shot at pcall#1, BEFORE the
         *    engine's pcall, so it runs while io/loadstring are still present
         *    (they are stripped between pcall #1 and #10). Runs first so its
         *    result is the headline of the pcall#1 log block. */
        if (n == 1 && InterlockedCompareExchange(&g_trampoline_done, 1, 0) == 0) {
            trampoline_run(L);
        }

        /* 1. Per-pcall CLASS/Managers transition (one-shot per global; the
         *    whole check is skipped once both have been seen). */
        int both_now = 0;
        if (!g_both_logged && g_lua_gettop && g_lua_getfield && g_lua_type && g_lua_settop) {
            both_now = probe_check_class_managers(L, n);
            if (both_now) {
                g_both_logged = 1;
                magos_log(MAGOS_LOG_DEBUG, "probe", "=== CLASS+Managers both non-nil at lua_pcall call#%ld ===\n", n);
                magos_log(MAGOS_LOG_DEBUG, "probe", "    engine-context lifecycle point: globals table fully materialized\n");
            }
        }

        /* 1b. Recon: 7-global init-timeline transition log on EVERY pcall
         *     (one-shot flags inside bound the logging). Read-only, stack-clean.
         *     Does not touch probe_check_class_managers / g_both_logged. */
        probe_log_transitions(L, n);

        /* 2. Full 16-globals snapshot at scheduled calls + the both-present call. */
        int scheduled = (n == 1 || n == 10 || n == 50 || n == 100);
        if (scheduled && both_now) {
            magos_log(MAGOS_LOG_DEBUG, "probe", "(pcall#%ld: scheduled snapshot AND both-present point)\n", n);
        } else if (both_now) {
            magos_log(MAGOS_LOG_DEBUG, "probe", "(pcall#%ld: both-present snapshot — first call with CLASS+Managers)\n", n);
        }
        if (scheduled || both_now) {
            probe_log_globals(L, "lua_pcall", (int)n);
        }

        /* 3. Chunk-injection (H2) at the both-present point; fall back to
         *    #50/#100 if the transition was never caught. One-shot. */
        if (!g_chunk_done) {
            int do_inject = both_now
                          || (!g_both_logged && (n == 50 || n == 100));
            if (do_inject) {
                g_chunk_done = 1;
                if (!both_now) {
                    magos_log(MAGOS_LOG_DEBUG, "probe", "chunk-injection FALLBACK @ pcall#%ld (both-present not yet caught)\n", n);
                }
                probe_inject_chunk(L, (int)n);
            }
        }
    }
    return g_orig_pcall(L, nargs, nresults, errfunc);
}

/* lua_setfenv(L, idx) — the HIGH-VALUE Phase-3 hook. lua_setfenv sets the env
 * of the func/udata/thread at `idx` to the table at the top of the stack
 * (L->top-1). For a THREAD, that rebinds the thread's globals (because
 * LUA_GLOBALSINDEX resolves to tabref(L->env) — the thread's globals table).
 * So if the engine setfenvs a thread/coroutine to a sandbox table, that is the
 * sandboxing mechanism.
 *
 * Measurement #1: on each call (sample the first ~20), BEFORE calling the
 * original (the env is at the top then), inspect the env table — check
 * io/loadstring/require/print/Managers via lua_getfield — and log the call#,
 * the type of the object being setfenv'd (func/udata/thread — a thread means a
 * globals rebind), and which globals that env has. This reveals what env the
 * engine assigns to scripts, and whether it has io/loadstring (i.e. is it the
 * bundle-script env patch_999 uses).
 *
 * Known LuaJIT signature `int (lua_State*, int)` → safe to MinHook (unlike the
 * unknown-sig lua_resource::bytecode). READ-ONLY inspection (lua_type +
 * lua_getfield + lua_settop), stack-balanced, before the original pops the env.
 */
static int detour_setfenv(lua_State *L, int idx) {
    if (!g_in_probe) {
        LONG n = InterlockedIncrement(&g_setfenv_calls);
        if (n <= 20 && g_lua_getfield && g_lua_type && g_lua_gettop && g_lua_settop) {
            /* The env table is at the top (L->top-1 == absolute index gettop).
             * Log what's being setfenv'd (type at idx) — a thread (type 8)
             * means a globals rebind — then inspect the env's globals. */
            int env_idx = g_lua_gettop(L);
            int obj_t = g_lua_type(L, idx);
            magos_log(MAGOS_LOG_DEBUG, "probe", "--- lua_setfenv call#%ld  idx=%d  obj_type=%s (%s) ---\n",
                      n, idx, lua_type_name(obj_t),
                      (obj_t == 8) ? "THREAD → globals rebind" : "func/udata/other");
            probe_inspect_env(L, env_idx, "setfenv env", (int)n);
        } else if (n <= 20) {
            magos_log(MAGOS_LOG_DEBUG, "probe", "lua_setfenv call#%ld (env not inspected: C-API not resolved)\n", n);
        }
    }
    return g_orig_setfenv(L, idx);
}

/* Create + enable a MinHook detour. Returns 1 on success, 0 on failure. */
static int install_hook(void *target, void *detour, void **original, const char *name) {
    MH_STATUS mh = MH_CreateHook(target, detour, original);
    if (mh != MH_OK) {
        magos_log(MAGOS_LOG_ERROR, "shell", "MH_CreateHook(%s) failed: %d\n", name, mh);
        return 0;
    }
    mh = MH_EnableHook(target);
    if (mh != MH_OK) {
        magos_log(MAGOS_LOG_ERROR, "shell", "MH_EnableHook(%s) failed: %d\n", name, mh);
        return 0;
    }
    magos_log(MAGOS_LOG_INFO, "shell", "hook installed: %s at %p (detour %p)\n", name, target, detour);
    return 1;
}

/* ---- worker: seam call (step 6) + hook install (step 4) ---- */
static DWORD WINAPI worker(LPVOID arg) {
    (void)arg;
    g_log_level = resolve_log_level();
    open_log();
    magos_log(MAGOS_LOG_INFO, "shell", "=== DllMain worker started (pid=%lu) ===\n", GetCurrentProcessId());

    HMODULE h = GetModuleHandleW(NULL);  /* Darktide.exe */
    if (!h) { magos_log(MAGOS_LOG_ERROR, "shell", "FATAL: GetModuleHandle(NULL) failed\n"); return 1; }
    MODULEINFO mi;
    if (!GetModuleInformation(GetCurrentProcess(), h, &mi, sizeof(mi))) {
        magos_log(MAGOS_LOG_ERROR, "shell", "FATAL: GetModuleInformation failed (lu=%lu)\n", GetLastError());
        return 1;
    }
    g_module_base = (uint8_t *)h;
    magos_log(MAGOS_LOG_INFO, "shell", "module base = %p, SizeOfImage = 0x%lx\n",
         (void *)g_module_base, (unsigned long)mi.SizeOfImage);

    /* step 6: invoke the Rust discovery seam in-process. */
    MagosAddressTable tbl;
    uint8_t detail[256] = {0};
    int rc = magos_discover_detail(g_module_base, mi.SizeOfImage, &tbl,
                                   detail, sizeof(detail));
    if (rc != MAGOS_OK) {
        magos_log(MAGOS_LOG_ERROR, "shell", "FATAL: magos_discover rc=%d (%s)\n", rc, (char*)detail);
        return 1;
    }
    magos_log(MAGOS_LOG_INFO, "discovery", "step6: discovery OK. 16 addresses (RVAs):\n");
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  lua_newstate_thunk  = 0x%08x  body=0x%08x\n",
         tbl.lua_newstate_thunk, tbl.lua_newstate_body);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  lua_atpanic=0x%08x  lua_gettop=0x%08x\n",
         tbl.lua_atpanic, tbl.lua_gettop);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  lua_pcall=0x%08x  luaL_loadbuffer=0x%08x\n",
         tbl.lua_pcall, tbl.lual_loadbuffer);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  lua_pushcclosure=0x%08x  lua_setfield=0x%08x\n",
         tbl.lua_pushcclosure, tbl.lua_setfield);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  lua_pushstring=0x%08x  lua_tolstring=0x%08x\n",
         tbl.lua_pushstring, tbl.lua_tolstring);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  lua_createtable=0x%08x  lua_type=0x%08x\n",
         tbl.lua_createtable, tbl.lua_type);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  lua_tonumber=0x%08x  lua_settop=0x%08x\n",
         tbl.lua_tonumber, tbl.lua_settop);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  luaL_openlibs=0x%08x  lua_panic_body=0x%08x\n",
         tbl.lual_openlibs, tbl.lua_panic_body);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  LuaEnvironment::init = 0x%08x..0x%08x\n",
         tbl.luaenvironment_init_begin, tbl.luaenvironment_init_end);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  probe lua_getfield=0x%08x  lua_resource::bytecode=0x%08x\n",
         tbl.lua_getfield, tbl.lua_resource_bytecode);
    magos_log(MAGOS_LOG_DEBUG, "discovery", "  probe lua_getfenv=0x%08x  lua_setfenv=0x%08x\n",
         tbl.lua_getfenv, tbl.lua_setfenv);

    /* resolve the LuaJIT C-API pointers used by the probe + the slice. */
    g_lua_gettop   = (gettop_t)(g_module_base + tbl.lua_gettop);
    g_lua_settop   = (settop_t)(g_module_base + tbl.lua_settop);
    g_lua_type     = (type_t)(g_module_base + tbl.lua_type);
    g_lua_getfield = (getfield_t)(g_module_base + tbl.lua_getfield);
    g_lua_getfenv  = (getfenv_t)(g_module_base + tbl.lua_getfenv);
    g_lua_loadbuffer = (loadbuffer_t)(g_module_base + tbl.lual_loadbuffer);
    g_lua_tolstring = (tolstring_t)(g_module_base + tbl.lua_tolstring);

    /* step 4: install the lifecycle hooks. lua_newstate is critical (can't
     * capture L without it); the other three are best-effort — a failure
     * degrades the probe to partial data rather than aborting. */
    MH_STATUS mh = MH_Initialize();
    if (mh != MH_OK) { magos_log(MAGOS_LOG_ERROR, "shell", "MH_Initialize failed: %d\n", mh); return 1; }
    if (!install_hook((void *)(g_module_base + tbl.lua_newstate_thunk),
                      (void *)&detour_newstate, (void **)&g_orig_newstate, "lua_newstate")) {
        return 1;
    }
    install_hook((void *)(g_module_base + tbl.lual_openlibs),
                 (void *)&detour_openlibs, (void **)&g_orig_openlibs, "luaL_openlibs");
    install_hook((void *)(g_module_base + tbl.lual_loadbuffer),
                 (void *)&detour_loadbuffer, (void **)&g_orig_loadbuffer, "luaL_loadbuffer");
    install_hook((void *)(g_module_base + tbl.lua_pcall),
                 (void *)&detour_pcall, (void **)&g_orig_pcall, "lua_pcall");
    install_hook((void *)(g_module_base + tbl.lua_setfenv),
                 (void *)&detour_setfenv, (void **)&g_orig_setfenv, "lua_setfenv");

    magos_log(MAGOS_LOG_DEBUG, "probe", "=== engine-context probe (Phase 3 — mechanism-cracker) ===\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "hooks: lua_newstate{1} luaL_openlibs{1} luaL_loadbuffer{1,5,20}\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "       lua_pcall{every} lua_setfenv{first 20}\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "pcall: per-call CLASS+Managers transition (one-shot per global);\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "       full-16 snapshot @ {1,10,50,100,both-present}; chunk-injection (v3) @ both-present (fallback #50/#100)\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "setfenv: sample first 20 — log obj_type + env globals (io/loadstring/require/print/Managers)\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "chunk (v3): #2 getfenv env, #3 before/after globals, + chunk return types\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "globals: print require dofile loadfile load loadstring io pcall pairs "
              "table string math CLASS Managers _G _VERSION\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "NOTE: lua_resource::bytecode @0x%08x discovered (anchor) but NOT hooked "
              "(unknown C++ sig) — luaL_loadbuffer (its known-sig callee) is the proxy.\n",
              tbl.lua_resource_bytecode);

    /* Production trampoline: stage the chunk from the two roots
     * (MAGOS_ENGINSEER_PATH + DARKTIDE_MOD_PATH) now so it is ready to fire
     * one-shot at pcall#1 (the hooks above are armed and the engine's first
     * lua_pcall is imminent once the main thread resumes). */
    magos_log(MAGOS_LOG_DEBUG, "probe", "=== production trampoline ===\n");
    magos_log(MAGOS_LOG_DEBUG, "probe", "fires one-shot at pcall#1 (before orig pcall); reads %s + %s + %s\n",
              ENGINSEER_DIR_ENV, MOD_PATH_ENV, ENGINSEER_ENTRY_FILENAME);
    trampoline_stage_chunk();

    /* Production hook-ready handshake: signal the launcher that the hooks are
     * enabled so it can resume the main thread. The launcher creates the
     * named event before injection and waits on it before ResumeThread;
     * resuming earlier loses the lua_newstate hook (the engine calls
     * lua_newstate during startup). If the event can't be opened (e.g.
     * launcher didn't create it), log and continue — the hooks are still armed. */
    HANDLE ready = OpenEventA(EVENT_MODIFY_STATE, FALSE, MAGOS_HOOK_READY_EVENT);
    if (ready) {
        SetEvent(ready);
        CloseHandle(ready);
        magos_log(MAGOS_LOG_INFO, "shell", "hook-ready signaled (%s)\n", MAGOS_HOOK_READY_EVENT);
    } else {
        magos_log(MAGOS_LOG_WARN, "shell", "OpenEvent(%s) failed (lu=%lu); hooks armed, not signaled\n",
             MAGOS_HOOK_READY_EVENT, GetLastError());
    }

    magos_log(MAGOS_LOG_INFO, "shell", "worker complete; waiting for the engine lifecycle...\n");
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved) {
    (void)hinst; (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hinst);
        /* Run on a worker so DllMain returns promptly (the loader holds the
         * loader lock; discovery + hook install must not block on it). */
        HANDLE h = CreateThread(NULL, 0, worker, NULL, 0, NULL);
        if (h) CloseHandle(h);
    }
    return TRUE;
}
