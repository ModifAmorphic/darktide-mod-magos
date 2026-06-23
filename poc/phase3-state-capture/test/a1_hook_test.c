/*
 * test/a1_hook_test.c — Phase 3 Tier A1: MinHook hook-mechanism end-to-end.
 *
 * This is the MEANINGFUL Tier A test. It proves, before we risk the live
 * game, that:
 *
 *   (a) MinHook initializes and hooks cleanly under mingw + Wine on x64.
 *   (b) The detour fires when the target is invoked.
 *   (c) The detour captures the original's return value (the lua_State*
 *       analog) into a process-global.
 *   (d) The detour passes the captured value through to the caller
 *       transparently — the caller sees what the original returned.
 *
 * The target's RVA `0xc7c000` in the live game is a CFG thunk: a 5-byte
 * `jmp rel32` followed by `int3` (0xcc) padding. That is a hard shape for
 * some hooking libs (the entire function is a single jmp; there is no
 * prologue to relocate). So we test TWO target shapes:
 *
 *   1. fake_newstate_plain     — normal C function (sub rsp / ... / ret)
 *   2. fake_newstate_thunk     — CFG-thunk shape: `jmp impl; int3 padding`
 *                                (defined in fake_thunk.S)
 *
 * Both must hook cleanly, fire, capture, and pass through. If a hooking
 * library works on both, it works on `0xc7c000`.
 *
 * The fake returns the sentinel 0xDEADBEEFCAFEBABE — an unlikely pointer
 * that proves the value passed through unchanged end-to-end.
 *
 * Exit codes:
 *   0  all checks passed
 *   1  setup failed (MinHook init / CreateHook / EnableHook)
 *   2  hook fired but capture / pass-through mismatched
 *   3  hook did not fire at all (the patch didn't take)
 */
#include <windows.h>
#include <MinHook.h>
#include <stdint.h>
#include <stdio.h>

/* ---- lua_newstate-shaped fake targets ------------------------------- */
/* lua_newstate signature: void* (*)(lua_Alloc f, void* ud). The args are
 * passed in rcx (f) and rdx (ud) on x64. The detour matches this shape. */
typedef void *(*lua_Alloc)(void *ud, void *ptr, size_t osize, size_t nsize);
typedef void *(*newstate_t)(lua_Alloc f, void *ud);

#define SENTINEL ((void *)0xDEADBEEFCAFEBABEULL)

/* Plain C target — normal prologue. */
newstate_t fake_newstate_plain = NULL;          /* fwd decls filled below */
newstate_t fake_newstate_thunk = NULL;

/* The plain target body (defined here, addressable as a function pointer). */
static void *fake_newstate_plain_impl(lua_Alloc f, void *ud) {
    (void)f; (void)ud;
    return SENTINEL;
}

/* The thunk target is defined in fake_thunk.S (CFG-thunk shape:
 * jmp impl; int3 padding). It returns SENTINEL via the impl routine. */
extern void *fake_newstate_thunk_impl_entry(void);
/* ^ Symbol declared in fake_thunk.S as fake_newstate_thunk. Renamed here
 *   to make the extern obvious; the linker resolves the same symbol. */

/* ---- detour (shared by both targets) -------------------------------- */
static newstate_t g_orig_plain = NULL;
static newstate_t g_orig_thunk = NULL;

static volatile LONG g_plain_fired = 0;
static volatile LONG g_thunk_fired = 0;
static void * volatile g_plain_captured = NULL;
static void * volatile g_thunk_captured = NULL;

static void *detour_plain(lua_Alloc f, void *ud) {
    InterlockedExchange(&g_plain_fired, 1);
    void *L = g_orig_plain(f, ud);
    InterlockedExchangePointer(&g_plain_captured, L);
    return L;
}
static void *detour_thunk(lua_Alloc f, void *ud) {
    InterlockedExchange(&g_thunk_fired, 1);
    void *L = g_orig_thunk(f, ud);
    InterlockedExchangePointer(&g_thunk_captured, L);
    return L;
}

/* ---- test runner ---------------------------------------------------- */
static int failures = 0;
static int checks   = 0;
#define CHECK(cond, ...) do { \
    ++checks; \
    if (!(cond)) { ++failures; printf("  FAIL: "); printf(__VA_ARGS__); printf("\n"); } \
    else        {             printf("  ok:   "); printf(__VA_ARGS__); printf("\n"); } \
} while (0)

