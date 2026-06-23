/*
 * phase3_hooks.c — Phase 3 hooking implementation.
 *
 * See phase3_hooks.h for the high-level contract. This file holds:
 *
 *   - Globals: captured lua_State*, original function pointers (filled by
 *     MinHook), pcall-winner identification state.
 *   - phase3_install(): resolve addresses from the host module base,
 *     initialize MinHook, create + enable all 4 hooks (1 newstate + 3
 *     pcall candidates).
 *   - Detour functions: detour_lua_newstate (captures L, verifies with
 *     lua_gettop), detour_pcall_0/1/2 (observe L vs captured, identify
 *     the winner).
 *
 * Threading model:
 *   - The lua_newstate detour runs ONCE, on the engine's main thread
 *     (the engine creates the VM early in main()). It writes g_captured_L
 *     via InterlockedExchangePointer (atomic on x64).
 *   - The 3 pcall-candidate detours fire on engine threads, frequently.
 *     Each reads g_captured_L atomically; until lua_newstate has fired,
 *     captured_L is NULL and the detours do nothing (defensive guard).
 *   - Per-candidate observation is one-shot via InterlockedCompareExchange
 *     on a per-candidate flag. The first observation with L == captured_L
 *     declares that candidate the winner.
 *   - Winner identification is serialized by g_pcall_cs; the first
 *     matching candidate wins. The other two are queued for unhooking via
 *     MH_DisableHook (MinHook handles concurrent in-flight trampoline
 *     calls; if disable fails, the one-shot flag prevents further work
 *     anyway, so the cost is a small extra check per call, not a bug).
 *
 * Loader-lock safety:
 *   - MinHook's init + hook creation touch only VirtualProtect,
 *     FlushInstructionCache, and HeapAlloc/VirtualAlloc for the
 *     trampoline buffer. None of these acquire the loader lock.
 *   - The detour functions run later (outside DllMain), when the engine
 *     invokes the hooked functions. They call poc_log_linef (which opens
 *     the log file via CreateFileW — safe under loader lock too, but we
 *     are not under it by then) and call the original lua_gettop (a leaf
 *     function with no loader interaction).
 */
#include "phase3_hooks.h"
#include "poc_log.h"

#include <MinHook.h>
#include <stdint.h>

/* ---- Confirmed RVAs (Phase 0 offline + Phase 2b runtime-verified). ---
 * Pinned to the analyzed Darktide.exe build
 * (SHA-256 132eed5f...791661). A game update will shift every address;
 * Phase 2b's runtime discovery (running in parallel) re-derives them
 * against the new binary, and a MISMATCH in the discovery log is the
 * signal that these constants need to be updated. */
#define RVA_LUA_NEWSTATE_THUNK   0xc7c000u
#define RVA_LUA_GETTOP           0xc74050u
#if PHASE3_INCLUDE_PCALL_OBSERVERS
#  define RVA_CAND_0               0xc748d0u
#  define RVA_CAND_1               0xc74f30u
#  define RVA_CAND_2               0xc754d0u
#endif

/* ---- Function pointer types ----------------------------------------- */
/* lua_newstate: void* (*)(lua_Alloc f, void* ud). lua_Alloc is itself a
 * function pointer; for hooking purposes we only need the calling-
 * convention shape (rcx=f, rdx=ud, return rax=L). */
typedef void *(*lua_Alloc)(void *ud, void *ptr, size_t osize, size_t nsize);
typedef void *(*lua_newstate_t)(lua_Alloc f, void *ud);

/* lua_gettop: int (*)(lua_State* L). A leaf function, no .pdata. */
typedef int (*lua_gettop_t)(void *L);

/* lua_pcall: int (*)(lua_State* L, int nargs, int nresults, int errfunc).
 * We use a 4-arg detour shape (L, int, int, int) — SAFE for any candidate
 * with <= 4 args (disassembly of all 3 candidates in tool/disasm_candidates.c
 * confirmed: 0xc748d0=3 args, 0xc74f30=4 args, 0xc754d0=4 args; none read
 * stack args beyond r9 in their first 24 instructions). */
