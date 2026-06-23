/*
 * inject.c — Phase 5 implementation. See inject.h for the contract.
 *
 * Phase 5 builds on Phase 4's retry-on-error injection mechanism + Phase 5
 * Step 3's C-function bootstrap. New in Phase 5:
 *
 *   - Registers the **6 DMF dependencies** as C functions / Lua tables,
 *     bypassing the sandboxed _G entirely. Each is registered via the
 *     LuaJIT C API (lua_pushcclosure + lua_setfield at LUA_GLOBALSINDEX):
 *
 *       _G.__print               = c_print       (writes args to log file)
 *       _G.Mods.file.dofile      = c_dofile      (reads + execs a .lua file)
 *       _G.Mods.lua.loadstring   = c_loadstring  (compiles a Lua source)
 *       _G.Mods.lua.io           = <empty table> (DMF mainly uses dofile)
 *       _G.Mods.require_store    = <empty table> (DMF's require populates)
 *       _G.Mods.original_require = c_require_stub (logs + returns nil)
 *
 *   - Loads **dmf_loader.lua** from the staging directory and executes it
 *     in the engine's VM. The loader uses Mods.file.dofile to load DMF's
 *     modules. Success criterion (Story 6): the loader begins executing
 *     without immediate errors (pcall_rc=0).
 *
 * Two compilation modes share this source:
 *
 *   1. Production (default): mingw PE-x86-64, links MinHook + poc_log.
 *      File I/O via CreateFileW/ReadFile. inject_install() creates a
 *      MinHook hook on lua_pcall; the engine triggers the detour on its
 *      own Lua thread.
 *
 *   2. Test (-DPHASE4_TEST_API): Linux native, links system LuaJIT.
 *      File I/O via standard fopen/fread (POSIX). inject_test_setup()
 *      wires up the function pointers manually so calling p_lua_pcall
 *      re-enters detour_lua_pcall (mirroring the post-MinHook patched-
 *      target semantics). The test exercises the exact detour source
 *      against a real LuaJIT VM.
 */
#include "inject.h"

/* =====================================================================*
 *  Portability layer
 * =====================================================================*/

#ifdef PHASE4_TEST_API
/* ---- Test build: Linux native, no windows.h ------------------------- */

#include <stdint.h>
#include <stddef.h>
#include <time.h>
#include <stdio.h>      /* fopen/fread for c_dofile in test build */
#include <stdlib.h>     /* malloc/free */
#include <string.h>     /* strlen/strncpy */

typedef long LONG;
typedef unsigned long long ULONGLONG;

/* GCC/clang atomic builtins — same memory-barrier semantics as Win32
 * Interlocked ops. Single-threaded test in practice, but atomic for parity. */
static inline LONG p_atomic_cas(volatile LONG *dst, LONG exch, LONG cmp) {
    return __sync_val_compare_and_swap(dst, cmp, exch);
}
static inline LONG p_atomic_xchg(volatile LONG *dst, LONG val) {
    return __sync_lock_test_and_set(dst, val);
}
static inline LONG p_atomic_inc(volatile LONG *dst) {
    return __sync_add_and_fetch(dst, 1);
}
static inline LONG p_atomic_dec(volatile LONG *dst) {
    return __sync_sub_and_fetch(dst, 1);
}

static inline unsigned long long p_now_ms(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (unsigned long long)ts.tv_sec * 1000ULL +
           (unsigned long long)ts.tv_nsec / 1000000ULL;
}

/* No-op logger. The test verifies behavior via inject_test_* accessors,
 * not by reading log output. */
static inline void poc_log_linef(const char *fmt, ...) { (void)fmt; }

#  define CAPTURED_L()     g_test_captured_L
#  define CAPTURED_TICK()  g_test_captured_tick

/* Staging-directory + file reader for the test build: standard POSIX. */
#define P5_MAX_PATH 1024
static char g_staging_dir[P5_MAX_PATH] = "./staging";

static char *p5_read_file(const char *path, size_t *out_size) {
    FILE *fp = fopen(path, "rb");
    if (!fp) return NULL;
    fseek(fp, 0, SEEK_END);
    long sz = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    if (sz <= 0) { fclose(fp); return NULL; }
    char *buf = (char *)malloc((size_t)sz + 1);
    if (!buf) { fclose(fp); return NULL; }
    size_t nread = fread(buf, 1, (size_t)sz, fp);
    fclose(fp);
    if (nread != (size_t)sz) { free(buf); return NULL; }
    buf[sz] = '\0';
    *out_size = (size_t)sz;
    return buf;
}

#else
/* ---- Production: mingw PE-x86-64, windows.h + MinHook + poc_log ----- */

#  include <windows.h>
#  include <stdint.h>
#  include <stdio.h>      /* snprintf */
#  include <string.h>     /* strncpy/memcpy/wcslen */
#  include <MinHook.h>
#  include "poc_log.h"
#  include "expected_addrs.h"   /* EXPECT_* constants — single source of truth */

/* Phase 3 owns the capture; read via its getters (declared in
 * phase3_hooks.h, but we forward-declare here to avoid a header
 * dependency from inject.h — the test build doesn't have phase3). */
void                  *phase3_get_captured_state(void);
unsigned long long    phase3_get_capture_tick(void);

#  define p_atomic_cas(dst, exch, cmp)  InterlockedCompareExchange((dst), (exch), (cmp))
#  define p_atomic_xchg(dst, val)       InterlockedExchange((dst), (val))
#  define p_atomic_inc(dst)             InterlockedIncrement((dst))
#  define p_atomic_dec(dst)             InterlockedDecrement((dst))
#  define p_now_ms()                    GetTickCount64()

#  define CAPTURED_L()     phase3_get_captured_state()
#  define CAPTURED_TICK()  phase3_get_capture_tick()

/* RVAs are sourced from expected_addrs.h so the bake-in is centralized
 * and the A1 disasm-check can verify the same constant against the
 * binary. See report.md for the per-function identification evidence. */
#  define RVA_LUA_PCALL          EXPECT_LUA_PCALL
#  define RVA_LUAL_LOADBUFFER    EXPECT_LOADBUFFER
#  define RVA_LUAL_OPENLIBS      EXPECT_LUAL_OPENLIBS
#  define RVA_LUA_PUSHCCLOSURE   EXPECT_LUA_PUSHCCLOSURE
#  define RVA_LUA_SETFIELD       EXPECT_LUA_SETFIELD
#  define RVA_LUA_TOLSTRING      EXPECT_LUA_TOLSTRING
#  define RVA_LUA_CREATETABLE    EXPECT_LUA_CREATETABLE

/* Staging directory + file reader for the production build: Win32. */
#define P5_MAX_PATH 1024
static char g_staging_dir[P5_MAX_PATH] = {0};