int main(void) {
    printf("[A1] MinHook hook-mechanism test\n");
    printf("[A1] building target pointers...\n");

    /* Resolve the plain target's address (it's a static function — take
     * its address directly). */
    fake_newstate_plain = &fake_newstate_plain_impl;

    /* Resolve the thunk target. The .S-defined symbol is itself the
     * thunk entry; assign it directly. */
    fake_newstate_thunk = (newstate_t)(void *)&fake_newstate_thunk_impl_entry;

    printf("[A1] plain target at %p\n", (void *)fake_newstate_plain);
    printf("[A1] thunk target at %p\n", (void *)fake_newstate_thunk);

    /* Sanity: both targets must return SENTINEL unhooked. This rules out
     * build/asm mistakes that would falsely pass the hooked assertions. */
    void *p_unhooked = fake_newstate_plain(NULL, NULL);
    void *t_unhooked = fake_newstate_thunk(NULL, NULL);
    CHECK(p_unhooked == SENTINEL, "plain unhooked returns SENTINEL (%p)", p_unhooked);
    CHECK(t_unhooked == SENTINEL, "thunk unhooked returns SENTINEL (%p)", t_unhooked);
    if (p_unhooked != SENTINEL || t_unhooked != SENTINEL) {
        printf("[A1] FAIL: target sanity check failed; aborting before hook\n");
        return 3;
    }

    /* Initialize MinHook and install both hooks. */
    printf("[A1] initializing MinHook...\n");
    MH_STATUS s = MH_Initialize();
    CHECK(s == MH_OK, "MH_Initialize -> %s (%d)", MH_StatusToString(s), (int)s);
    if (s != MH_OK) return 1;

    s = MH_CreateHook((LPVOID)fake_newstate_plain,
                      (LPVOID)&detour_plain,
                      (LPVOID *)&g_orig_plain);
    CHECK(s == MH_OK, "MH_CreateHook(plain) -> %s", MH_StatusToString(s));

    s = MH_CreateHook((LPVOID)fake_newstate_thunk,
                      (LPVOID)&detour_thunk,
                      (LPVOID *)&g_orig_thunk);
    CHECK(s == MH_OK, "MH_CreateHook(thunk) -> %s", MH_StatusToString(s));

    if (s != MH_OK) { MH_Uninitialize(); return 1; }

    /* Enable both. MH_EnableHook(MH_ALL_HOOKS) would batch, but enabling
     * them one-by-one more closely mirrors what the DLL does. */
    s = MH_EnableHook((LPVOID)fake_newstate_plain);
    CHECK(s == MH_OK, "MH_EnableHook(plain) -> %s", MH_StatusToString(s));
    s = MH_EnableHook((LPVOID)fake_newstate_thunk);
    CHECK(s == MH_OK, "MH_EnableHook(thunk) -> %s", MH_StatusToString(s));
    if (s != MH_OK) { MH_Uninitialize(); return 1; }

    /* Invoke both targets. The detours must fire, capture SENTINEL, and
     * return it to us unchanged. */
    printf("[A1] invoking hooked targets...\n");
    void *p_hooked = fake_newstate_plain(NULL, NULL);
    void *t_hooked = fake_newstate_thunk(NULL, NULL);

    /* The meaningful assertions. */
    CHECK(g_plain_fired == 1, "plain detour fired");
    CHECK(g_thunk_fired == 1, "thunk detour fired (CFG-thunk shape, the hard case)");
    CHECK(g_plain_captured == SENTINEL,
          "plain captured return value == SENTINEL (got %p)", g_plain_captured);
    CHECK(g_thunk_captured == SENTINEL,
          "thunk captured return value == SENTINEL (got %p)", g_thunk_captured);
    CHECK(p_hooked == SENTINEL,
          "plain caller sees SENTINEL (got %p)", p_hooked);
    CHECK(t_hooked == SENTINEL,
          "thunk caller sees SENTINEL (got %p)", t_hooked);

    /* The trampoline (g_orig_*) must point at executable memory that is
     * NOT the original target (MinHook allocates a new trampoline region). */
    CHECK((LPVOID)g_orig_plain != (LPVOID)fake_newstate_plain,
          "plain trampoline != target (target=%p tramp=%p)",
          (void *)fake_newstate_plain, (void *)g_orig_plain);
    CHECK((LPVOID)g_orig_thunk != (LPVOID)fake_newstate_thunk,
          "thunk trampoline != target (target=%p tramp=%p)",
          (void *)fake_newstate_thunk, (void *)g_orig_thunk);

    /* Clean teardown (mirrors what the DLL would do at DLL_PROCESS_DETACH). */
    MH_DisableHook((LPVOID)fake_newstate_plain);
    MH_DisableHook((LPVOID)fake_newstate_thunk);
    MH_Uninitialize();

    printf("\n[A1] =========================================\n");
    printf("[A1]  checks:   %d\n", checks);
    printf("[A1]  failures: %d\n", failures);
    if (failures == 0)
        printf("[A1]  RESULT: PASS \xe2\x80\x94 hook+capture+passthrough proven for "
               "both plain and CFG-thunk shapes.\n");
    else
        printf("[A1]  RESULT: FAIL \xe2\x80\x94 see failures above.\n");
    printf("[A1] =========================================\n");
    return failures == 0 ? 0 : 2;
}