typedef int (*lua_pcall_t)(void *L, int a, int b, int c);

/* ---- Globals -------------------------------------------------------- */

/* The captured lua_State*. Set once by detour_lua_newstate, read by the
 * pcall-candidate detours AND by Phase 4's injection logic. Aligned 64-bit
 * atomic on x64. */
static volatile LPVOID g_captured_L = NULL;

/* The GetTickCount64 timestamp at which g_captured_L was captured. Read by
 * Phase 4's injection logic to wait until globals are registered before
 * attempting loadbuffer+pcall. Set atomically alongside g_captured_L.
 * Written once, read many — aligned 64-bit on x64 is atomic. */
static volatile ULONGLONG g_captured_tick = 0;

/* Original function pointers (filled by MinHook when each hook is created). */
static lua_newstate_t g_orig_lua_newstate = NULL;
static lua_gettop_t   g_lua_gettop        = NULL;  /* direct call, not hooked */

/* Guard for the one-shot lua_gettop verification call inside
 * detour_lua_newstate. Lives outside the observer block because the
 * newstate detour is always compiled. */
static volatile LONG   g_gettop_called = 0;

#if PHASE3_INCLUDE_PCALL_OBSERVERS
/* 3 pcall candidates. Index 0..2. */
static lua_pcall_t    g_orig_pcall[3]     = { NULL, NULL, NULL };
static LPVOID         g_pcall_target[3]   = { NULL, NULL, NULL };  /* for MH_DisableHook */
static const char    *g_pcall_name[3]     = { "0xc748d0", "0xc74f30", "0xc754d0" };
static const uint32_t g_pcall_rva[3]      = { RVA_CAND_0, RVA_CAND_1, RVA_CAND_2 };

/* One-shot per candidate: 0 = not yet observed with matching L, 1 = done. */
static volatile LONG  g_pcall_done[3]     = { 0, 0, 0 };

/* Winner identification: the first candidate to fire with matching L
 * becomes the winner. Subsequent candidates observe but do not overwrite. */
static CRITICAL_SECTION g_pcall_cs;
static int g_pcall_cs_inited = 0;
static volatile LPVOID g_pcall_winner = NULL;     /* the winning target addr */
#endif  /* PHASE3_INCLUDE_PCALL_OBSERVERS */

/* ---- Capture access (Phase 4) ----------------------------------------*/
/* Reads of g_captured_L use InterlockedCompareExchangePointer(NULL, NULL)
 * for the memory barrier (so the caller sees the write that happened in
 * detour_lua_newstate before the matching InterlockedExchangePointer). */
void *phase3_get_captured_state(void) {
    return (void *)InterlockedCompareExchangePointer(&g_captured_L, NULL, NULL);
}

unsigned long long phase3_get_capture_tick(void) {
    return (unsigned long long)g_captured_tick;
}

/* ---- Helpers -------------------------------------------------------- */

/* Read SizeOfImage from the in-memory PE optional header. */
static uint32_t read_size_of_image(uintptr_t base) {
    const uint8_t *p = (const uint8_t *)base;
    uint32_t e_lfanew = *(const uint32_t *)(p + 0x3C);
    if (e_lfanew == 0 || e_lfanew > (1u << 20)) return 0;
    if (memcmp(p + e_lfanew, "PE\0\0", 4) != 0) return 0;
    /* SizeOfImage is at optional_header + 0x38 = e_lfanew + 0x50. */
    return *(const uint32_t *)(p + e_lfanew + 0x50u);
}

/* ---- lua_newstate detour -------------------------------------------- */
/* Fires once when the engine creates the VM. Captures the returned L,
 * records the capture timestamp (used by Phase 4's time-delayed injection),
 * verifies L via lua_gettop (expect 0 on a fresh state), and returns
 * it to the engine. */