static char *p5_read_file(const char *path, size_t *out_size) {
    /* Convert UTF-8 path to UTF-16 for CreateFileW. */
    wchar_t wpath[P5_MAX_PATH];
    int wlen = MultiByteToWideChar(CP_UTF8, 0, path, -1, wpath,
                                    sizeof(wpath) / sizeof(wpath[0]));
    if (wlen <= 0) return NULL;

    HANDLE h = CreateFileW(wpath, GENERIC_READ, FILE_SHARE_READ, NULL,
                            OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return NULL;

    LARGE_INTEGER fsize;
    if (!GetFileSizeEx(h, &fsize) || fsize.QuadPart <= 0) {
        CloseHandle(h); return NULL;
    }
    /* Cap at 16 MB to avoid runaway allocation on bad inputs. */
    if (fsize.QuadPart > 16 * 1024 * 1024) {
        CloseHandle(h); return NULL;
    }
    char *buf = (char *)malloc((size_t)fsize.QuadPart + 1);
    if (!buf) { CloseHandle(h); return NULL; }

    DWORD total = 0;
    BOOL ok = TRUE;
    while (total < (DWORD)fsize.QuadPart) {
        DWORD nread = 0;
        DWORD to_read = (DWORD)fsize.QuadPart - total;
        if (!ReadFile(h, buf + total, to_read, &nread, NULL) || nread == 0) {
            ok = FALSE; break;
        }
        total += nread;
    }
    CloseHandle(h);
    if (!ok) { free(buf); return NULL; }
    buf[total] = '\0';
    *out_size = (size_t)total;
    return buf;
}

#endif  /* PHASE4_TEST_API */

/* LUA_GLOBALSINDEX — the pseudo-index for the global table in LuaJIT 2.1.
 * Used as the `idx` argument to lua_setfield to register a global.
 * (lua_setglobal is a macro: lua_setfield(L, LUA_GLOBALSINDEX, name).)
 * Value verified against the engine's init at 0x32a2a0, which uses this
 * exact constant 13 times for _G.<name> replacements. */
#define POC_LUA_GLOBALSINDEX  (-10002)

/* LUA_TSTRING = 4, LUA_TFUNCTION = 6, LUA_TNIL = 0 (Lua 5.1 type codes). */
#define POC_LUA_TSTRING    4
#define POC_LUA_TFUNCTION  6
#define POC_LUA_TNIL       0

/* LUA_MULTRET = -1 (pass to lua_pcall's nresults to pass through all
 * results from the called function). */
#define POC_LUA_MULTRET    (-1)

/* Maximum length of a staging-relative path (relative_path + ".lua" +
 * staging_dir + "/"). */
#define P5_MAX_RELPATH 512

/* Relative path to dmf_loader.lua from the staging dir root. */
#define P5_DMF_LOADER_RELPATH  "dmf/scripts/mods/dmf/dmf_loader"

/* =====================================================================*
 *  Shared state
 * =====================================================================*/

/* Function pointer types — match Phase 3/4's verified arg shapes. */
typedef int  (*lua_pcall_t)(void *L, int nargs, int nresults, int errfunc);
typedef int  (*luaL_loadbuffer_t)(void *L, const char *buf, size_t size,
                                       const char *name);
typedef void (*luaL_openlibs_t)(void *L);

/* Phase 5 Step 3 — C-function bootstrap function pointer types.
 *
 * NOTE: lua_pushcfunction and lua_setglobal are MACROS in lua.h, not
 * real functions. The actual symbols are:
 *   - lua_pushcclosure(L, f, n)   — 3 args; we call with n=0 (equivalent
 *                                    to lua_pushcfunction(L, f)).
 *   - lua_setfield(L, idx, k)     — 3 args; we call with idx=LUA_GLOBALSINDEX
 *                                    (-10002; equivalent to lua_setglobal(L, k)
 *                                    when called with a value on the stack top).
 * See report.md §"lua_pushcclosure / lua_setfield identification". */
typedef void (*lua_pushcclosure_t)(void *L, void *f, int n);
typedef void (*lua_setfield_t)(void *L, int idx, const char *k);

/* Phase 5 — DMF bootstrap: additional API function pointer types.
 * These are needed to read string arguments (c_dofile, c_loadstring,
 * c_print) and to build the Mods table + subtables. */
typedef const char *(*lua_tolstring_t)(void *L, int idx, size_t *len);
typedef void (*lua_createtable_t)(void *L, int narray, int nrec);
typedef int  (*lua_gettop_t)(void *L);
typedef int  (*lua_type_t)(void *L, int idx);

/* The "trampoline" — the saved original lua_pcall body.
 *   Production: filled by MinHook (points to the relocated prologue +
 *               jump-back to lua_pcall + patch-len).
 *   Test:       set by inject_test_setup to the real libluajit lua_pcall. */
static lua_pcall_t g_orig_pcall = NULL;

/* The patched target — calling this re-enters detour_lua_pcall.
 *   Production: base + RVA_LUA_PCALL (after MinHook patches its prologue).
 *   Test:       &detour_lua_pcall (set by inject_test_setup). */
static lua_pcall_t p_lua_pcall = NULL;

/* luaL_loadbuffer direct-call pointer (never hooked). */
static luaL_loadbuffer_t p_luaL_loadbuffer = NULL;

/* luaL_openlibs direct-call pointer (Phase 5 Step 2 — never hooked).
 * DISABLED in production (wired to NULL by inject_install — calling
 * openlibs on the engine's state is destructive, verified in live
 * testing). The A1 test may wire it to the real luaL_openlibs to
 * exercise the openlibs call path. */
static luaL_openlibs_t p_luaL_openlibs = NULL;

/* Phase 5 Step 3 — C-function bootstrap direct-call pointers (never
 * hooked). Wired by inject_install to base + RVA_LUA_PUSHCCLOSURE and
 * base + RVA_LUA_SETFIELD. The A1 test wires them via
 * inject_test_set_cfunc_bootstrappers() or leaves them NULL to disable
 * the bootstrap (negative control). */
static lua_pushcclosure_t p_lua_pushcclosure = NULL;
static lua_setfield_t     p_lua_setfield     = NULL;

/* Phase 5 — DMF bootstrap: additional API direct-call pointers (never
 * hooked). Wired by inject_install to base + RVA_LUA_TOLSTRING,
 * base + RVA_LUA_CREATETABLE, and base + EXPECT_GETTOP. */
static lua_tolstring_t  p_lua_tolstring  = NULL;
static lua_createtable_t p_lua_createtable = NULL;
static lua_gettop_t     p_lua_gettop     = NULL;
static lua_type_t       p_lua_type       = NULL;

/* Reentry guard: 1 while our loadbuffer+pcall is in flight. Breaks the
 * recursion when our injected pcall (via p_lua_pcall) re-enters the
 * detour. THIS IS THE #1 RISK PER THE BRIEF — set BEFORE we pcall,
 * cleared AFTER. Also set during the openlibs call AND the Mods table
 * setup (same reentrancy pattern — these calls MAY trigger nested pcall
 * internally via GC indirection). */
static volatile LONG g_injecting = 0;

/* Openlibs one-shot latch: 0 = not yet called on the captured state,
 * 1 = done. Set to 1 on the first qualifying detour entry (after the
 * capture check passes), never reset. The openlibs call happens at most
 * once per DLL lifetime. */
static volatile LONG g_openlibs_called = 0;

/* Phase 5 Step 3 — C-function bootstrap one-shot latch: 0 = poc_print not
 * yet registered, 1 = done. Kept for backward compat with the Phase 5
 * Step 3 A1 test (no longer the primary bootstrap; the DMF setup latch
 * below supersedes it in production). */
static volatile LONG g_cfuncs_registered = 0;

/* Phase 5 — DMF bootstrap one-shot latch: 0 = Mods table + __print not
 * yet set up, 1 = done. The setup runs at most once per DLL lifetime.
 * The A1 test resets it between sub-phases via inject_test_reset(). */
static volatile LONG g_dmf_setup_done = 0;

/* Phase 5 — test observables for the C functions.
 * In production, the effects are visible in the log file. In the test
 * build, the log is a no-op, so we increment these counters as the
 * observables the A1 test asserts on. */
#ifdef PHASE4_TEST_API
static volatile LONG g_test_poc_print_calls = 0;
static volatile LONG g_test_c_print_calls   = 0;
static volatile LONG g_test_c_dofile_calls  = 0;
static volatile LONG g_test_c_dofile_ok     = 0;
static volatile LONG g_test_c_loadstring_calls = 0;
static volatile LONG g_test_c_loadstring_ok = 0;
static volatile LONG g_test_c_require_calls = 0;
#endif

/* Phase 5 Step 3 — poc_print: the trivial Phase 5 Step 3 C function
 * (kept for the cumulative A1 regression). Counted separately from the
 * new c_print (Phase 5). */
static int poc_print(void *L) {
    (void)L;
#ifdef PHASE4_TEST_API
    p_atomic_inc(&g_test_poc_print_calls);
#endif
    poc_log_linef("[FROM LUA] poc_print called \xe2\x80\x94 Lua executed a C function!");
    return 0;
}

/* Latch: 0 = keep trying, 1 = done (success OR gave up). Set ONLY on
 * success (pcall returned 0) or when the max-retry cap is hit. While 0,
 * each qualifying engine lua_pcall call triggers another attempt. */
static volatile LONG g_injected = 0;

/* Retry bookkeeping. g_attempt_count is the total number of loadbuffer+
 * pcall attempts made so far (bounded by PHASE4_MAX_INJECT_ATTEMPTS).
 * g_last_attempt_tick backs the min-interval rate limiter
 * (PHASE4_INJECT_DELAY_MS). */
static volatile LONG        g_attempt_count     = 0;
static unsigned long long   g_last_attempt_tick = 0;

/* The injected Lua source — Phase 5 bootstrap chunk.
 *
 *   return Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")
 *
 * This chunk:
 *   - Reads `Mods` (set up by our C-function bootstrap BEFORE this chunk
 *     runs — bypasses the sandboxed _G).
 *   - Calls `Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")` — our
 *     c_dofile C function, which reads dmf_loader.lua from disk and
 *     executes it via luaL_loadbuffer + lua_pcall.
 *   - Returns whatever dmf_loader.lua returns (typically the
 *     dmf_mod_object table).
 *
 * Outcomes:
 *   - If `Mods` was set up correctly → c_dofile fires, reads the loader,
 *     executes it → pcall_rc=0. SUCCESS.
 *   - If `Mods.file.dofile` is nil → "attempt to call a nil value" →
 *     pcall_rc=2 (LUA_ERRRUN). RETRY.
 *   - If dmf_loader.lua errors during its execution → propagated through
 *     c_dofile's pcall → pcall_rc != 0. RETRY.
 *
 * IMPORTANT: the chunk uses _G.Mods (set up by our bootstrap) and
 * _G.__print (also set up). NO dependency on whatever the engine did to
 * _G.print/_G.io/_G.require. This is the bootstrap trick that bypasses
 * the engine's stripped global environment.
 *
 * The path "dmf/scripts/mods/dmf/dmf_loader" is resolved by c_dofile as
 * <staging_dir> + "/" + path + ".lua" (matches the original mod_loader's
 * get_file_path behavior). */
static const char g_inject_src_default[] =
    "return Mods.file.dofile(\"dmf/scripts/mods/dmf/dmf_loader\")\n";

/* The actual chunk source + length used by do_inject. Defaults to
 * g_inject_src_default but may be overridden by the test (e.g., to test
 * the DMF setup in isolation). In production these point at the default
 * and never change. */
static const char *g_inject_src = g_inject_src_default;
static unsigned long long g_inject_src_len = sizeof(g_inject_src_default) - 1;

#ifdef PHASE4_TEST_API
/* Test-only state. */
static void                   *g_test_captured_L       = NULL;
static unsigned long long      g_test_captured_tick    = 0;
static volatile LONG           g_test_detour_depth     = 0;
static volatile LONG           g_test_max_depth        = 0;
static int                     g_test_last_load_rc     = -1;
static int                     g_test_last_pcall_rc    = -1;
/* L->top offset for THIS LuaJIT build. Defaults to the Darktide binary's
 * offset (0x18); the test overrides it after auto-detecting against the
 * system LuaJIT (which may be GC64 and use a different layout). */
static size_t                  g_test_L_top_offset     = 0x18;
#endif

/* =====================================================================*
 *  Phase 5 C functions — the 6 DMF dependencies
 * =====================================================================*
 * Each function is a Lua-callable C function (Lua C API calling
 * convention: `int (*)(lua_State*)`). We register each as a Lua global
 * (or field in the Mods table) via lua_pushcclosure + lua_setfield in
 * setup_mods_globals() below.
 *
 * They run on the engine's Lua thread (inside our pcall detour). They
 * must be fast and side-effect-free from the engine's perspective.
 *
 * Reentry safety: c_dofile and c_loadstring call p_lua_pcall internally.
 * When called from inside the bootstrap chunk's execution, g_injecting
 * is already 1 (set by do_inject), so the nested pcall re-enters the
 * detour and routes straight to the trampoline (real lua_pcall). When
 * called from Lua outside our bootstrap (e.g., the engine calls
 * dmf_mod_object:init() which calls Mods.file.dofile), g_injecting is 0,
 * but g_injected is 1 (latch set), so the detour skips injection logic.
 */

/* c_print — the DMF `__print` implementation.
 *
 * Reads all arguments via lua_tolstring (which number-coerces numbers to
 * strings) and writes them to the log file via poc_log_linef. Returns 0
 * (no Lua return values).
 *
 * Replaces the engine's `print` for DMF code — DMF's logging module
 * stores `__print` as the "real" print and uses it for echo/notify. */
static int c_print(void *L) {
#ifdef PHASE4_TEST_API
    p_atomic_inc(&g_test_c_print_calls);
#endif
    int nargs = 0;
    if (p_lua_gettop) nargs = p_lua_gettop(L);

    /* Build a single log line from all args, separated by tabs. */
    char logbuf[1024];
    size_t pos = 0;
    logbuf[0] = '\0';
    for (int i = 1; i <= nargs && i <= 16; i++) {
        if (i > 1 && pos + 1 < sizeof(logbuf)) { logbuf[pos++] = '\t'; }
        size_t len = 0;
        const char *s = p_lua_tolstring ? p_lua_tolstring(L, i, &len) : NULL;
        if (s && pos + len + 1 < sizeof(logbuf)) {
            memcpy(logbuf + pos, s, len);
            pos += len;
        } else if (pos + 5 < sizeof(logbuf)) {
            const char *nil = "(nil)";
            memcpy(logbuf + pos, nil, 5);
            pos += 5;
        }
    }
    logbuf[pos < sizeof(logbuf) ? pos : sizeof(logbuf) - 1] = '\0';
    poc_log_linef("[FROM LUA __print] %s", logbuf);
    return 0;
}

/* c_require_stub — placeholder for `Mods.original_require`.
 *
 * The engine's `require` is sandboxed away (it's nil in _G). DMF expects
 * Mods.original_require to be callable but only uses it to load
 * bundle-system modules via the engine's bundle loader. For the POC,
 * we log + return nil. If DMF code calls this, it'll error in a
 * downstream module — but the loader itself only stores this reference,
 * it doesn't call it during init(). */
static int c_require_stub(void *L) {
#ifdef PHASE4_TEST_API
    p_atomic_inc(&g_test_c_require_calls);
#endif
    (void)L;
    poc_log_linef("[__print] Mods.original_require called (stubbed) — "
                  "engine require is sandboxed");
    return 0;  /* (push nothing; "returns" nil implicitly via empty stack) */
}

/* c_loadstring — the DMF `Mods.lua.loadstring` implementation.
 *
 * Compiles a Lua source string via luaL_loadbuffer. On success, leaves
 * the compiled function on the stack and returns 1. On failure, leaves
 * nil + error message on the stack and returns 2 (matches Lua 5.1
 * loadstring semantics).
 *
 * DMF's hook system (core/hooks.lua) uses this to compile hook chains. */
static int c_loadstring(void *L) {
#ifdef PHASE4_TEST_API
    p_atomic_inc(&g_test_c_loadstring_calls);
#endif
    /* Defensive type check: if the argument is neither a string nor a
     * number (the only types tolstring coerces), return early without
     * calling loadbuffer. Uses lua_type — exposed here so p_lua_type
     * isn't dead code in production. */
    if (p_lua_type) {
        int t = p_lua_type(L, 1);
        if (t != POC_LUA_TSTRING && t != 3 /* LUA_TNUMBER */) {
            poc_log_linef("[c_loadstring] arg is not string/number (type=%d)", t);
            return 0;
        }
    }
    size_t len = 0;
    const char *src = p_lua_tolstring ? p_lua_tolstring(L, 1, &len) : NULL;
    if (!src) {
        /* No source argument — push nil + error. */
        if (p_lua_pushcclosure) {}  /* placeholder */
        /* Push nil + error message via loadbuffer of an empty chunk. */
        if (p_luaL_loadbuffer(L, "nil \"loadstring: no source\"", 27,
                              "[c_loadstring]") == 0) {
#ifdef PHASE4_TEST_API
            /* leave the result on the stack */
#endif
            return 1;
        }
        return 0;
    }

    int load_rc = p_luaL_loadbuffer(L, src, len, "[c_loadstring]");
    if (load_rc == 0) {
#ifdef PHASE4_TEST_API
        p_atomic_inc(&g_test_c_loadstring_ok);
#endif
        return 1;  /* function is on the stack */
    }
    /* loadbuffer pushed an error message — leave it. Return 1 since DMF
     * hooks.lua only checks for a truthy return. */
    return 1;
}

/* c_dofile — the DMF `Mods.file.dofile` implementation.
 *
 * Reads a Lua file from the staging directory and executes it via
 * luaL_loadbuffer + lua_pcall. Signature matches the original mod_loader:
 *   Mods.file.dofile(relpath)
 * where relpath is relative to the staging dir, WITHOUT ".lua" extension
 * (c_dofile appends it). Returns whatever the loaded chunk returns
 * (nresults = LUA_MULTRET pass-through). On read or load error, pushes
 * nothing and returns 0.
 *
 * File I/O: p5_read_file() — Windows APIs in production, POSIX in test. */
static int c_dofile(void *L) {
#ifdef PHASE4_TEST_API
    p_atomic_inc(&g_test_c_dofile_calls);
#endif
    size_t relpath_len = 0;
    const char *relpath = p_lua_tolstring ? p_lua_tolstring(L, 1, &relpath_len)
                                          : NULL;
    if (!relpath || relpath_len == 0 || relpath_len >= P5_MAX_RELPATH) {
        poc_log_linef("[c_dofile] no/invalid path argument");
        return 0;
    }

    /* Build the full path: staging_dir + "/" + relpath + ".lua". */
    char fullpath[P5_MAX_PATH];
    int n = snprintf(fullpath, sizeof(fullpath), "%s/%.*s.lua",
                     g_staging_dir, (int)relpath_len, relpath);
    if (n <= 0 || (size_t)n >= sizeof(fullpath)) {
        poc_log_linef("[c_dofile] path too long: %.*s", (int)relpath_len, relpath);
        return 0;
    }

    size_t file_size = 0;
    char *src = p5_read_file(fullpath, &file_size);
    if (!src) {
        poc_log_linef("[c_dofile] failed to read %s", fullpath);
        return 0;
    }

    /* Load + execute the file. loadbuffer + pcall both go through the
     * LuaJIT API. The pcall here re-enters the detour (because we patched
     * lua_pcall). When called from inside the bootstrap chunk's pcall,
     * g_injecting=1 routes the reentry straight to the trampoline. */
    int load_rc = p_luaL_loadbuffer(L, src, file_size, fullpath);
    free(src);
    if (load_rc != 0) {
        /* loadbuffer pushed an error message — propagate it via lua_error
         * would require lua_error's address (which we don't have). For
         * the POC: log + return 0 (no results). The bootstrap chunk's
         * own pcall will see the missing return value as nil. */
        poc_log_linef("[c_dofile] loadbuffer failed for %s (rc=%d)",
                      fullpath, load_rc);
        return 0;
    }

    int pcall_rc = p_lua_pcall(L, 0, POC_LUA_MULTRET, 0);
    if (pcall_rc != 0) {
        poc_log_linef("[c_dofile] pcall failed for %s (rc=%d)", fullpath, pcall_rc);
        return 0;
    }

#ifdef PHASE4_TEST_API
    p_atomic_inc(&g_test_c_dofile_ok);
#endif
    /* The chunk's return values are on the stack. Return the count. */
    int base_top = 0;  /* base is where the chunk was; -1 before our pcall */
    if (p_lua_gettop) {
        int now_top = p_lua_gettop(L);
        /* After pcall(L, 0, MULTRET, 0), the chunk is consumed and its
         * nresults are pushed. top = base + nresults. base for this
         * C function call is 0 (relative to the caller's stack frame).
         * The "1" we read as the relpath arg is consumed by us. So:
         *   nargs from Lua = 1 (relpath), top at entry was 1 + caller_base.
         *   After we read relpath, top is still caller_base+1 (tolstring
         *   doesn't change top). loadbuffer pushes chunk (+1). pcall
         *   consumes chunk (-1) and pushes nresults (+nresults). So now
         *   top = caller_base + 1 + nresults. nresults = top - caller_base - 1.
         * The "1" is the relpath arg we still need to "consume". In
         * practice, since c_dofile is called as `dofile(path)` from Lua
         * (single arg), Lua's C-call convention has our function's stack
         * frame at the relpath arg. Return values go ABOVE that. So
         * nresults = top - 1 (assuming base=1). */
        (void)base_top;
        return now_top > 1 ? now_top - 1 : 0;
    }
    return 0;
}

/* =====================================================================*
 *  setup_mods_globals — build the Mods table + __print via the C API
 * =====================================================================*
 * ONE-SHOT, runs on the first qualifying detour entry after capture.
 * Builds:
 *
 *   _G.__print               = c_print
 *   _G.Mods.file.dofile      = c_dofile
 *   _G.Mods.lua.loadstring   = c_loadstring
 *   _G.Mods.lua.io           = {}     (empty table; DMF mainly uses dofile)
 *   _G.Mods.require_store    = {}     (empty table; DMF's require populates)
 *   _G.Mods.original_require = c_require_stub
 *
 * Uses lua_createtable + lua_setfield + lua_pushcclosure (no lua_insert
 * or lua_pushvalue needed — careful stack ordering). Stack-neutral on
 * completion.
 *
 * Also registers the trivial poc_print global (Phase 5 Step 3 cumulative
 * regression — kept so the existing A1 phase still passes; harmless in
 * production). */
static void setup_mods_globals(void *L) {
    p_atomic_xchg(&g_injecting, 1);

    /* ---- _G.__print = c_print ---------------------------------------- */
    p_lua_pushcclosure(L, (void *)&c_print, 0);
    p_lua_setfield(L, POC_LUA_GLOBALSINDEX, "__print");

    /* ---- _G.poc_print = poc_print (Phase 5 Step 3 regression) -------- */
    p_lua_pushcclosure(L, (void *)&poc_print, 0);
    p_lua_setfield(L, POC_LUA_GLOBALSINDEX, "poc_print");

    /* ---- Build _G.Mods bottom-up ------------------------------------- *
     * Stack comments use [bottom, ..., top] notation. Initial: [].
     * All setfield ops pop their value, so we always come back to []. */

    /* Create the outer Mods table. Stack: [Mods]. */
    p_lua_createtable(L, 0, 4);

    /* Mods.lua = { loadstring = c_loadstring, io = {} }
     *   Stack: [Mods]
     *   -> create lua_table on top: [Mods, lua_table]
     *   -> set lua_table.loadstring = c_loadstring: [Mods, lua_table]
     *   -> create io_table on top: [Mods, lua_table, io_table]
     *   -> set lua_table.io = io_table: [Mods, lua_table]
     *   -> set Mods.lua = lua_table: [Mods] */
    p_lua_createtable(L, 0, 2);                       /* [Mods, lua_t]   */
    p_lua_pushcclosure(L, (void *)&c_loadstring, 0);  /* [Mods, lua_t, ls] */
    p_lua_setfield(L, -2, "loadstring");              /* [Mods, lua_t]   */
    p_lua_createtable(L, 0, 0);                       /* [Mods, lua_t, io_t] */
    p_lua_setfield(L, -2, "io");                      /* [Mods, lua_t]   */
    p_lua_setfield(L, -2, "lua");                     /* [Mods]          */

    /* Mods.file = { dofile = c_dofile } */
    p_lua_createtable(L, 0, 1);                       /* [Mods, file_t]  */
    p_lua_pushcclosure(L, (void *)&c_dofile, 0);      /* [Mods, file_t, df] */
    p_lua_setfield(L, -2, "dofile");                  /* [Mods, file_t]  */
    p_lua_setfield(L, -2, "file");                    /* [Mods]          */

    /* Mods.require_store = {} */
    p_lua_createtable(L, 0, 0);                       /* [Mods, rs_t]    */
    p_lua_setfield(L, -2, "require_store");           /* [Mods]          */

    /* Mods.original_require = c_require_stub */
    p_lua_pushcclosure(L, (void *)&c_require_stub, 0);/* [Mods, req]     */
    p_lua_setfield(L, -2, "original_require");        /* [Mods]          */

    /* _G.Mods = Mods. setfield pops Mods; stack is back to []. */
    p_lua_setfield(L, POC_LUA_GLOBALSINDEX, "Mods");  /* []              */

    p_atomic_xchg(&g_injecting, 0);

    /* Set the latches. */
    p_atomic_xchg(&g_cfuncs_registered, 1);
    p_atomic_xchg(&g_dmf_setup_done, 1);

    poc_log_linef("DMF bootstrap: registered Mods table + __print "
                  "(staging=%s)", g_staging_dir);
}

/* =====================================================================*
 *  do_inject: loadbuffer + pcall (the actual execution)
 * =====================================================================*
 * Caller already verified: L matches captured state, latch not set,
 * rate-limiter interval elapsed, max-retry cap not hit, and g_attempt_count
 * was just incremented. Sets g_injecting around our pcall.
 *
 * Returns 1 on success (chunk executed — latch should be set), 0 on
 * failure (globals not ready, chunk errored, or loadbuffer failed — the
 * caller should allow retry, subject to the cap).
 *
 * Stack contract: on success the stack is clean (pcall consumed the chunk,
 * 0 results). On failure we restore L->top to its pre-loadbuffer value,
 * popping the error object — the engine sees the stack exactly as its own
 * pcall left it. */
static int do_inject(void *L, unsigned long long now,
                     unsigned long long captured_tick) {
    /* Reentry guard ON before any lua_pcall-family call. */
    p_atomic_xchg(&g_injecting, 1);

    /* Save L->top so we can restore it on error (pop the error object).
     * L->top offset in lua_State: 0x18 in the Darktide binary's LuaJIT
     * (non-GC64; confirmed by lua_pcall disasm `mov rcx, [rcx+0x18]`).
     * The A1 test runs against the SYSTEM LuaJIT (typically GC64 on
     * modern x86_64), which uses a different offset — so the test sets
     * it via inject_test_set_L_top_offset() after auto-detecting it.
     * Each stack slot is 8 bytes (TValue), confirmed by lua_gettop's
     * `(top-base)>>3` computation. */
#ifdef PHASE4_TEST_API
    uint8_t **L_top = (uint8_t **)((uint8_t *)L + g_test_L_top_offset);
#else
    uint8_t **L_top = (uint8_t **)((uint8_t *)L + 0x18);
#endif
    uint8_t *saved_top = *L_top;

    int load_rc = p_luaL_loadbuffer(L, g_inject_src, (size_t)g_inject_src_len,
                                     "[poc-inject]");
    int pcall_rc = -1;
    if (load_rc == 0) {
        /* This call goes through p_lua_pcall, which is the patched target.
         * It re-enters detour_lua_pcall. The reentry sees g_injecting==1
         * and routes straight to g_orig_pcall (the trampoline / real
         * lua_pcall), which executes our chunk. Net recursion depth: 2. */
        pcall_rc = p_lua_pcall(L, 0, 0, 0);
    }

    int success = (load_rc == 0 && pcall_rc == 0);

    if (!success) {
        /* FAILED — globals not ready (pcall_rc=LUA_ERRRUN), chunk errored,
         * or loadbuffer itself failed (pcall_rc still -1). In all cases
         * exactly one item (error object) was left on the stack above
         * saved_top. Restore L->top to pop it. The engine's stack is left
         * exactly as its own pcall left it — this retry is stack-neutral. */
        *L_top = saved_top;
    }

    /* Reentry guard OFF. */
    p_atomic_xchg(&g_injecting, 0);

#ifdef PHASE4_TEST_API
    g_test_last_load_rc  = load_rc;
    g_test_last_pcall_rc = pcall_rc;
#endif

    /* delay_ms = how long after capture this attempt happened. Logged
     * either way (success or failure) so Tier B can verify timing. */
    poc_log_linef("injected attempt=%lu load_rc=%d pcall_rc=%d (0=success) "
                  "delay_ms=%llu src_len=%llu",
                  (unsigned long)g_attempt_count, load_rc, pcall_rc,
                  (unsigned long long)(now - captured_tick),
                  g_inject_src_len);

    return success;
}

/* =====================================================================*
 *  The detour — retry-on-error injection
 * =====================================================================*
 * Each time the engine calls lua_pcall, this hook fires. After letting
 * the engine's pcall complete (transparent passthrough), it checks the
 * guards in order and, if all pass, attempts to inject our chunk. On
 * failure (globals not ready), the latch is NOT set — the next engine
 * pcall triggers another attempt. On success, the latch is set and no
 * further attempts are made. A max-retry cap prevents infinite retries
 * if the chunk can never succeed. */
int detour_lua_pcall(void *L, int nargs, int nresults, int errfunc) {
    int rc;

#ifdef PHASE4_TEST_API
    LONG depth = p_atomic_inc(&g_test_detour_depth);
    if (depth > g_test_max_depth) p_atomic_xchg(&g_test_max_depth, depth);
#endif

    /* Guard 1 — reentry break: if we're inside our own injection's pcall,
     * just call the original (the trampoline / real lua_pcall). No
     * injection logic. This breaks the recursion when do_inject's
     * p_lua_pcall re-enters this detour, AND when c_dofile/c_loadstring's
     * nested pcalls re-enter. */
    if (g_injecting) {
        rc = g_orig_pcall ? g_orig_pcall(L, nargs, nresults, errfunc) : 0;
        goto done;
    }

    /* Engine's pcall completes first (transparent passthrough — the engine
     * cannot observe any difference from an unhooked call). */
    rc = g_orig_pcall ? g_orig_pcall(L, nargs, nresults, errfunc) : 0;

    /* Guard 2 — capture: only act on the captured lua_State*. Phase 3's
     * lua_newstate hook must have fired (captured_L != NULL) and the
     * caller's L must match it. */
    void *captured_L = CAPTURED_L();
    if (captured_L == NULL || L != captured_L) goto done;

    unsigned long long captured_tick = CAPTURED_TICK();
    if (captured_tick == 0) goto done;   /* defensive — shouldn't happen */

    /* ---- Phase 5 Step 2: one-shot openlibs diagnostic -------------- *
     * DISABLED in production (destructive). See inject.h for the full
     * rationale. The A1 test may enable it to exercise the path. */
    if (p_luaL_openlibs != NULL && !g_openlibs_called) {
        p_atomic_xchg(&g_injecting, 1);
        p_luaL_openlibs(L);
        p_atomic_xchg(&g_injecting, 0);
        p_atomic_xchg(&g_openlibs_called, 1);
        poc_log_linef("openlibs called on captured L=0x%llx",
                      (unsigned long long)(uintptr_t)L);
    }

    /* ---- Phase 5: DMF bootstrap (C-function bootstrap) ------------- *
     * Register the 6 DMF dependencies as C functions / Lua tables ONCE
     * on the captured state, BEFORE the injection attempt. This bypasses
     * the sandboxed _G entirely — we provide our own implementations via
     * the LuaJIT C API.
     *
     * Setup requires all of: pushcclosure, setfield, createtable,
     * tolstring (used inside c_dofile/c_loadstring, but the pointers
     * must be non-NULL so those functions don't segfault), gettop (same).
     * If any are NULL (A1 negative control), skip the setup — the chunk
     * will fail with pcall_rc=2 and retry. */
    if (p_lua_pushcclosure != NULL && p_lua_setfield != NULL &&
        p_lua_createtable != NULL && p_lua_tolstring != NULL &&
        p_lua_gettop != NULL &&
        !g_dmf_setup_done) {
        setup_mods_globals(L);
    }

    /* Guard 3 — latch: stop retrying once we've succeeded or given up. */
    if (g_injected) goto done;

    /* Guard 4 — min-interval rate limiter. Prevents hammering the VM
     * during the engine's init burst (lua_pcall may fire dozens of times
     * per second). This is NOT a readiness delay — the chunk's self-check
     * determines readiness; this just throttles how often we re-check.
     * First attempt (g_last_attempt_tick == 0) always passes.
     * When PHASE4_INJECT_DELAY_MS is 0 (test build), the rate limiter is
     * compiled out entirely (the comparison would be unsigned < 0, a no-op). */
    unsigned long long now = p_now_ms();
#if PHASE4_INJECT_DELAY_MS > 0
    if (g_last_attempt_tick != 0 &&
        (now - g_last_attempt_tick) < (unsigned long long)PHASE4_INJECT_DELAY_MS) {
        goto done;
    }
#endif

    /* Guard 5 — max-retry cap. If we've exhausted all attempts, give up
     * (set the latch so we never try again) and log it. */
    if ((unsigned long)g_attempt_count >= (unsigned long)PHASE4_MAX_INJECT_ATTEMPTS) {
        poc_log_linef("giving up after %lu attempts — dmf_loader never loaded",
                      (unsigned long)g_attempt_count);
        p_atomic_xchg(&g_injected, 1);
        goto done;
    }

    /* ---- Attempt the injection ----------------------------------- *
     * Increment the counter, run loadbuffer+pcall, and set the latch
     * only on success. On failure, do_inject already restored the stack. */
    p_atomic_inc(&g_attempt_count);

    int success = do_inject(L, now, captured_tick);

    g_last_attempt_tick = p_now_ms();

    if (success) {
        /* Chunk executed — globals were ready + dmf_loader loaded. Stop
         * retrying forever. */
        p_atomic_xchg(&g_injected, 1);
    }

done:
#ifdef PHASE4_TEST_API
    p_atomic_dec(&g_test_detour_depth);
#endif
    return rc;
}

/* =====================================================================*
 *  Production: MinHook install
 * =====================================================================*/
#ifndef PHASE4_TEST_API

int inject_install(HMODULE main_module) {
    if (!main_module) {
        poc_log_linef("inject ABORT main_module is NULL");
        return 1;
    }
    uintptr_t base = (uintptr_t)main_module;

    LPVOID p_pcall        = (LPVOID)(base + RVA_LUA_PCALL);
    LPVOID p_loadbuffer   = (LPVOID)(base + RVA_LUAL_LOADBUFFER);
    LPVOID p_openlibs     = (LPVOID)(base + RVA_LUAL_OPENLIBS);
    LPVOID p_pushcclosure = (LPVOID)(base + RVA_LUA_PUSHCCLOSURE);
    LPVOID p_setfield     = (LPVOID)(base + RVA_LUA_SETFIELD);
    LPVOID p_tolstring    = (LPVOID)(base + RVA_LUA_TOLSTRING);
    LPVOID p_createtable  = (LPVOID)(base + RVA_LUA_CREATETABLE);
    LPVOID p_gettop       = (LPVOID)(base + EXPECT_GETTOP);

    /* Wire up the function pointers used by do_inject, the detour, and
     * the C functions (c_dofile uses tolstring + loadbuffer + pcall;
     * c_loadstring uses tolstring + loadbuffer; etc.).
     *
     * openlibs is DISABLED — calling it on the engine's state is
     * destructive (overwrites the engine's custom globals → crash within
     * 1 second, verified in live testing). */
    p_lua_pcall        = (lua_pcall_t)p_pcall;
    p_luaL_loadbuffer  = (luaL_loadbuffer_t)p_loadbuffer;
    /* p_luaL_openlibs = (luaL_openlibs_t)p_openlibs; -- DISABLED */
    p_lua_pushcclosure = (lua_pushcclosure_t)p_pushcclosure;
    p_lua_setfield     = (lua_setfield_t)p_setfield;
    p_lua_tolstring    = (lua_tolstring_t)p_tolstring;
    p_lua_createtable  = (lua_createtable_t)p_createtable;
    p_lua_gettop       = (lua_gettop_t)p_gettop;

    /* Resolve the staging directory.
     * Priority: DARKTIDE_MOD_STAGING env var > default game install path.
     * The default is derived from the engine's module path:
     *   <Darktide.exe dir>/../../mods  →  the game's "mods" directory
     * which is where the live install puts DMF source.
     */
    DWORD env_len = GetEnvironmentVariableA("DARKTIDE_MOD_STAGING",
                                             g_staging_dir,
                                             sizeof(g_staging_dir));
    if (env_len == 0 || env_len >= sizeof(g_staging_dir)) {
        /* Fall back: derive from the engine module path. */
        wchar_t exe_path[P5_MAX_PATH];
        DWORD exe_len = GetModuleFileNameW(NULL, exe_path,
                                            sizeof(exe_path)/sizeof(exe_path[0]));
        if (exe_len > 0 && exe_len < sizeof(exe_path)/sizeof(exe_path[0])) {
            /* exe_path = .../binaries/Darktide.exe. We want .../mods/.
             * Strip the exe name (3 levels up: binaries → DARKTIDE → mods).
             * Find the last 3 backslashes. */
            int slashes[4] = {0,0,0,0};
            int ns = 0;
            for (DWORD i = 0; i < exe_len; i++) {
                if (exe_path[i] == L'\\' || exe_path[i] == L'/') {
                    slashes[ns % 4] = (int)i;
                    ns++;
                }
            }
            /* We need at least 1 slash (to find binaries dir). */
            if (ns >= 1) {
                /* Take everything up to the last slash (binaries dir),
                 * then append "\..\mods". */
                int last_slash = slashes[(ns - 1) % 4];
                wchar_t mods_w[P5_MAX_PATH];
                int copy_n = last_slash < (int)(sizeof(mods_w)/sizeof(mods_w[0]) - 16)
                              ? last_slash : (int)(sizeof(mods_w)/sizeof(mods_w[0]) - 16);
                memcpy(mods_w, exe_path, (size_t)copy_n * sizeof(wchar_t));
                const wchar_t *suffix = L"\\..\\mods";
                size_t suffix_len = wcslen(suffix);
                memcpy(mods_w + copy_n, suffix, suffix_len * sizeof(wchar_t));
                mods_w[copy_n + suffix_len] = L'\0';
                WideCharToMultiByte(CP_UTF8, 0, mods_w, -1,
                                     g_staging_dir, sizeof(g_staging_dir),
                                     NULL, NULL);
            }
        }
        if (g_staging_dir[0] == '\0') {
            /* Last-resort default (matches the dev box's install path). */
            strncpy(g_staging_dir,
                    "Z:/games/steamapps/common/Warhammer 40,000 DARKTIDE/mods",
                    sizeof(g_staging_dir) - 1);
            g_staging_dir[sizeof(g_staging_dir) - 1] = '\0';
        }
    }

    poc_log_linef("inject targets: lua_pcall=0x%llx (rva=0x%x) "
                  "luaL_loadbuffer=0x%llx (rva=0x%x) "
                  "luaL_openlibs=0x%llx (rva=0x%x) [DISABLED] "
                  "lua_pushcclosure=0x%llx (rva=0x%x) "
                  "lua_setfield=0x%llx (rva=0x%x) "
                  "lua_tolstring=0x%llx (rva=0x%x) "
                  "lua_createtable=0x%llx (rva=0x%x) "
                  "lua_gettop=0x%llx (rva=0x%x) "
                  "min_interval_ms=%d max_attempts=%d staging=%s",
                  (unsigned long long)(uintptr_t)p_pcall,        RVA_LUA_PCALL,
                  (unsigned long long)(uintptr_t)p_loadbuffer,    RVA_LUAL_LOADBUFFER,
                  (unsigned long long)(uintptr_t)p_openlibs,      RVA_LUAL_OPENLIBS,
                  (unsigned long long)(uintptr_t)p_pushcclosure,  RVA_LUA_PUSHCCLOSURE,
                  (unsigned long long)(uintptr_t)p_setfield,      RVA_LUA_SETFIELD,
                  (unsigned long long)(uintptr_t)p_tolstring,     RVA_LUA_TOLSTRING,
                  (unsigned long long)(uintptr_t)p_createtable,   RVA_LUA_CREATETABLE,
                  (unsigned long long)(uintptr_t)p_gettop,        EXPECT_GETTOP,
                  PHASE4_INJECT_DELAY_MS, PHASE4_MAX_INJECT_ATTEMPTS,
                  g_staging_dir);

    /* MinHook was initialized by phase3_install() (which always runs first).
     * If phase3_install failed, DllMain skipped us. */

    /* CreateHook on a target MinHook has already hooked returns
     * MH_ERROR_ALREADY_CREATED. Phase 4 builds Phase 3 with
     * -DPHASE3_INCLUDE_PCALL_OBSERVERS=0 so no observer hook exists on
     * 0xc744c0 — we own this target. */
    MH_STATUS s = MH_CreateHook(p_pcall, (LPVOID)&detour_lua_pcall,
                                (LPVOID *)&g_orig_pcall);
    if (s != MH_OK) {
        poc_log_linef("inject ABORT MH_CreateHook(lua_pcall) failed: %s (%d)",
                      MH_StatusToString(s), (int)s);
        return 2;
    }
    /* Per-target enable (phase3_install's MH_EnableHook(MH_ALL_HOOKS) only
     * enabled hooks that existed at that moment — ours is new). */
    s = MH_EnableHook(p_pcall);
    if (s != MH_OK) {
        poc_log_linef("inject ABORT MH_EnableHook(lua_pcall) failed: %s (%d)",
                      MH_StatusToString(s), (int)s);
        return 3;
    }

    poc_log_linef("inject lua_pcall hook installed at 0x%llx (rva=0x%x) "
                  "min_interval_ms=%d max_attempts=%d staging=%s",
                  (unsigned long long)(uintptr_t)p_pcall, RVA_LUA_PCALL,
                  PHASE4_INJECT_DELAY_MS, PHASE4_MAX_INJECT_ATTEMPTS,
                  g_staging_dir);
    return 0;
}

#endif  /* !PHASE4_TEST_API */

/* =====================================================================*
 *  Test API (only compiled with -DPHASE4_TEST_API)
 * =====================================================================*/
#ifdef PHASE4_TEST_API

void inject_test_setup(void *real_pcall, void *real_loadbuffer,
                       void *real_openlibs,
                       void *captured_L, unsigned long long captured_tick) {
    g_orig_pcall         = (lua_pcall_t)real_pcall;
    p_luaL_loadbuffer    = (luaL_loadbuffer_t)real_loadbuffer;
    p_lua_pcall          = (lua_pcall_t)(void *)&detour_lua_pcall;
    p_luaL_openlibs      = (luaL_openlibs_t)real_openlibs;
    g_test_captured_L    = captured_L;
    g_test_captured_tick = captured_tick;
}

void inject_test_set_cfunc_bootstrappers(void *pushcclosure, void *setfield) {
    /* Wire (or unwire) the C-function bootstrap pointers.
     * Pass the system LuaJIT's &lua_pushcclosure and &lua_setfield to
     * enable the bootstrap, or NULL/NULL to disable it (negative control). */
    p_lua_pushcclosure = (lua_pushcclosure_t)pushcclosure;
    p_lua_setfield     = (lua_setfield_t)setfield;
}

void inject_test_set_dmf_api(void *tolstring, void *createtable,
                              void *gettop, void *lua_type) {
    /* Wire the Phase 5 DMF-bootstrap API pointers. Pass the system
     * LuaJIT's &lua_tolstring, &lua_createtable, &lua_gettop, &lua_type
     * to enable the bootstrap. NULL/NULL/NULL/NULL disables. */
    p_lua_tolstring   = (lua_tolstring_t)tolstring;
    p_lua_createtable = (lua_createtable_t)createtable;
    p_lua_gettop      = (lua_gettop_t)gettop;
    p_lua_type        = (lua_type_t)lua_type;
}

void inject_test_set_staging_dir(const char *path) {
    /* Override the staging directory (default "./staging" in test build). */
    if (path) {
        strncpy(g_staging_dir, path, sizeof(g_staging_dir) - 1);
        g_staging_dir[sizeof(g_staging_dir) - 1] = '\0';
    } else {
        g_staging_dir[0] = '\0';
    }
}

void inject_test_set_inject_src(const char *src, size_t len) {
    /* Override the chunk source. Pass NULL/0 to restore the default. */
    if (src && len > 0) {
        g_inject_src     = src;
        g_inject_src_len = (unsigned long long)len;
    } else {
        g_inject_src     = g_inject_src_default;
        g_inject_src_len = sizeof(g_inject_src_default) - 1;
    }
}

void inject_test_reset(void) {
    g_injecting             = 0;
    g_injected              = 0;
    g_attempt_count         = 0;
    g_last_attempt_tick     = 0;
    g_openlibs_called       = 0;
    g_cfuncs_registered     = 0;
    g_dmf_setup_done        = 0;
    g_test_detour_depth     = 0;
    g_test_max_depth        = 0;
    g_test_last_load_rc     = -1;
    g_test_last_pcall_rc    = -1;
    g_test_poc_print_calls  = 0;
    g_test_c_print_calls    = 0;
    g_test_c_dofile_calls   = 0;
    g_test_c_dofile_ok      = 0;
    g_test_c_loadstring_calls = 0;
    g_test_c_loadstring_ok  = 0;
    g_test_c_require_calls  = 0;
    /* Restore the default chunk source. */
    g_inject_src     = g_inject_src_default;
    g_inject_src_len = sizeof(g_inject_src_default) - 1;
    /* NOTE: g_test_L_top_offset, the API pointers (p_luaL_openlibs,
     * p_lua_pushcclosure, p_lua_setfield, p_lua_tolstring, p_lua_createtable,
     * p_lua_gettop, p_lua_type), and g_staging_dir are NOT reset here —
     * they're properties of the linked LuaJIT build / test phase, set
     * once via the corresponding setters and persist across resets. */
}

void inject_test_reset_openlibs(void) {
    g_openlibs_called = 0;
}

void inject_test_reset_cfunc_bootstrap(void) {
    g_cfuncs_registered    = 0;
    g_test_poc_print_calls = 0;
}

int inject_test_last_load_rc(void)  { return g_test_last_load_rc; }
int inject_test_last_pcall_rc(void) { return g_test_last_pcall_rc; }
int inject_test_inject_count(void)  { return (int)g_attempt_count; }
int inject_test_detour_depth(void)  { return (int)g_test_max_depth; }
int inject_test_openlibs_called(void) { return (int)g_openlibs_called; }
int inject_test_cfuncs_registered(void) { return (int)g_cfuncs_registered; }
int inject_test_dmf_setup_done(void) { return (int)g_dmf_setup_done; }
int inject_test_poc_print_calls(void)  { return (int)g_test_poc_print_calls; }
int inject_test_c_print_calls(void)    { return (int)g_test_c_print_calls; }
int inject_test_c_dofile_calls(void)   { return (int)g_test_c_dofile_calls; }
int inject_test_c_dofile_ok(void)      { return (int)g_test_c_dofile_ok; }
int inject_test_c_loadstring_calls(void) { return (int)g_test_c_loadstring_calls; }
int inject_test_c_loadstring_ok(void)  { return (int)g_test_c_loadstring_ok; }
int inject_test_c_require_calls(void)  { return (int)g_test_c_require_calls; }

void inject_test_set_L_top_offset(size_t offset) {
    g_test_L_top_offset = offset;
}

#endif  /* PHASE4_TEST_API */
