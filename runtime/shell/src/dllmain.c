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
 * Phase-1 engine-context probe (this build): the worker additionally installs
 * detours on four Lua-lifecycle points and, at each, reads a fixed list of
 * engine globals via the LuaJIT C API (`lua_getfield` + `lua_type`) and logs
 * presence/type. This is READ-ONLY recon — no global is *called*, no Lua is
 * loaded, no mods; the only side effect is log output. The log answers:
 *   - At which lifecycle point does the engine's globals table contain the
 *     full bundle-script environment (io, require, loadstring, CLASS, Managers,
 *     ...)? That point is the engine-context entry for DLL injection (the
 *     patch_999-equivalent), or evidence it is not achievable.
 *
 * Hook points (all use the single captured lua_State*):
 *   - lua_newstate      (1-shot, right after capture)            — VM fresh, baseline
 *   - luaL_openlibs     (1-shot, after the engine call returns)  — stdlib registered
 *   - luaL_loadbuffer   (calls 1, 5, 20)                         — script loads
 *   - lua_pcall         (calls 1, 2)                             — POC injection point
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
 * goes to OutputDebugString + a log file (MAGOS_LOG_FILE env, or
 * magos_spike.log beside the game exe).
 */
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>

#include "magos_discovery.h"
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
typedef void (*openlibs_t)(lua_State *L);
typedef int  (*loadbuffer_t)(lua_State *L, const char *buf, size_t size, const char *name);
typedef int  (*pcall_t)(lua_State *L, int nargs, int nresults, int errfunc);

/* ---- resolved from the discovered RVAs ---- */
static newstate_t   g_orig_newstate = NULL;
static openlibs_t   g_orig_openlibs = NULL;
static loadbuffer_t g_orig_loadbuffer = NULL;
static pcall_t      g_orig_pcall = NULL;
static gettop_t     g_lua_gettop = NULL;
static settop_t     g_lua_settop = NULL;
static type_t       g_lua_type = NULL;
static getfield_t   g_lua_getfield = NULL;

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
static volatile int  g_openlibs_logged = 0;
static volatile int  g_newstate_logged = 0;
static volatile int  g_in_probe = 0;

/* The fixed list of globals the probe checks at each lifecycle point. */
static const char *PROBE_GLOBALS[] = {
    "print", "require", "dofile", "loadfile", "load",
    "loadstring", "io", "pcall", "pairs", "table",
    "string", "math", "CLASS", "Managers", "_G", "_VERSION",
};
#define PROBE_NGLOBALS (sizeof(PROBE_GLOBALS) / sizeof(PROBE_GLOBALS[0]))

static void magos_log(const char *fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n > 0) {
        OutputDebugStringA(buf);
        if (g_log) {
            fputs(buf, g_log);
            fflush(g_log);
        }
    }
}

static void open_log(void) {
    char path[MAX_PATH];
    static const char logname[] = "magos_spike.log";
    DWORD n = GetEnvironmentVariableA("MAGOS_LOG_FILE", path, sizeof(path));
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
    g_log = fopen(path, "a");
    magos_log("[magos] log -> %s\n", path);
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
        magos_log("[probe] %s call#%d  (skipped: probe C-API not resolved)\n", point, call_num);
        g_in_probe = 0;
        return;
    }
    if (g_L && L != g_L) {
        magos_log("[probe] %s call#%d  (note: L=%p differs from captured g_L=%p)\n",
                  point, call_num, (void *)L, (void *)g_L);
    }
    magos_log("[probe] %s call#%d\n", point, call_num);
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
            if (per_line > 0) magos_log("[probe]%s\n", line);
            break;
        }
        off += n;
        if (++per_line >= 5) {
            magos_log("[probe]%s\n", line);
            off = 0;
            per_line = 0;
        }
    }
    if (per_line > 0) {
        magos_log("[probe]%s\n", line);
    }
    g_lua_settop(L, base);  /* restore stack — read-only, zero net effect */
    g_in_probe = 0;
}

/* ---- lifecycle detours ---- */

