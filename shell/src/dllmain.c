/*
 * dllmain.c — Component A C shell (Spike 001 minimal validation slice).
 *
 * Linked with the Rust `magos-discovery` staticlib (C-ABI) + MinHook into one
 * PE DLL, delivered by CreateRemoteThread. This slice validates the Hybrid:
 *   - DLL_PROCESS_ATTACH spawns a worker that calls the Rust `magos_discover`
 *     seam on the live Darktide.exe image (step 6: seam integration, in-process).
 *   - Installs a MinHook detour on `lua_newstate` (step 4: hook + L capture).
 *   - In the detour, calls `lua_gettop(L)` via the discovered RVA (step 5: one
 *     LuaJIT C-API call against the documented struct offsets).
 *
 * Out of scope (per spike): DMF bootstrap, multi-shot injection, mod-manager
 * UI. Logging goes to OutputDebugString + a log file (MAGOS_LOG_FILE env, or
 * magos_spike.log beside the game exe).
 */
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>

#include "magos_discovery.h"
#include "MinHook.h"

/* ---- minimal LuaJIT type stubs (only what the slice touches) ---- */
typedef struct lua_State lua_State;
typedef void *(*lua_Alloc)(void *ud, void *ptr, size_t osize, size_t nsize);
typedef lua_State *(*newstate_t)(lua_Alloc f, void *ud);
typedef int (*gettop_t)(lua_State *L);

static newstate_t g_orig_newstate = NULL;
static gettop_t   g_lua_gettop = NULL;       /* set from the discovered RVA */
static uint8_t   *g_module_base = NULL;      /* Darktide.exe base */
static FILE      *g_log = NULL;

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
    DWORD n = GetEnvironmentVariableA("MAGOS_LOG_FILE", path, sizeof(path));
    if (n == 0 || n >= sizeof(path)) {
        /* default: beside the game exe */
        GetModuleFileNameA(NULL, path, sizeof(path));
        char *slash = strrchr(path, '\\');
        if (slash) strcpy(slash + 1, "magos_spike.log");
        else       snprintf(path, sizeof(path), "magos_spike.log");
    }
    g_log = fopen(path, "a");
    magos_log("[magos] log -> %s\n", path);
}

/* ---- step 4/5: lua_newstate detour — capture L, call lua_gettop ---- */
static lua_State *detour_newstate(lua_Alloc f, void *ud) {
    lua_State *L = g_orig_newstate(f, ud);
    if (L) {
        magos_log("[magos] step4: lua_newstate hook fired; L = %p\n", (void *)L);
        /* step 5: one LuaJIT C-API call against the documented offsets.
         * lua_gettop(L) = (L->top - L->base) >> 3; for a fresh state top==base
         * so it must return 0, confirming the LJ_64 non-GC64 struct layout. */
        if (g_lua_gettop) {
            int top = g_lua_gettop(L);
            magos_log("[magos] step5: lua_gettop(L) = %d (expect 0 for fresh state)\n", top);
        }
    } else {
        magos_log("[magos] step4: lua_newstate returned NULL\n");
    }
    return L;
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

    /* step 5 prep: resolve lua_gettop to a callable function pointer. */
    g_lua_gettop = (gettop_t)(g_module_base + tbl.lua_gettop);

    /* step 4: install the lua_newstate MinHook (on the thunk callers invoke). */
    void *target = g_module_base + tbl.lua_newstate_thunk;
    MH_STATUS mh = MH_Initialize();
    if (mh != MH_OK) { magos_log("[magos] MH_Initialize failed: %d\n", mh); return 1; }
    mh = MH_CreateHook(target, (LPVOID)&detour_newstate, (LPVOID *)&g_orig_newstate);
    if (mh != MH_OK) { magos_log("[magos] MH_CreateHook(newstate) failed: %d\n", mh); return 1; }
    mh = MH_EnableHook(target);
    if (mh != MH_OK) { magos_log("[magos] MH_EnableHook(newstate) failed: %d\n", mh); return 1; }
    magos_log("[magos] step4: lua_newstate hook installed at %p (detour %p)\n",
         target, (void *)&detour_newstate);

    magos_log("[magos] worker complete; waiting for the engine to call lua_newstate...\n");
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