static void *detour_lua_newstate(lua_Alloc f, void *ud) {
    void *L = g_orig_lua_newstate ? g_orig_lua_newstate(f, ud) : NULL;

    if (L == NULL) {
        poc_log_linef("hook lua_newstate returned NULL (engine may retry)");
        return NULL;
    }

    /* Atomic store (aligned 64-bit on x64 is atomic anyway; this also
     * serves as a memory barrier so subsequent reads see the write). */
    InterlockedExchangePointer(&g_captured_L, L);

    /* Record the capture moment. Phase 4 waits PHASE4_INJECT_DELAY_MS
     * after this before attempting loadbuffer+pcall, so the engine has
     * time to register its globals (Managers, CLASS, require, print). */
    g_captured_tick = GetTickCount64();
    poc_log_linef("captured lua_State* = 0x%p", L);

    /* Verify with lua_gettop(L): expect 0 (empty stack on a fresh state).
     * Call ONCE — guarded against re-entry (some engines create multiple
     * VMs; we only verify the first). The state is fully initialized
     * the instant lua_newstate returns, so calling lua_gettop here is
     * safe (per the brief's risks/gotchas section). */
    if (g_lua_gettop && InterlockedCompareExchange(&g_gettop_called, 1, 0) == 0) {
        int top = g_lua_gettop(L);
        poc_log_linef("lua_gettop(L) = %d (expected 0 on fresh state)", top);
    }

    return L;
}

/* ---- pcall-candidate observation ----------------------------------- */
#if PHASE3_INCLUDE_PCALL_OBSERVERS
static void observe_pcall_candidate(int idx, void *L, int a, int b, int c) {
    /* Defensive: don't fire before lua_newstate captured L. (The candidates
     * operate on a state, so lua_newstate must have run first; this guard
     * is paranoia against an engine that creates extra transient states.) */
    LPVOID captured = (LPVOID)g_captured_L;
    if (captured == NULL) return;

    /* Only the call whose L matches the captured state is interesting.
     * Other L values would flood the log; ignore them silently. */
    if (L != captured) return;

    /* One-shot per candidate: log the first matching call, then mark done. */
    if (InterlockedCompareExchange(&g_pcall_done[idx], 1, 0) != 0) return;

    poc_log_linef("lua_pcall candidate %s fired L=0x%p (matches captured state) "
                  "args=(%d, %d, %d)", g_pcall_name[idx], L, a, b, c);

    /* Identify the winner under a critical section so only one candidate
     * wins even if two fire concurrently on different threads. */
    if (!g_pcall_cs_inited) return;
    EnterCriticalSection(&g_pcall_cs);
    if (g_pcall_winner == NULL) {
        g_pcall_winner = g_pcall_target[idx];
        poc_log_linef("lua_pcall identified at %s (winner)", g_pcall_name[idx]);

        /* Disable the OTHER two candidates to avoid noise / perf cost.
         * The winner's own one-shot flag already prevents re-logging;
         * disabling the others entirely means their detours stop firing.
         * MinHook's MH_DisableHook is safe to call from within a detour
         * (it queues the disable if a trampoline is in flight). */
        for (int j = 0; j < 3; ++j) {
            if (j == idx) continue;
            if (g_pcall_target[j] == NULL) continue;
            MH_STATUS s = MH_DisableHook(g_pcall_target[j]);
            poc_log_linef("lua_pcall candidate %s pruned (other winner) "
                          "DisableHook=%d", g_pcall_name[j], (int)s);
        }
    } else {
        /* Lost the race. Mark ourselves as not-the-winner and disable. */
        if (g_pcall_target[idx] != NULL) {
            MH_STATUS s = MH_DisableHook(g_pcall_target[idx]);
            poc_log_linef("lua_pcall candidate %s observed-but-lost-race "
                          "DisableHook=%d", g_pcall_name[idx], (int)s);
        }
    }
    LeaveCriticalSection(&g_pcall_cs);
}