/* lua_newstate — capture L (baseline; VM fresh, no stdlib/globals yet). */
static lua_State *detour_newstate(lua_Alloc f, void *ud) {
    lua_State *L = g_orig_newstate(f, ud);
    if (L) {
        magos_log("[magos] step4: lua_newstate hook fired; L = %p\n", (void *)L);
        if (!g_L) g_L = L;
        /* step 5: one LuaJIT C-API call against the documented offsets.
         * lua_gettop(L) = (L->top - L->base) >> 3; for a fresh state top==base
         * so it must return 0, confirming the LJ_64 non-GC64 struct layout. */
        if (g_lua_gettop) {
            int top = g_lua_gettop(L);
            magos_log("[magos] step5: lua_gettop(L) = %d (expect 0 for fresh state)\n", top);
        }
        if (!g_newstate_logged) {
            g_newstate_logged = 1;
            probe_log_globals(L, "lua_newstate", 1);
        }
    } else {
        magos_log("[magos] step4: lua_newstate returned NULL\n");
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

/* luaL_loadbuffer — the bytecode-load proxy (see file header NOTE). Samples
 * calls 1, 5, 20; logs the script `name` for each sampled call. */
static int detour_loadbuffer(lua_State *L, const char *buff, size_t size, const char *name) {
    LONG n = 0;
    int do_probe = 0;
    if (!g_in_probe) {
        n = InterlockedIncrement(&g_loadbuffer_calls);
        do_probe = (n == 1 || n == 5 || n == 20);
    }
    if (do_probe) {
        if (!g_L) g_L = L;
        char namebuf[128];
        snprintf(namebuf, sizeof(namebuf), "%s", name ? name : "<null>");
        magos_log("[probe] luaL_loadbuffer (bytecode-load proxy) call#%ld  name=%s  size=%zu\n",
                  n, namebuf, size);
        probe_log_globals(L, "luaL_loadbuffer", (int)n);
    }
    return g_orig_loadbuffer(L, buff, size, name);
}

/* lua_pcall — the POC's injection point (POC found executed chunks sandboxed).
 * Samples the first 2 engine calls. */
static int detour_pcall(lua_State *L, int nargs, int nresults, int errfunc) {
    LONG n = 0;
    int do_probe = 0;
    if (!g_in_probe) {
        n = InterlockedIncrement(&g_pcall_calls);
        do_probe = (n == 1 || n == 2);
    }
    if (do_probe) {
        if (!g_L) g_L = L;
        probe_log_globals(L, "lua_pcall", (int)n);
    }
    return g_orig_pcall(L, nargs, nresults, errfunc);
}

/* Create + enable a MinHook detour. Returns 1 on success, 0 on failure. */
static int install_hook(void *target, void *detour, void **original, const char *name) {
    MH_STATUS mh = MH_CreateHook(target, detour, original);
    if (mh != MH_OK) {
        magos_log("[magos] MH_CreateHook(%s) failed: %d\n", name, mh);
        return 0;
    }
    mh = MH_EnableHook(target);
    if (mh != MH_OK) {
        magos_log("[magos] MH_EnableHook(%s) failed: %d\n", name, mh);
        return 0;
    }
    magos_log("[magos] hook installed: %s at %p (detour %p)\n", name, target, detour);
    return 1;
}

/* ---- worker: seam call (step 6) + hook install (step 4) ---- */
static DWORD WINAPI worker(LPVOID arg) {
    (void)arg;
    open_log();
    magos_log("[magos] === DllMain worker started (pid=%lu) ===\n", GetCurrentProcessId());

    HMODULE h = GetModuleHandleW(NULL);  /* Darktide.exe */
    if (!h) { magos_log("[magos] FATAL: GetModuleHandle(NULL) failed\n"); return 1; }
    MODULEINFO mi;
    if (!GetModuleInformation(GetCurrentProcess(), h, &mi, sizeof(mi))) {
        magos_log("[magos] FATAL: GetModuleInformation failed (lu=%lu)\n", GetLastError());
        return 1;
    }
    g_module_base = (uint8_t *)h;
    magos_log("[magos] module base = %p, SizeOfImage = 0x%lx\n",
         (void *)g_module_base, (unsigned long)mi.SizeOfImage);

    /* step 6: invoke the Rust discovery seam in-process. */
    MagosAddressTable tbl;
    uint8_t detail[256] = {0};
    int rc = magos_discover_detail(g_module_base, mi.SizeOfImage, &tbl,
                                   detail, sizeof(detail));
    if (rc != MAGOS_OK) {
        magos_log("[magos] FATAL: magos_discover rc=%d (%s)\n", rc, (char*)detail);
        return 1;
    }
    magos_log("[magos] step6: discovery OK. 16 addresses (RVAs):\n");
    magos_log("[magos]   lua_newstate_thunk  = 0x%08x  body=0x%08x\n",
         tbl.lua_newstate_thunk, tbl.lua_newstate_body);
    magos_log("[magos]   lua_atpanic=0x%08x  lua_gettop=0x%08x\n",
         tbl.lua_atpanic, tbl.lua_gettop);
    magos_log("[magos]   lua_pcall=0x%08x  luaL_loadbuffer=0x%08x\n",
         tbl.lua_pcall, tbl.lual_loadbuffer);
    magos_log("[magos]   lua_pushcclosure=0x%08x  lua_setfield=0x%08x\n",
         tbl.lua_pushcclosure, tbl.lua_setfield);
    magos_log("[magos]   lua_pushstring=0x%08x  lua_tolstring=0x%08x\n",
         tbl.lua_pushstring, tbl.lua_tolstring);
    magos_log("[magos]   lua_createtable=0x%08x  lua_type=0x%08x\n",
         tbl.lua_createtable, tbl.lua_type);
    magos_log("[magos]   lua_tonumber=0x%08x  lua_settop=0x%08x\n",
         tbl.lua_tonumber, tbl.lua_settop);
    magos_log("[magos]   luaL_openlibs=0x%08x  lua_panic_body=0x%08x\n",
         tbl.lual_openlibs, tbl.lua_panic_body);
    magos_log("[magos]   LuaEnvironment::init = 0x%08x..0x%08x\n",
         tbl.luaenvironment_init_begin, tbl.luaenvironment_init_end);
    magos_log("[magos]   [probe] lua_getfield=0x%08x  lua_resource::bytecode=0x%08x\n",
         tbl.lua_getfield, tbl.lua_resource_bytecode);

    /* resolve the LuaJIT C-API pointers used by the probe + the slice. */
    g_lua_gettop   = (gettop_t)(g_module_base + tbl.lua_gettop);
    g_lua_settop   = (settop_t)(g_module_base + tbl.lua_settop);
    g_lua_type     = (type_t)(g_module_base + tbl.lua_type);
    g_lua_getfield = (getfield_t)(g_module_base + tbl.lua_getfield);

    /* step 4: install the lifecycle hooks. lua_newstate is critical (can't
     * capture L without it); the other three are best-effort — a failure
     * degrades the probe to partial data rather than aborting. */
    MH_STATUS mh = MH_Initialize();
    if (mh != MH_OK) { magos_log("[magos] MH_Initialize failed: %d\n", mh); return 1; }
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

    magos_log("[probe] === engine-context probe (Phase 1) ===\n");
    magos_log("[probe] hooks: lua_newstate{1} luaL_openlibs{1} luaL_loadbuffer{1,5,20} lua_pcall{1,2}\n");
    magos_log("[probe] globals: print require dofile loadfile load loadstring io pcall pairs "
              "table string math CLASS Managers _G _VERSION\n");
    magos_log("[probe] NOTE: lua_resource::bytecode @0x%08x discovered (anchor) but NOT hooked "
              "(unknown C++ sig) — luaL_loadbuffer (its known-sig callee) is the proxy.\n",
              tbl.lua_resource_bytecode);

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
        magos_log("[magos] hook-ready signaled (%s)\n", MAGOS_HOOK_READY_EVENT);
    } else {
        magos_log("[magos] warning: OpenEvent(%s) failed (lu=%lu); hooks armed, not signaled\n",
             MAGOS_HOOK_READY_EVENT, GetLastError());
    }

    magos_log("[magos] worker complete; waiting for the engine lifecycle...\n");
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