/* Three near-identical detours. Each calls observe_pcall_candidate with
 * its index, then tail-calls its own trampoline. (MinHook requires a
 * distinct detour function per hook; they share observation logic.) */
static int detour_pcall_0(void *L, int a, int b, int c) {
    observe_pcall_candidate(0, L, a, b, c);
    return g_orig_pcall[0] ? g_orig_pcall[0](L, a, b, c) : 0;
}
static int detour_pcall_1(void *L, int a, int b, int c) {
    observe_pcall_candidate(1, L, a, b, c);
    return g_orig_pcall[1] ? g_orig_pcall[1](L, a, b, c) : 0;
}
static int detour_pcall_2(void *L, int a, int b, int c) {
    observe_pcall_candidate(2, L, a, b, c);
    return g_orig_pcall[2] ? g_orig_pcall[2](L, a, b, c) : 0;
}
#endif  /* PHASE3_INCLUDE_PCALL_OBSERVERS */

/* ---- Install -------------------------------------------------------- */

int phase3_install(HMODULE main_module) {
#if PHASE3_INCLUDE_PCALL_OBSERVERS
    if (g_pcall_cs_inited == 0) {
        InitializeCriticalSection(&g_pcall_cs);
        g_pcall_cs_inited = 1;
    }
#endif

    if (!main_module) {
        poc_log_linef("hook ABORT main_module is NULL");
        return 1;
    }
    uintptr_t base = (uintptr_t)main_module;
    uint32_t size = read_size_of_image(base);
    poc_log_linef("hook install base=0x%llx size_of_image=0x%x",
                  (unsigned long long)base, size);
    if (size == 0) {
        poc_log_linef("hook ABORT could not read SizeOfImage at base=0x%llx",
                      (unsigned long long)base);
        return 2;
    }

    /* Resolve the absolute addresses. */
    LPVOID p_newstate     = (LPVOID)(base + RVA_LUA_NEWSTATE_THUNK);
    LPVOID p_gettop       = (LPVOID)(base + RVA_LUA_GETTOP);
    g_lua_gettop          = (lua_gettop_t)p_gettop;
#if PHASE3_INCLUDE_PCALL_OBSERVERS
    LPVOID p_cand[3]      = {
        (LPVOID)(base + RVA_CAND_0),
        (LPVOID)(base + RVA_CAND_1),
        (LPVOID)(base + RVA_CAND_2),
    };
    g_pcall_target[0]     = p_cand[0];
    g_pcall_target[1]     = p_cand[1];
    g_pcall_target[2]     = p_cand[2];

    poc_log_linef("hook targets: lua_newstate=0x%llx lua_gettop=0x%llx "
                  "cands=[0x%llx, 0x%llx, 0x%llx]",
                  (unsigned long long)(uintptr_t)p_newstate,
                  (unsigned long long)(uintptr_t)p_gettop,
                  (unsigned long long)(uintptr_t)p_cand[0],
                  (unsigned long long)(uintptr_t)p_cand[1],
                  (unsigned long long)(uintptr_t)p_cand[2]);
#else
    poc_log_linef("hook targets: lua_newstate=0x%llx lua_gettop=0x%llx "
                  "(pcall observers compiled out)",
                  (unsigned long long)(uintptr_t)p_newstate,
                  (unsigned long long)(uintptr_t)p_gettop);
#endif

    /* Validate target addresses are inside the host module (defensive —
     * if the game updated and the binary moved, the RVAs might be stale
     * and point into unmapped memory. We do NOT VirtualQuery here since
     * that's still safe but adds noise; the discovery worker's MISMATCH
     * log is the authoritative "binary moved" signal.) */
#if PHASE3_INCLUDE_PCALL_OBSERVERS
    for (int i = 0; i < 3; ++i) {
        if ((uintptr_t)p_cand[i] - base >= size) {
            poc_log_linef("hook ABORT candidate %d (rva=0x%x) outside module",
                          i, g_pcall_rva[i]);
            return 3;
        }
    }
#endif

    /* Initialize MinHook. MH_Initialize touches only the small block heap
     * and a global lock — no loader interaction. Safe under loader lock. */
    MH_STATUS s = MH_Initialize();
    if (s != MH_OK && s != MH_ERROR_ALREADY_INITIALIZED) {
        poc_log_linef("hook ABORT MH_Initialize failed: %s (%d)",
                      MH_StatusToString(s), (int)s);
        return 4;
    }
    poc_log_linef("hook MH_Initialize ok");

    /* Create the lua_newstate hook. The thunk at 0xc7c000 is `E9 rel32`
     * + `cc` padding — the hard shape for inline hooking. A1 proves
     * MinHook handles it. If creation fails, fall back to the body at
     * 0xc7eea0 (full prologue, easy to hook). */
    s = MH_CreateHook(p_newstate, (LPVOID)&detour_lua_newstate,
                      (LPVOID *)&g_orig_lua_newstate);
    if (s != MH_OK) {
        poc_log_linef("hook lua_newstate CreateHook at thunk 0x%x failed: %s (%d) "
                      "(falling back to body 0xc7eea0)",
                      RVA_LUA_NEWSTATE_THUNK, MH_StatusToString(s), (int)s);
        /* Fallback: hook the body instead. */
        LPVOID p_body = (LPVOID)(base + 0xc7eea0u);
        s = MH_CreateHook(p_body, (LPVOID)&detour_lua_newstate,
                          (LPVOID *)&g_orig_lua_newstate);
        if (s != MH_OK) {
            poc_log_linef("hook lua_newstate CreateHook at body 0xc7eea0 ALSO failed: %s (%d)",
                          MH_StatusToString(s), (int)s);
            return 5;
        }
        p_newstate = p_body;
        poc_log_linef("hook lua_newstate fallback to body OK");
    }

#if PHASE3_INCLUDE_PCALL_OBSERVERS
    /* Create the 3 pcall-candidate hooks. */
    LPVOID detours[3] = {
        (LPVOID)&detour_pcall_0,
        (LPVOID)&detour_pcall_1,
        (LPVOID)&detour_pcall_2,
    };
    for (int i = 0; i < 3; ++i) {
        s = MH_CreateHook(p_cand[i], detours[i], (LPVOID *)&g_orig_pcall[i]);
        if (s != MH_OK) {
            poc_log_linef("hook lua_pcall candidate %s CreateHook FAILED: %s (%d)",
                          g_pcall_name[i], MH_StatusToString(s), (int)s);
            /* Continue with the remaining candidates; a single failure
             * is recoverable (we still have the others). */
            continue;
        }
    }
#endif

    /* Enable all hooks in one batched call. MH_EnableHook(MH_ALL_HOOKS)
     * patches every created hook's prologue atomically (per-hook). */
    s = MH_EnableHook(MH_ALL_HOOKS);
    if (s != MH_OK) {
        poc_log_linef("hook ABORT MH_EnableHook(MH_ALL_HOOKS) failed: %s (%d)",
                      MH_StatusToString(s), (int)s);
        return 6;
    }

    /* Final, authoritative log line — the one the runbook tells the user
     * to grep for. */
    poc_log_linef("hook lua_newstate installed at 0x%llx (rva=0x%x)",
                  (unsigned long long)(uintptr_t)p_newstate,
                  RVA_LUA_NEWSTATE_THUNK);
#if PHASE3_INCLUDE_PCALL_OBSERVERS
    poc_log_linef("hook lua_pcall candidates installed: "
                  "0xc748d0 0xc74f30 0xc754d0 (observing)");
#else
    poc_log_linef("hook lua_pcall observers compiled out "
                  "(PHASE3_INCLUDE_PCALL_OBSERVERS=0)");
#endif
    return 0;
}
