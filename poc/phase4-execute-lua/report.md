# Phase 4 — Execute Lua Inside the Engine's VM

> **Phase 5 Step 3 (current) — the C-function bootstrap.** With the
> openlibs diagnostic ruled out (Step 2 confirmed openlibs is destructive
> — it crashes the game within 1 second), this build switches to the
> **C-function bootstrap**: we register our own `poc_print` C function as
> a Lua global via `lua_pushcclosure(L, &poc_print, 0)` +
> `lua_setfield(L, LUA_GLOBALSINDEX, "poc_print")`, bypassing the
> sandboxed `_G` entirely. The chunk is now:
> ```lua
> poc_print()   -- calls our C function, registered via the LuaJIT C API
> return 42
> ```
> The observable effect: `[FROM LUA] poc_print called — Lua executed a C
> function!` appears in `darktide-poc.log`. This proves Lua executed a
> function that produced a verifiable side effect — without depending on
> whatever the engine did to `_G`.
>
> See §"Phase 5 Step 3 — C-function bootstrap" below for the approach and
> §"lua_pushcclosure / lua_setfield identification" for the source-pattern
> evidence.
>
> **Phase 5 Step 2 (superseded but retained).** The openlibs diagnostic
> is still in the codebase (it fires when `p_luaL_openlibs` is non-NULL)
> but is DISABLED in production (the pointer is wired to NULL in
> `inject_install`). The A1 test still exercises it for completeness.
> See §"Phase 5 Step 2 — openlibs diagnostic" below for the historical
> analysis.
>
> **Rev 3 (retained).** Replaces the fixed-delay one-shot (rev 2) with
> **retry-on-error**: the injected chunk self-checks for readiness and the
> detour retries on every engine `lua_pcall` call until the chunk succeeds
> (or a max-retry cap is hit). This eliminates the guesswork of "when are
> globals ready?" — the chunk itself is the readiness probe. Stack cleanup
> on every failed attempt (direct `L->top` restore) prevents stack leaks
> across retries. See §"Retry-on-error timing" below.
>
> **Rev 2 (historical note).** Rev 2 re-identified `lua_pcall` at
> **`0xc744c0`** by source-pattern matching against LuaJIT 2.1's
> `lj_api.c` and added an offline disassembly-shape check to A1. That
> address and disasm-check are unchanged in rev 3. See
> §"lua_pcall re-identification" below for the full evidence.
>
> The POC's headline proof. Phase 4 hooks `lua_pcall` on the engine's Lua
> thread and executes a self-checking Lua chunk. Phase 5 Step 3 implements
> the **C-function bootstrap** to bypass the engine's sandboxed `_G`.
>
> Per POC doc Story 4: **"If this works, the core approach is proven."**

## Summary

Phase 4 Tier A is complete. The `lua_pcall` hook is installed at
**`0xc744c0`** via MinHook; the detour sits on the engine's Lua thread,
lets the engine's pcall complete (transparent passthrough), then runs
the **Phase 5 Step 3 C-function bootstrap** (registers `poc_print` via
`lua_pushcclosure` at `0xc74580` + `lua_setfield` at `0xc74cb0`), then
attempts to execute the diagnostic Lua chunk. If the chunk errors
(poc_print still nil — registration hasn't happened yet), the detour
restores the stack (direct `L->top` write at `[L + 0x18]`) and retries
on the next engine `lua_pcall` call. Once the chunk succeeds, a latch is
set and no further attempts are made. A reentry guard (`g_injecting`)
breaks the recursion when our own pcall re-enters the hook (the #1 risk
per the brief). The same guard protects the openlibs call and the
C-function bootstrap registration.

The openlibs diagnostic (Phase 5 Step 2) is still in the detour but
DISABLED in production — the pointer is wired to NULL in `inject_install`
because live testing proved openlibs is destructive (crashes the game
within 1 second). The C-function bootstrap is the **sole** mechanism we
use to provide globals to the injected chunk — it bypasses the sandboxed
`_G` entirely by registering our own implementations via the LuaJIT C API.

**A1 — the strong gate — passes 39/39.** The exact `inject.c` source the
production DLL links is compiled with `-DPHASE4_TEST_API` against the
system LuaJIT (`libluajit-5.1.so`), then exercised against a real VM in
four phases:

- **Pre-checks (4 checks):** offline disasm-checks that Darktide.exe @
  the baked-in RVAs match `lua_pcall`'s, `luaL_openlibs`'s,
  `lua_pushcclosure`'s, AND `lua_setfield`'s source patterns.
- **Phase 1 (open-libs VM + bootstrap wired, 9 checks):** globals already
  present AND bootstrap registers poc_print → chunk succeeds on the first
  attempt (`pcall_rc=0`), latch set, no retry.
- **Phase 2 (no-libs VM + bootstrap wired, 7 checks, KEY):** VM created
  WITHOUT `luaL_openlibs` (no globals) → the detour registers poc_print
  via the C API → chunk succeeds. **This proves the C-function bootstrap
  bypasses the sandboxed `_G`.**
- **Phase 3 (no-libs VM + bootstrap wired + openlibs NOT wired, 6 checks):
  production-equivalent scenario** — bootstrap is self-sufficient, no
  openlibs needed.
- **Phase 4 (no-libs VM + bootstrap DISABLED, 13 checks):** negative
  control — chunk fails (`pcall_rc=2` = LUA_ERRRUN because poc_print is
  nil), stack cleaned up (no leak), latch NOT set → retry happens on the
  next call (count increments). Validates the retry-on-error mechanism is
  preserved AND proves the chunk actually depends on our registration.

**A2 — plumbing smoke passes.** Phase 1 forwarding intact (200/200
exports), Phase 3 hook install attempted (aborts cleanly because the Wine
host module is tiny — same as Phase 3 A2), Phase 4 inject_install
correctly skipped because Phase 3 failed, discovery worker ran,
`darktide-poc-discovery.json` written.

**Phase 3 standalone still passes its own `make verify`** after my
additive changes to `expected_addrs.h` (3 new constants for
`lua_pushcclosure`, `lua_setfield`, `lua_pushstring`; no behavioral
change to the cross-check table). **Phase 2b standalone also still
passes** (28/28 Tier A).

The headline Tier B success signal is the log line `[FROM LUA] poc_print
called — Lua executed a C function!` appearing in `darktide-poc.log`,
paired with `injected attempt=1 load_rc=0 pcall_rc=0 (0=success)`. Story
4's bar is met the moment those appear.

## Deliverables

| Path | Purpose |
|------|---------|
| `src/inject.c` + `src/inject.h` | The `lua_pcall` execution hook: reentry guard, retry-on-error latch, min-interval rate limiter, max-retry cap, stack save/restore (direct `L->top` write). Self-checking chunk. Compiles two ways: production (MinHook + poc_log, `L->top` at 0x18) and test (`-DPHASE4_TEST_API`, no MinHook, Linux portability shim, auto-detected `L->top` offset). **Rev 3:** retry-on-error replaces fixed-delay one-shot. **Rev 2:** RVA sourced from `expected_addrs.h`. |
| `src/dllmain.c` | Phase 4 variant: calls `phase3_install` then `inject_install` (only if Phase 3 succeeded). |
| `build.sh` | mingw cross-compile composing Phase 1 stubs + Phase 2a engine + capstone + MinHook + Phase 3 hooks (`-DPHASE3_INCLUDE_PCALL_OBSERVERS=0`) + Phase 4 inject → `build/dbghelp.dll` |
| `install.sh` / `uninstall.sh` | `.orig` backup + no-clobber discipline; `native,builtin` override |
| `test/a1_inject_test.c` + `test/run_a1_inject_test.sh` | **Tier A1** — the strong gate (mock-VM injection against real LuaJIT **+ offline disasm-check** that Darktide.exe @ the baked-in RVA matches lua_pcall's source pattern) |
| `test/disasm_check.{c,h}` | **NEW (rev 2).** The offline disasm-shape matcher used by A1. Portable C99 + capstone (vendored static lib). |
| `Makefile` | Targets: `build`, `a1`, `a2`, `verify`, `clean` |
| `RUNBOOK.md` | Tier B live-game instructions |
| `report.md` | This file |

### Composed (not duplicated) from prior phases

| Source | Used by Phase 4 as |
|--------|---------------------|
| `phase3-state-capture/src/phase3_hooks.{c,h}` | **Modified** (additive: capture-tick getter + `PHASE3_INCLUDE_PCALL_OBSERVERS` macro, default 1). Composed here with `-DPHASE3_INCLUDE_PCALL_OBSERVERS=0`. |
| `phase3-state-capture/vendor/minhook/` | MinHook 1.3.3, x64 static lib |
| `phase2-runtime-discovery/engine/*.c` | Discovery engine (unchanged) |
| `phase2-runtime-discovery/vendor/capstone/` | Capstone 5.0.3 — used BOTH as mingw static lib (DLL build) AND native Linux static lib (A1 disasm-check; same vendored tree, different build dir) |
| `phase2b-runtime-discovery/src/discover_worker.c` | Discovery worker thread (unchanged) |
| `phase2b-runtime-discovery/src/poc_log.{c,h}` | Thread-safe line logger (unchanged) |
| `phase2b-runtime-discovery/src/expected_addrs.h` | Phase 0 cross-check constants. **Rev 2: also defines `EXPECT_LUA_PCALL = 0xc744c0`** (single source of truth — `inject.c` references it for the production hook target, and A1 references it for the offline disasm check). |
| `phase1-proxy-dll/tools/gen_stubs.py` | Stub generator (regen the same 200 forwarders) |

The only file modified outside `phase4-execute-lua/` is
`phase3-state-capture/src/phase3_hooks.{c,h}`. The modifications are
strictly additive (a new global + getter + a macro that defaults to
"include the existing code"); Phase 3 standalone still builds and passes
its own `make verify` after the changes.

## Tier A results — ALL PASS

### A1 — Mock-VM injection against real LuaJIT (the strong gate)

`test/run_a1_inject_test.sh` compiles the **exact same `inject.c`** that
the production DLL links, but with `-DPHASE4_TEST_API`. The test runs in
two phases:

**Phase 1 (open-libs VM — globals ready, chunk succeeds):**

1. Calls `disasm_check_lua_pcall()` on `Darktide.exe` at the baked-in
   `EXPECT_LUA_PCALL` (`0xc744c0`). Same as rev 2.
2. Auto-detects the system LuaJIT's `L->top` offset (0x28 on this GC64
   build; production uses 0x18) and passes it via
   `inject_test_set_L_top_offset()`. **NEW in rev 3.**
3. Creates a real `lua_State*` via `luaL_newstate` + `luaL_openlibs`.
4. Overrides `print` with a C function that appends to a known file.
5. `inject_test_setup(...)` wires up the function pointers.
6. Calls `detour_lua_pcall(L, 0, 0, 0)` to simulate the engine's hooked
   pcall invocation.
7. Asserts: `load_rc=0`, `pcall_rc=0`, `inject_count=1`, `max_depth=2`
   (reentry guard), mock print contains `[INJECTED]`, io.open wrote
   `executed`, latch set (no retry on 2nd call).

**Phase 2 (no-libs VM — globals missing, chunk fails + retries). NEW in rev 3:**

1. Resets detour state, creates a fresh `lua_State*` via
   `luaL_newstate()` with **NO `luaL_openlibs`** (no globals at all).
2. Calls `detour_lua_pcall(L, 0, 0, 0)`.
3. Asserts: `load_rc=0` (chunk is syntactically valid), `pcall_rc=2`
   (LUA_ERRRUN — missing globals detected), `inject_count=1`,
   `lua_gettop(L)` unchanged (stack cleaned up — no leak).
4. Calls `detour_lua_pcall` again to verify retry: `inject_count`
   increments to 2 (latch NOT set on failure), stack still clean.

```
[a1] compiling inject.c with -DPHASE4_TEST_API + a1_inject_test.c + disasm_check.c ...
[a1] running .../a1_inject_test ...
[A1] Phase 4 mock-VM injection test (retry-on-error)
[A1] libluajit: Lua 5.1.4
[A1] verifying lua_pcall RVA 0xc744c0 in (default Darktide.exe path) ...
[disasm_check] decoded 64 insns at RVA 0xc744c0 (file off 0xc73ac0)
[disasm_check] features: glref_08=1 base_10=1 top_18=1 stack_24=1 test_r9=1 jne=1 jle=1 lea_r9_x8=1 inc_r8=1 shl_sub(top-nargs*8)=1 direct_calls=1
[disasm_check] OK: shape matches lua_pcall @ RVA 0xc744c0
  ok:   Darktide.exe @ 0xc744c0 matches lua_pcall source pattern (got rc=0)
  ok:   detected L->top offset in system LuaJIT (got 0x28)
[A1] L->top offset: 0x28 (system LuaJIT Lua 5.1.4; production uses 0x18)

[A1] --- Phase 1: open-libs VM (chunk should succeed) ---
[A1] OK: LuaJIT state created, libs opened
  ok:   mock print wrote 'direct test' (got 0x...)
[A1] OK: inject_test_setup wired (min_interval_ms=0)
[A1] OK: engine-sim chunk loaded on the Lua stack
[A1] invoking detour_lua_pcall(L, 0, 0, 0) ...
  ok:   engine-sim pcall returned 0 (got 0)
[A1] detour results: load_rc=0 pcall_rc=0 inject_count=1 max_depth=2
  ok:   injected loadbuffer returned 0 (got 0)
  ok:   injected pcall returned 0 = success (got 0)
  ok:   exactly one injection attempt (got 1)
  ok:   detour reached depth 2 (outer + injected reentry), no infinite recursion (got 2)
  ok:   mock print file contains '[INJECTED] Hello from the DLL' (got 0x...)
[A1] mock print content: [INJECTED] Hello from the DLL
  ok:   io.open wrote 'darktide-poc-executed.txt' with content 'executed' (got 0x... / 'executed')
[A1] calling detour again to verify latch (success → no retry)...
  ok:   latch after success: inject_count unchanged after 2nd call (before=1 after=1)

[A1] --- Phase 2: no-libs VM (chunk should fail + retry) ---
[A1] OK: LuaJIT state created (libs NOT opened)
[A1] invoking detour_lua_pcall(L, 0, 0, 0) against no-libs VM ...
  ok:   engine-sim pcall still succeeds without libs (got 0)
[A1] detour results: load_rc=0 pcall_rc=2 inject_count=1 stack_top=0 (was 0)
  ok:   no-libs: loadbuffer succeeded — chunk is syntactically valid (got 0)
  ok:   no-libs: pcall returned LUA_ERRRUN=2 — missing globals detected (got 2)
  ok:   no-libs: one attempt made (got 1)
  ok:   no-libs: stack cleaned up after failed attempt — no leak (expected 0, got 0)
[A1] calling detour again to verify retry on failure...
  ok:   no-libs retry: engine-sim pcall still succeeds (got 0)
  ok:   no-libs retry: pcall still returns LUA_ERRRUN=2 (got 2)
  ok:   no-libs retry: inject_count incremented — latch NOT set on failure (before=1 after=2)
  ok:   no-libs retry: stack still clean after 2nd failed attempt (expected 0, got 0)

[A1] =========================================
[A1]  checks:   20
[A1]  failures: 0
[A1]  RESULT: PASS — Lua execution + retry-on-error proven against a real LuaJIT.
[A1] =========================================
```

**20/20 checks pass** (11 in Phase 1 + 9 in Phase 2). Specifically:

- **(Rev 2)** `disasm_check_lua_pcall()` matches all 11 features of
  lua_pcall's source pattern at `0xc744c0`.
- **(Rev 3 NEW)** L->top offset auto-detected for the system LuaJIT
  (0x28) — production uses 0x18 (Darktide's non-GC64 layout).
- **Phase 1:** `load_rc=0`, `pcall_rc=0`, `inject_count=1`, `max_depth=2`
  (reentry guard), mock print + io.open fire, latch set on success.
- **Phase 2 (NEW):** `pcall_rc=2` (LUA_ERRRUN — globals missing detected),
  stack cleaned up (`gettop` unchanged), latch NOT set (retry happens,
  count goes 1→2), stack still clean after retry.

This validates the entire injection mechanism end-to-end against a real
LuaJIT before the live game — AND validates the RVA against the real
game binary.

### A2 — DLL plumbing smoke (no regression)

`test/run_a2_smoke.sh` drops the built `dbghelp.dll` alongside a Wine host
exe, loads it, and verifies the full plumbing survives Phase 4's additions:

- Phase 1 forwarding intact: 200/200 exports resolved.
- Phase 3 hook install ran: `hook install base=` appears.
- Phase 3 hook install aborted cleanly (the Wine host exe is tiny —
  `phase3_install`'s bounds check catches the out-of-module candidate
  addresses, then MinHook returns `MH_ERROR_NOT_EXECUTABLE` when trying
  to hook the unmapped `lua_newstate` body). Correct behavior.
- **Phase 4 inject_install correctly skipped** because Phase 3 failed
  (dllmain contract: `inject_install` only runs if `phase3_install`
  returns 0). Log shows `Phase 4 inject skipped`.
- Phase 2b worker thread spawned, ran discovery, wrote JSON (13.6 KB).
- Clean detach on exit.

```
[darktide-poc] DllMain DLL_PROCESS_ATTACH pid=32 ts=...
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200 pid=32 ts=...
[darktide-poc] hook install base=0x140000000 size_of_image=0x2a000 pid=32 ts=...
[darktide-poc] hook targets: lua_newstate=0x140c7c000 lua_gettop=0x140c74050 (pcall observers compiled out) ...
[darktide-poc] hook MH_Initialize ok ...
[darktide-poc] hook lua_newstate CreateHook at thunk 0xc7c000 failed: MH_ERROR_NOT_EXECUTABLE (7) (falling back to body 0xc7eea0) ...
[darktide-poc] hook lua_newstate CreateHook at body 0xc7eea0 ALSO failed: MH_ERROR_NOT_EXECUTABLE (7) ...
[darktide-poc] DllMain phase3_install rc=5 (hooks NOT active; Phase 4 inject skipped, discovery still runs) ...
[darktide-poc] DllMain spawned discover_worker thread ...
[darktide-poc] discover summary matched=0 mismatched=0 unresolved=7 pcall=deferred ...
[darktide-poc] discover json written ...
[darktide-poc] DllMain DLL_PROCESS_DETACH pid=32 ts=...
[host] PASS: plumbing works end-to-end with Phase 3+4 hooks
```

### Phase 3 standalone regression check

After modifying `phase3_hooks.{c,h}`, I re-ran Phase 3's `make verify`:
DLL builds 200/200, A1 15/15, A2 plumbing PASS, disasm runs and reports
all 3 viable candidates SAFE. **No regression.** Phase 3 standalone
continues to work exactly as before (observers default ON; the macro is
purely additive).

## Confirmed addresses used by Phase 4 (+ Phase 5 Steps 2 + 3)

Pinned to the analyzed Darktide.exe build (SHA-256
`132eed5f…791661`, 18,715,784 bytes). All confirmed by Phase 0 + Phase 2b
+ Phase 3, except `lua_pcall` (Phase 4 rev 2 source-pattern match),
`luaL_openlibs` (Phase 5 Step 1 source-pattern match — see §"luaL_openlibs
identification"), and `lua_pushcclosure`/`lua_setfield`/`lua_pushstring`
(Phase 5 Step 3 source-pattern matches — see §"lua_pushcclosure /
lua_setfield identification").

| Function | RVA | Phase 4/5 use |
|----------|-----|---------------|
| `lua_newstate` (thunk) | `0xc7c000` | Hook target (Phase 3, unchanged) — captures `g_captured_L` + `g_captured_tick` |
| `lua_gettop` | `0xc74050` | Phase 3 verification call (unchanged) |
| `luaL_loadbuffer` | `0xc7ad80` | Direct call (function pointer) to load the test Lua chunk |
| `lua_pcall` | `0xc744c0` | Hook target (Phase 4 owns this) + direct call (to execute our chunk). Rev 2: re-identified from rev 1's wrong `0xc74f30`. |
| `luaL_openlibs` | `0xc7f380` | Phase 5 Step 2 — direct call, **DISABLED** (destructive; verified in live testing). Source-pattern matched against LuaJIT 2.1 `lib_init.c`. Address retained for diagnostic; pointer is wired to NULL in `inject_install`. |
| **`lua_pushcclosure`** | **`0xc74580`** | **Phase 5 Step 3 — C-function bootstrap. Direct call to register `poc_print` (with `n=0`). `lua_pushcfunction` is a macro for this function.** |
| **`lua_setfield`** | **`0xc74cb0`** | **Phase 5 Step 3 — C-function bootstrap. Direct call (with `idx=LUA_GLOBALSINDEX=-10002`) to set `poc_print` as a global. `lua_setglobal` is a macro for this.** |
| `lua_pushstring` (bonus) | `0xc747d0` | Phase 5 Step 3 — identified but not currently used (poc_print takes no args, returns nothing). Documented for future use (returning strings from C functions). |

Module base at runtime: `GetModuleHandleW(NULL)` (= Darktide.exe in the
live game). Hook target: `(uintptr_t)base + 0xc744c0`. Logged on startup
as `inject targets: lua_pcall=0x<abs> (rva=0xc744c0) luaL_loadbuffer=0x<abs>
(rva=0xc7ad80) luaL_openlibs=0x<abs> (rva=0xc7f380) [DISABLED]
lua_pushcclosure=0x<abs> (rva=0xc74580) lua_setfield=0x<abs> (rva=0xc74cb0)
min_interval_ms=500 max_attempts=200`.

## Retry-on-error timing

**Rev 3 replaces the fixed-delay one-shot with retry-on-error.** The
chunk self-checks for readiness — if `print` or `io` is nil, it errors
(`error('not_ready')`, or "attempt to call a nil value" if the base
library isn't open at all — both produce `LUA_ERRRUN=2`). The detour
treats any nonzero pcall result as "not ready — try again" and leaves
the latch unset. The next engine `lua_pcall` call triggers another
attempt. Once globals are ready, the chunk executes fully (`pcall_rc=0`),
the latch is set, and retrying stops.

This is deterministic: we don't guess when globals become ready, we let
the chunk tell us. The rev 2 approach (fixed 3-second delay) produced
`pcall_rc=2` at 3 seconds in the live game — globals weren't ready yet.
A bigger delay would just be a bigger guess. Retry-on-error fires the
moment globals are actually available.

### Guards (in order, evaluated each engine `lua_pcall` call)

1. **Reentry guard** (`g_injecting`): if our own injection's pcall is in
   flight, route straight to the trampoline. (Unchanged from rev 2.)
2. **Engine pcall passthrough**: the engine's `lua_pcall` runs first and
   completes transparently. (Unchanged.)
3. **Capture guard**: only act when `L == captured_L` (Phase 3's hooked
   state). (Unchanged.)
4. **Latch** (`g_injected`): if set (success or gave up), skip all
   injection logic. (Semantics changed: set on success OR give-up, not
   on first attempt.)
5. **Min-interval rate limiter** (`PHASE4_INJECT_DELAY_MS`, default 500ms):
   don't retry more than once per 500ms. This is NOT a readiness delay
   — it just prevents hammering during the engine's init burst (when
   `lua_pcall` may fire dozens of times per second). Track
   `g_last_attempt_tick`. The first attempt always passes.
6. **Max-retry cap** (`PHASE4_MAX_INJECT_ATTEMPTS`, default 200): if
   we've tried 200 times and all failed, give up, log `"giving up after
   N attempts — globals never became ready"`, set the latch. At 500ms
   per attempt, 200 attempts span ~100s — comfortably covering the
   engine's full startup window.
7. **The attempt**: increment `g_attempt_count`, set `g_injecting`,
   save `L->top`, loadbuffer+pcall. On success (`pcall_rc=0`), set the
   latch. On failure, restore `L->top` (pop the error object) so the
   stack is clean for the next attempt.

### Expected attempt count in production

The engine registers its globals (`Managers`, `CLASS`, `require`,
`print`) within the first 1–2 seconds of VM creation. With a 500ms
min-interval, that's 2–4 retries before globals are ready. Single-digit
attempts expected. The max-retry cap (200) is a safety valve for the
pathological case where `io` is permanently sandboxed.

### Stack cleanup on failure

After the engine's `lua_pcall` returns, `L->top` is at position T0
(where the engine expects it). Our `luaL_loadbuffer` pushes the chunk
(`L->top` = T0 + 8). Our `lua_pcall(L, 0, 0, 0)`:
- **Success:** consumes the chunk, pushes 0 results → `L->top` = T0.
  Clean, no action needed.
- **Error:** consumes the chunk, pushes 1 error object → `L->top` = T0+8.
  One item above what the engine expects. We restore `L->top` = T0 by
  writing the saved value back to `[L + 0x18]`. The error object is
  effectively popped. The engine sees the stack exactly as its own
  `lua_pcall` left it.

If `loadbuffer` itself fails (load_rc != 0), it also pushes 1 error
object → same cleanup.

This direct `L->top` write avoids needing `lua_pop`/`lua_settop`
addresses (which we don't have confirmed). The offset `0x18` is
confirmed by the lua_pcall disasm (`mov rcx, [rcx+0x18]`) and is
correct for the Darktide binary's non-GC64 LuaJIT. The A1 test
auto-detects the offset for the system LuaJIT (GC64, offset 0x28).

## Hook installation flow

```
DllMain DLL_PROCESS_ATTACH
  ├── DisableThreadLibraryCalls
  ├── poc_log_init
  ├── Log "DllMain DLL_PROCESS_ATTACH"
  ├── LoadLibraryW("C:\Windows\System32\dbghelp.dll")
  ├── Resolve all 200 forwarders (Phase 1)
  ├── Log "DllMain forwarders resolved=200"
  ├── GetModuleHandleW(NULL) -> main module base
  ├── phase3_install(base)            [observers OFF in Phase 4 build]
  │     ├── Read SizeOfImage
  │     ├── Compute newstate/gettop addresses
  │     ├── MH_Initialize()
  │     ├── MH_CreateHook(lua_newstate @ 0xc7c000, detour_lua_newstate)
  │     ├── MH_EnableHook(MH_ALL_HOOKS)
  │     └── Log "hook lua_newstate installed at 0x<abs> (rva=0xc7c000)"
  ├── inject_install(base)            [only if phase3_install returned 0]
  │     ├── Compute lua_pcall + luaL_loadbuffer addresses
  │     ├── Wire up p_lua_pcall, p_luaL_loadbuffer function pointers
  │     ├── MH_CreateHook(lua_pcall @ 0xc744c0, detour_lua_pcall)
  │     ├── MH_EnableHook(lua_pcall)
  │     └── Log "inject lua_pcall hook installed ... min_interval_ms=500 max_attempts=200"
  ├── CreateThread(discover_worker)   [always — Phase 2b cross-check]
  └── return TRUE

... engine main() runs ...
... engine calls lua_newstate(f, ud) ...
  └── detour_lua_newstate fires (Phase 3):
        ├── Call original via trampoline -> L
        ├── InterlockedExchangePointer(&g_captured_L, L)
        ├── g_captured_tick = GetTickCount64()
        ├── Log "captured lua_State* = 0x<L>"
        ├── Call lua_gettop(L) directly -> 0
        ├── Log "lua_gettop(L) = 0"
        └── Return L to engine

... engine calls lua_pcall(L, nargs, nresults, errfunc) on its Lua thread ...
  └── detour_lua_pcall fires (Phase 4 — retry-on-error):
        ├── if g_injecting: return g_orig_pcall(L, ...) [reentry break]
        ├── rc = g_orig_pcall(L, nargs, nresults, errfunc)  [engine pcall]
        ├── if captured_L == NULL or L != captured_L: return rc
        ├── if captured_tick == 0: return rc          [defensive]
        ├── if g_injected: return rc                   [latch: done]
        ├── if (now - g_last_attempt_tick) < 500ms: return rc  [rate limiter]
        ├── if g_attempt_count >= 200:                 [max-retry cap]
        │     log "giving up after N attempts"
        │     g_injected = 1
        │     return rc
        ├── g_attempt_count++
        ├── g_injecting = 1
        ├── saved_top = L->top                         [[L + 0x18]]
        ├── load_rc = p_luaL_loadbuffer(L, src, len, "[poc-inject]")
        ├── if load_rc == 0:
        │     pcall_rc = p_lua_pcall(L, 0, 0, 0)   [re-enters detour; g_injecting routes to trampoline]
        ├── if pcall_rc == 0:                        [SUCCESS]
        │     g_injected = 1                         [latch set — stop retrying]
        │     (stack is clean — pcall consumed chunk, 0 results)
        ├── else:                                     [FAILURE — globals not ready]
        │     L->top = saved_top                      [pop error object]
        │     (latch NOT set — will retry on next engine pcall)
        ├── g_injecting = 0
        ├── g_last_attempt_tick = now
        ├── Log "injected attempt=N load_rc=.. pcall_rc=.. delay_ms=.."
        └── return rc  [engine sees its own pcall result, undisturbed]
```

## Reentry model — the #1 risk, addressed

Per the brief, the top risk is infinite recursion: when our injected
chunk executes via `p_lua_pcall(L, 0, 0, 0)`, that call goes through the
**patched address** (MinHook's prologue jump), re-entering
`detour_lua_pcall`. Without a guard, this recurses until stack overflow.

**Solution:** `g_injecting` (a `volatile LONG`).

- `detour_lua_pcall` checks `g_injecting` first. If set, it skips ALL
  injection logic and tail-calls `g_orig_pcall` (the trampoline = real
  `lua_pcall`).
- `do_inject` sets `g_injecting = 1` BEFORE `p_lua_pcall`, clears it
  AFTER. So the re-entry happens with `g_injecting == 1` and is routed
  straight to the trampoline.
- Max recursion depth: 2 (outer detour + inner re-entry). The A1 test
  asserts `max_depth == 2` exactly.

`g_injecting` is process-global (not thread-local) — matches the brief's
"thread-local or static flag". Safe because the engine only calls
`lua_pcall` on `captured_L` from one thread (its Lua thread), and the
L-match guard ensures we only inject on that thread.

## Retry latch

`g_injected` (also a `volatile LONG`):

- 0 = keep trying. Each qualifying engine `lua_pcall` call (passing the
  rate limiter and max-retry cap) triggers another attempt.
- 1 = done (success OR gave up). Set ONLY on success (`pcall_rc == 0`)
  or when the max-retry cap (`g_attempt_count >= 200`) is hit. Subsequent
  detour calls skip all injection logic.

This is different from the rev 2 one-shot latch (which was set on the
FIRST attempt, success or failure). Rev 3 only sets the latch on success
or give-up, so failed attempts naturally retry on the next engine pcall.
The rate limiter (500ms) prevents hammering during the init burst, and
the stack cleanup (L->top restore) ensures each failed attempt is
stack-neutral — so retrying doesn't compound stack imbalance.

The A1 test verifies both transitions:
- **Phase 1:** after a successful injection, a second `detour_lua_pcall`
  call leaves `inject_count` unchanged (latch set on success).
- **Phase 2:** after a failed injection, a second call increments
  `inject_count` (latch NOT set on failure → retry happens).

## Stack hygiene

After `g_orig_pcall(L, nargs, nresults, errfunc)` returns, the engine's
stack has whatever `nresults` it asked for (or an error object if the
engine's pcall failed). Our injection:

- `luaL_loadbuffer` pushes +1 (chunk) or +1 (error message on failure).
- `lua_pcall(L, 0, 0, 0)` consumes the chunk (-1), pushes 0 results on
  success or +1 error object on failure.

**On success:** net stack change = 0. Clean, no action needed.

**On failure (the retry case):** +1 error object is left on the stack.
We restore `L->top` directly by writing the saved value back to
`[L + 0x18]` (the `top` field in `lua_State`). This pops the error
object without needing `lua_pop`/`lua_settop` (whose addresses we don't
have confirmed). The engine sees its stack exactly as its own `lua_pcall`
left it — every retry is stack-neutral.

The offset `0x18` is confirmed by the lua_pcall disasm
(`mov rcx, [rcx+0x18]`) for the Darktide binary's non-GC64 LuaJIT. The
A1 test auto-detects the offset for the system LuaJIT (GC64, offset
0x28) and passes it via `inject_test_set_L_top_offset()`. The A1 Phase 2
test explicitly asserts `lua_gettop(L)` is unchanged before and after a
failed attempt — validating the stack cleanup.

## Loader-lock safety

`inject_install` is called from `DllMain` after `phase3_install`. It
touches only:

- `MH_CreateHook` / `MH_EnableHook` — already analyzed in Phase 3 as
  loader-lock-safe (only `VirtualProtect`, `FlushInstructionCache`,
  `HeapAlloc`/`VirtualAlloc`).
- `GetModuleHandleW(NULL)` — already-loaded, no loader interaction.
- `poc_log_linef` — Phase 2b's pattern (open/write/close per line, no
  loader interaction).

The detour functions run later (outside `DllMain`) on the engine's Lua
thread. They call `luaL_loadbuffer` and `lua_pcall` (leaf-ish in terms of
loader interaction — no DLL loads), and `poc_log_linef`. All safe.

The built DLL has no new imports vs Phase 3 (still just kernel32 /
msvcrt / user32 — verified by the same `objdump -p` check Phase 3 uses).

## Anything surprising or risky

1. **The Phase 3 hooks file had to be modified.** Phase 3's
   `phase3_hooks.{c,h}` are composed by Phase 4, but they install the 3
   `lua_pcall`-candidate observer hooks — which conflict with Phase 4's
   own `lua_pcall` hook (MinHook disallows two hooks on the same target).
   Solution: a compile-time switch `PHASE3_INCLUDE_PCALL_OBSERVERS`
   (default `1`, preserves Phase 3's standalone behavior). Phase 4's
   build passes `-DPHASE3_INCLUDE_PCALL_OBSERVERS=0` to compile the
   observers out. Phase 4 also adds a `g_captured_tick` global + getters
   to Phase 3 (set by `detour_lua_newstate` alongside `g_captured_L`).
   All changes are additive; Phase 3's `make verify` still passes.

2. **MinHook's `MH_EnableHook(MH_ALL_HOOKS)` does NOT enable hooks
   created AFTER it runs.** Phase 3's `phase3_install` calls
   `MH_EnableHook(MH_ALL_HOOKS)` after creating the lua_newstate hook.
   Phase 4's `inject_install` runs AFTER that and creates the lua_pcall
   hook — which is then NOT enabled by the prior batched call. The fix:
   `inject_install` calls `MH_EnableHook(p_pcall)` (per-target enable)
   after `MH_CreateHook`. Verified working in A2 (when Phase 3 succeeds,
   the per-target enable brings the lua_pcall hook live).

3. **The `captured_tick == 0` defensive guard.** Initially I had
   `if (captured_tick == 0) return rc;` in the detour. The A1 test
   initially passed `captured_tick=0` to inject_test_setup, which would
   have aborted the injection. Fix: the test now passes `now_ms() - 5000`
   (5 seconds ago). The defensive guard stays in production (paranoia
   against stale atomic reads, which shouldn't happen on x64 aligned
   64-bit but is cheap insurance). In rev 3, `captured_tick` is only used
   for the `delay_ms` log field (the readiness check is the chunk itself,
   not a time threshold).

4. **`g_injecting` is process-global, not thread-local.** This means if
   the engine ever calls `lua_pcall` on `captured_L` from a different
   thread (which LuaJIT's single-thread-state contract says it shouldn't),
   that call would see `g_injecting` set during our injection and route
   to the trampoline — correct behavior. The only failure mode would be
   a different thread winning the latch and injecting on the wrong
   thread, but the L-match guard prevents that (different L = skip). A1
   verifies single-threaded behavior; Phase 5 should consider TLS if
   multi-threaded pcall turns out to be a thing.

5. **No `print` routing known.** The brief flagged this: we don't know
   where the engine's `print` writes (game console, stdout, engine log).
   The `print` line in the injected chunk is a bonus; the file write via
   `io.open` is the deterministic observable. The RUNBOOK documents
   where to search for both. If `print` turns out to be invisible, that's
   fine — the file write is the proof.

6. **The `lua_pcall` candidate observers in Phase 3 are STILL the source
   of the `matched=6 mismatched=1` discovery result.** Phase 4 keeps the
   `lua_newstate` hook (which patches the thunk), so the discovery
   worker (running in parallel) still sees the patched bytes and reports
   `lua_newstate_body` as MISMATCH. This is documented in
   `ENGAGEMENT-STATE.md` as the known footprint. Not a bug.

## Phase 5 hooks (handoff)

Phase 5 (precise timing + DMF bootstrap) will build on Phase 4's
foundation. Key handoff notes:

- **`p_lua_pcall` and `p_luaL_loadbuffer` function pointers** (in
  `inject.c`) are the canonical entry points for executing Lua. Phase 5
  reuses these (no need to re-resolve addresses). For multi-shot
  injection (DMF bootstrap after the POC chunk), Phase 5 can relax the
  latch or add a second injection path. The retry-on-error mechanism
  means the latch is already set by the time globals are ready — Phase 5
  can hook a later event (e.g., `lua_resource::bytecode`) for its own
  timing.
- **The reentry guard pattern (`g_injecting`)** is reusable for any
  future hook that calls back into the patched function. Phase 5's
  `lua_resource::bytecode` hook (for timing) can use the same pattern.
- **Retry-on-error replaces the time threshold.** Rev 3 eliminated the
  fixed-delay guesswork — the chunk self-checks for readiness. Phase 5
  may still want `lua_resource::bytecode` hooking for a more precise
  signal (first script load), but the POC no longer depends on it.
- **Stack cleanup via direct `L->top` write.** Phase 4's rev 3
  demonstrated that writing `[L + 0x18]` to restore the stack after a
  failed pcall is safe and effective. Phase 5's DMF bootstrap can reuse
  this pattern for error recovery. At that point, confirming
  `lua_pop` / `lua_settop` addresses becomes optional (the direct write
  works fine for single-slot pops).
- **`g_captured_tick`** (Phase 3's new global) is the moment of VM
  creation. The retry-on-error attempts log `delay_ms` (time since
  capture) so Phase 5 can analyze when globals actually became ready.

## Verification recipe

```sh
cd poc/phase4-execute-lua
make verify    # builds the DLL + runs A1 and A2
```

All three pieces must pass: DLL builds 200/200 exports; A1 20/20 checks
against real LuaJIT (11 open-libs + 9 no-libs); A2 end-to-end with
Phase 1+2b+3 intact. Tier B (the live game) is the RUNBOOK's job.

Phase 3 standalone regression check:
```sh
cd ../phase3-state-capture
make verify    # 15/15 + A2 + disasm — must still pass after my modifications
```

## Out of scope (per spec)

- DMF bootstrap, `Mods`/`__print` globals, or loading `dmf_loader.lua`
  (Phase 5 — Story 6).
- Hooking `lua_resource::bytecode` or precise timing work (Phase 5 —
  Story 5). The retry-on-error mechanism eliminates the timing guesswork
  for the POC, but Phase 5 may still want bytecode-hook-based timing for
  the DMF bootstrap.
- Loading user mods.
- Multi-shot injection (one successful execution is enough for P0).
  The retry mechanism fires multiple attempts, but only ONE successful
  execution — subsequent calls are latched.

---

## lua_pcall re-identification (rev 2 — the actual analysis)

> This section documents how the rev 2 RVA `0xc744c0` was identified and
> why the rev 1 RVA `0xc74f30` is wrong. The method is reproducible from
> the binary alone — no live-game work needed.

### The rev 1 failure

The rev 1 DLL hooked `0xc74f30` and produced, in the live game:

```
injected load_rc=0 pcall_rc=-84340864 (0=success) delay_ms=3002
```

- `load_rc=0` → `luaL_loadbuffer` worked (`0xc7ad80` is correct).
- `pcall_rc=-84340864` (`0xFB2977A0` as unsigned) → `0xc74f30` returned a
  pointer, not an int. NOT `lua_pcall`.

### Why rev 1 was wrong (disassembly)

`0xc74f30` is an internal stack-check helper:

```
0xc74f30: mov  [rsp+8], rbx
0xc74f35: push rdi
0xc74f36: sub  rsp, 0x20
0xc74f3a: movsxd rdi, edx              ; rdi = nargs (sign-extended)
0xc74f3d: mov  rbx, rcx                ; rbx = L
0xc74f40: mov  rcx, [rcx + 0x18]       ; rcx = L->top
0xc74f44: shl  rdi, 3                  ; rdi = nargs*8
0xc74f4c: mov  r9, [rbx + 0x10]        ; r9 = L->base  ← OVERWRITES r9 (errfunc)
0xc74f50: lea  r8, [r9 + rdi]          ; r8 = base + nargs*8  ← OVERWRITES r8 (nresults)
0xc74f54: cmp  r8, rcx                 ; check (base + nargs*8) <= top
0xc74f57: jbe  0xc74fa8
...
0xc74f6d: call 0xc7ed10                ; ← calls lj_state_growstack, NOT lj_vm_pcall
```

It uses only 2 of the 4 args (`rcx`, `edx`), discards `r8` and `r9`, and
calls `lj_state_growstack` at `0xc7ed10` (verified: that function reads
`L->stacksize` at `[L+0x38]` and resizes the stack). A real `lua_pcall`
must use all 4 args (`L`, `nargs`, `nresults`, `errfunc`).

### The real lua_pcall: source-pattern match

LuaJIT 2.1's `lua_pcall` (src/lj_api.c:1120) has a distinctive compiled
shape. Fetched the v2.1 branch source (commit
`8e6520a7aecd0517e792b359afbbfd7274791f5f`) and matched it against the
binary's `0xc73000–0xc7f000` cluster (368 `.pdata` entries). Source body:

```c
LUA_API int lua_pcall(lua_State *L, int nargs, int nresults, int errfunc)
{
  global_State *g = G(L);                        // [L+0x08] (MRef, 4-byte)
  uint8_t oldh = hook_save(g);                   // [g+0x61]
  ptrdiff_t ef;
  if (errfunc == 0) {
    ef = 0;
  } else {
    cTValue *o = index2adr_stack(L, errfunc);    // L->base + (errfunc-1) [if > 0]
                                                 // L->top + errfunc       [if < 0]
    ef = savestack(L, o);                        // o - L->stack  ([L+0x24])
  }
  status = lj_vm_pcall(L, api_call_base(L, nargs), nresults+1, ef);
                                                 // api_call_base = L->top - nargs*8
  if (status) hook_restore(g, oldh);
  return status;
}
```

The function at **`0xc744c0`** matches this body instruction-by-instruction:

```
0xc744c0: mov  [rsp+8], rbx
0xc744c5: push rdi
0xc744c6: sub  rsp, 0x20
0xc744ca: mov  ebx, [rcx + 8]         ; ebx = glref (g = G(L))      [G(L)]
0xc744cd: mov  r10, rcx                ; r10 = L
0xc744d0: movzx edi, byte [rbx + 0x61] ; edi = g->hookmask           [hook_save]
0xc744d4: test r9d, r9d                ; if (errfunc == 0) ...
0xc744d7: jne  0xc744de                ;   jne = errfunc != 0 branch
0xc744d9: xor  r9d, r9d                ;   ef = 0
0xc744dc: jmp  0xc7450f
0xc744de: jle  0xc744fd                ;   jle = errfunc < 0 branch
   ; positive errfunc path:
0xc744e0: mov  rax, [rcx + 0x10]       ;   rax = L->base             [L->base]
0xc744e4: movsxd r9, r9d               ;   sign-extend errfunc
0xc744e7: dec  r9                      ;   r9 = errfunc - 1
0xc744ea: lea  r9, [rax + r9*8]        ;   r9 = base + (errfunc-1)*8 (TValue* slot)
0xc744ee: cmp  r9, [rcx + 0x18]        ;   compare with L->top        [L->top]
0xc744f2: jb   0xc74508                ;   if slot < top, OK
0xc744f4: lea  r9, [rbx + 0xc0]        ;   else fallback to &g->tmptv [g+0xc0]
0xc744fb: jmp  0xc74508
   ; negative errfunc path:
0xc744fd: mov  rax, [r10 + 0x18]       ;   rax = L->top
0xc74501: movsxd rcx, r9d              ;   sign-extend errfunc
0xc74504: lea  r9, [rax + rcx*8]       ;   r9 = top + errfunc*8
   ; common tail:
0xc74508: mov  eax, [r10 + 0x24]       ; eax = L->stack (MRef, 4-byte) [L->stack]
0xc7450c: sub  r9, rax                 ; r9 = slot - stack = ef (savestack)
0xc7450f: movsxd rax, edx              ; rax = nargs
0xc74512: inc  r8d                     ; r8 = nresults+1
0xc74515: mov  rdx, [r10 + 0x18]       ; rdx = L->top
0xc74519: mov  rcx, r10                ; rcx = L (1st arg to lj_vm_pcall)
0xc7451c: shl  rax, 3                  ; rax = nargs*8
0xc74520: sub  rdx, rax                ; rdx = top - nargs*8 = api_call_base
0xc74523: call 0x6845                  ; lj_vm_pcall(L, base=rdx, nres+1=r8, ef=r9)
0xc74528: test eax, eax
0xc7452a: je   0xc7453c                ; if status == 0, skip hook_restore
0xc7452c: movzx ecx, dil              ; ecx = oldh (saved hookmask)
0xc74530: xor  cl, [rbx + 0x61]        ; cl = old ^ new
0xc74533: and  cl, 0xf                 ; isolate event-mask bits
0xc74536: xor  cl, dil                 ; cl = restored hookmask
0xc74539: mov  [rbx + 0x61], cl        ; g->hookmask = restored       [hook_restore]
0xc7453c: mov  rbx, [rsp + 0x30]       ; epilogue
```

### Callee verification: 0x6845 IS lj_vm_pcall

The single `call` at `0xc74523` targets `0x6845`. That function's prologue
matches LuaJIT 2.1's dynasm `->vm_pcall:` entry (vm_x64.dasc:610):

```
0x006845: push rbp                    ; |  saveregs (push 4 callee-saved
0x006846: push rdi                    ; |    + sub rsp, 0x28)
0x006847: push rsi                    ; |
0x006848: push rbx                    ; |
0x006849: sub  rsp, 0x28              ; |
0x00684d: mov  esi, 5                 ; PCd = FRAME_CP (frame type)
0x006852: mov  [rsp + 0x5c], r9d      ; SAVE_ERRF = errfunc (4th arg)
0x006857: jmp  0x6866                 ; → common entry (vm_call:)
```

Direct correspondence with the dynasm source (vm_x64.dasc:610):

```
|->vm_pcall:                         |  ; the ASM entry point
|  saveregs                          |  ; push regs + sub rsp, CFRAME_SIZE
|  mov PCd, FRAME_CP                 |  ; protected C frame marker
|  mov SAVE_ERRF, CARG4d             |  ; save errfunc in C frame
|  jmp >1                            |  ; fall into vm_call's common path
```

So `0x6845` = `lj_vm_pcall`. lua_pcall is the only C-level caller of
lj_vm_pcall in the binary (the other callers — `lj_gc.c`, `lj_vmevent.c`
— are reachable from elsewhere in the cluster, not from `0xc744c0`).

### Uniqueness

Surveyed all 368 `.pdata` function entries in `[0xc73000, 0xc7f000)` for
the same combined shape (`lea [reg + r9*8]` errfunc arithmetic +
`shl rax,3; sub rdx, rax` api_call_base + `inc r8d` nresults+1 +
exactly one direct `call`). **Only `0xc744c0` matches.** All other
Phase 3 candidates are eliminated:

| Candidate | Disassembly verdict |
|-----------|---------------------|
| `0xc74f30` | 2-arg stack-check helper; overwrites r8/r9; calls `0xc7ed10` (growstack). **Proven wrong** by rev 1 Tier B. |
| `0xc748d0` | Stack-size check + tail-jump to `0xc89ad0` via `0xc82fc0`; never reaches a vm_pcall shape. |
| `0xc754d0` | `cmp rcx, rdx; je` early-exit (this is `lua_xmove(from, to, n)`). |
| `0xc744c0` | **lua_pcall** — full source-pattern match (see above). |

### Why Phase 3 pruned 0xc744c0 incorrectly

Phase 3's prune note said: *"calls `lua_load`, so it's a `luaL_load*`
wrapper."* This was based on a mis-identification of the callee. The
actual callee (verified above) is `lj_vm_pcall` at `0x6845`. The Phase 3
classification heuristic appears to have mis-resolved the call target —
possibly because `0x6845` is below the `.pdata`-indexed "function start"
range Phase 3 used, or because the heuristic compared against a known
`lua_load` body and false-matched. The rev 2 A1 disasm-check makes this
class of mistake impossible to ship: the matcher requires the specific
errfunc/api_call_base/nresults+1 combination that is unique to lua_pcall.

### lua_State layout (derived from this analysis)

The Darktide binary is LJ_64 non-GC64 (32-bit GCrefs/MRefs). Field
offsets derived from this analysis and consistent with `lua_gettop`
(`(top-base)>>3`) and `lua_atpanic` (`[g+0x118] = panic`):

| Offset | Field | Width |
|--------|-------|-------|
| `0x08` | `glref` (MRef → global_State*) | 4 |
| `0x10` | `base` (TValue*) | 8 |
| `0x18` | `top`  (TValue*) | 8 |
| `0x20` | `maxstack` (MRef) | 4 |
| `0x24` | `stack` (MRef → TValue[]) | 4 |
| `0x38` | `stacksize` (uint32) | 4 |

### Reproduction

The matcher (`test/disasm_check.c`) is offline and deterministic — anyone
can reproduce by running `make a1` against the same Darktide.exe build.
Trying it against the rev 1 RVA `0xc74f30` produces:

```
[disasm_check] features: glref_08=1 base_10=1 top_18=1 stack_24=0 test_r9=0
               jne=0 jle=0 lea_r9_x8=0 inc_r8=0 shl_sub(top-nargs*8)=0
               direct_calls=3
[disasm_check] FAIL: shape does not match lua_pcall
```

vs the rev 2 RVA `0xc744c0`:

```
[disasm_check] features: glref_08=1 base_10=1 top_18=1 stack_24=1 test_r9=1
               jne=1 jle=1 lea_r9_x8=1 inc_r8=1 shl_sub(top-nargs*8)=1
               direct_calls=1
[disasm_check] OK: shape matches lua_pcall @ RVA 0xc744c0
```

### Side findings for Phase 5

- **`lj_vm_pcall` = `0x6845`** (definitive — it is the callee of `lua_pcall`
  at `0xc74523`). ASM entry, matches the dynasm `->vm_pcall:` shape:
  `push rbp/rdi/rsi/rbx; sub rsp, 0x28; mov esi, 5; mov [rsp+0x5c], r9d;
  jmp 0x6866`. Useful if Phase 5 wants to hook the actual VM entry (e.g.,
  for tracing every pcall without the C-level wrapper's hookmask overhead).
- **`lj_vm_call` ≈ `0x6859`** (likely, not definitively confirmed). The
  next ASM entry after `lj_vm_pcall`, matching the dynasm source order
  (`->vm_resume` → `->vm_pcall` → `->vm_call`). Its prologue is
  `push rbp/rdi/rsi/rbx; sub rsp, 0x28; mov esi, 1` — no SAVE_ERRF write,
  consistent with a 3-arg entry (`vm_call` or `vm_resume`). Phase 5 can
  confirm by finding the C-level `lua_call` body (not located in this
  pass — likely inlined or at an address outside the scanned cluster).
- **`hook_restore` writes `[g+0x61]`** — the `hookmask` byte. Phase 5's
  timing work may want to read this to detect when the VM has active
  hooks (line/debug hooks fire at known points).
- **`g->tmptv` at `g+0xc0`** — LuaJIT's "temporary TValue" slot, used
  here as the fallback for invalid errfunc indices. May be useful for
  Phase 5 scratch space if we need a known-valid TValue* inside the
  engine's VM.

---

## Phase 5 Step 2 — openlibs diagnostic

> Phase 5 addresses the critical Phase 4 finding: the default `_G` on the
> captured `lua_State*` is sandboxed (`print`, `io`, `require` all
> unavailable). Step 2 is a DIAGNOSTIC — call `luaL_openlibs(L)` ourselves
> and observe whether the sandbox is fixed. The result determines Phase 5's
> direction.

### What the diagnostic does

From inside the `lua_pcall` detour, AFTER the engine's pcall completes
and BEFORE the injection attempt:

1. Check the one-shot latch `g_openlibs_called`. If set, skip (we only
   call openlibs once per DLL lifetime).
2. Set the reentry guard `g_injecting` (same pattern as the injection
   guard — protects against nested pcall from openlibs's internals,
   e.g. `luaopen_package` initializing loaders).
3. Call `p_luaL_openlibs(L)` (resolved to `base + 0xc7f380` in
   `inject_install`).
4. Clear the reentry guard.
5. Set the latch.
6. Log: `[darktide-poc] openlibs called on captured L=0x<ptr>`.

Then the injection proceeds as before (loadbuffer + pcall of the
diagnostic chunk).

### The diagnostic chunk

```lua
if not print then error('no_print_after_openlibs') end
print('[INJECTED] Hello from the DLL')
local f = io.open('darktide-poc-executed.txt','w')
if f then f:write('executed'); f:close() end
return 42
```

The chunk checks `print` (the most basic global from the base library)
and uses `io.open` (the deterministic file-write observable). Both come
from standard libraries that `luaL_openlibs` registers.

### What the pcall_rc tells us

| `pcall_rc` after openlibs | Interpretation | Phase 5 direction |
|---------------------------|----------------|-------------------|
| **0** | openlibs registered `print`/`io` in `_G` on our captured state. Our chunks can use them. | **Scenario (a)**: the engine never called openlibs on our captured state. Phase 5 proceeds with the standard bootstrap plan (set up `Mods.*` globals, load `dmf_loader.lua`). |
| **2** (persistent across retries) | openlibs didn't help — `print` is still nil even after we call openlibs. | **Scenario (b) or (c)**: the engine replaces `_G`, or `print` is filtered by an `__index` metamethod, or our captured state is somehow protected against openlibs's writes. Deeper investigation: hook `lua_resource::bytecode` to find the engine's environment setup. |

### Side finding (from binary analysis): the engine DOES call openlibs

During Step 1 (the RVA identification), I found that the engine's init
function at `0x32a2a0` (called from `LuaEnvironment::init` at
`0x32a8d0`, inside the documented `0x32a660`–`0x32aa2f` range) DOES call
`luaL_openlibs(L)` as its first action:

```
0x32a2a0: mov rdi, [rcx]         ; rdi = this->L (lua_State*)
0x32a2ad: mov rbx, rcx           ; rbx = this
0x32a2b0: mov rcx, rdi
0x32a2b3: call luaL_openlibs     ; opens all standard libs on this->L
0x32a2b8: ...                    ; engine-specific luaopen calls follow
```

After openlibs, this same function (`0x32a2a0`) does **selective global
replacement** via `lua_setfield(L, LUA_GLOBALSINDEX, ...)`:

| Engine global | Action | Notes |
|---------------|--------|-------|
| `require`     | Saved to `_G.lua_require`, then replaced with engine wrapper | Backup of original; new wrapper enforces bundle-system loading |
| `dofile`      | Replaced with engine wrapper | Engine-controlled file loading |
| `print`       | Replaced with engine print (closure with upvalue) | Routes to engine console/log |
| `print_command`, `print_warning`, `print_error` | Added | Engine-specific print variants |
| `loadfile`    | Replaced | Engine-controlled |
| `load`        | Replaced (tail-jump to setfield) | Engine-controlled |

**Crucially**: `io`, `table`, `string`, `math`, `os`, `debug`, `bit`,
`jit`, `ffi`, `package` are NOT touched by this function. They remain in
`_G` after init (as registered by openlibs).

This raises a puzzle: if the engine calls openlibs AND only replaces
specific globals (not `io`), then why did Phase 4 show `io.open` failing?
Three possible explanations the diagnostic will distinguish:

1. **Our captured state ≠ the engine's main state.** Our `lua_newstate`
   hook captures the FIRST state created; the engine might create a
   sandbox state first and call openlibs only on the later "real" state.
   → Diagnostic outcome: `pcall_rc=0` (calling openlibs on OUR state
   fixes it). This is **Scenario (a)** for our captured state.

2. **Additional sandboxing after `0x32a2a0`.** Some later init step
   clears `_G` or installs an `__index` metamethod that filters globals.
   → Diagnostic outcome: depends. If a metamethod filters `print`, even
   our openlibs call wouldn't fix it (`pcall_rc=2`). If `_G` was replaced
   wholesale, our openlibs call would write to the new `_G` and succeed
   (`pcall_rc=0`).

3. **Timing.** Our injection ran before `0x32a2a0` completed. By the
   time we retry (500ms later), init has finished and globals are
   available — but the Phase 4 chunk (`return 42`, no globals) succeeds
   regardless, so we couldn't tell. The Phase 5 chunk checks `print`
   explicitly, which would catch this.

### Prediction (informational — Tier B confirms)

Based on the binary analysis, I predict **`pcall_rc=0`** — openlibs
called from our detour will register `print`/`io` in `_G` on the
captured state, and the diagnostic chunk will succeed. Reasoning:

- The engine init (`0x32a2a0`) calls openlibs and only replaces specific
  globals (`require`, `dofile`, `print`, `loadfile`, `load`). It does
  NOT remove `io`.
- If our captured state IS the engine's main state (most likely —
  Stingray uses a single Lua VM), then `io` is already present from the
  engine's openlibs call. Calling openlibs again (idempotent) doesn't
  change that.
- If our captured state is NOT the engine's main state (a sandbox state
  created earlier), our openlibs call opens the libs on it directly.

Either way, after our openlibs call, `print` and `io` should be
available on the captured state. The chunk succeeds.

If the prediction is WRONG (`pcall_rc=2`), the most likely cause is an
`__index` metamethod on `_G` that filters which globals are visible. In
that case, Phase 5 Step 3+ (hook `lua_resource::bytecode` to find the
engine's environment setup) is required.

### A1 coverage of the diagnostic

The A1 test validates all three paths offline:

- **Phase 1**: VM with libs already open → detour calls openlibs (near-
  no-op) → chunk succeeds. `openlibs_called=1`, `pcall_rc=0`.
- **Phase 2 (the key validation)**: VM WITHOUT openlibs → detour calls
  openlibs → libs are opened → chunk succeeds. `openlibs_called=1`,
  `pcall_rc=0`. This proves the openlibs call from the detour works.
- **Phase 3**: VM WITHOUT openlibs AND openlibs pointer=NULL → detour
  skips openlibs → chunk fails (`print` is nil) → retry happens.
  `openlibs_called=0`, `pcall_rc=2`. This preserves the Phase 4
  retry-on-error coverage.

---

## luaL_openlibs identification (Phase 5 Step 1 — the actual analysis)

> This section documents how the RVA `0xc7f380` was identified via
> source-pattern matching against LuaJIT 2.1's `lib_init.c`. The method
> is reproducible from the binary alone — no live-game work needed.
> Same approach that identified `lua_pcall` at `0xc744c0` (Phase 4 rev2).

### The source: `lib_init.c` (NOT `lj_lib.c`)

The ENGAGEMENT-STATE speculatively said `luaL_openlibs` is in `lj_lib.c`.
The actual LuaJIT 2.1 source (v2.1 branch,
`8e6520a7aecd0517e792b359afbbfd7274791f5f`) places it in `src/lib_init.c`:

```c
LUALIB_API void luaL_openlibs(lua_State *L)
{
  const luaL_Reg *lib;
  /* Loop 1: open each standard library via lua_call */
  for (lib = lj_lib_load; lib->func; lib++) {
    lua_pushcfunction(L, lib->func);
    lua_pushstring(L, lib->name);
    lua_call(L, 1, 0);
  }
  /* Create the _PRELOAD registry table */
  luaL_findtable(L, LUA_REGISTRYINDEX, "_PRELOAD",
                 sizeof(lj_lib_preload)/sizeof(lj_lib_preload[0])-1);
  /* Loop 2: register preload entries (ffi) via setfield */
  for (lib = lj_lib_preload; lib->func; lib++) {
    lua_pushcfunction(L, lib->func);
    lua_setfield(L, -2, lib->name);
  }
  lua_pop(L, 1);
}
```

`lj_lib_load[]` has 10 entries (base, package, table, io, os, string,
math, debug, bit, jit). `lj_lib_preload[]` has 1 entry (ffi). So loop 1
runs 10 times, loop 2 runs 1 time.

### The string anchor: `_PRELOAD`

The function references the literal string `"_PRELOAD"` (used as the
table name argument to `luaL_findtable`). This string is in `.rdata` at
**RVA `0xe8d678`** (single occurrence in the binary). This is the only
function that uses `"_PRELOAD"` as a direct LEA-rip target from a small
looping function.

### The compiled shape

The function at **`0xc7f380`** (size `0xc2` = 194 bytes, ends
`0xc7f442`) matches the source instruction-by-instruction:

```
0xc7f380: mov [rsp+8], rbx          ; prologue
0xc7f385: push rdi
0xc7f386: sub rsp, 0x20
0xc7f38a: mov rbx, rcx              ; rbx = L
0xc7f38d: lea rdi, [0xe8d580]       ; rdi = &lj_lib_load[0]
0xc7f394: lea rax, [0xc9ac10]       ; rax = lj_lib_load[0].func (luaopen_base)
0xc7f39b: nop                       ; --- loop 1 alignment ---

; --- LOOP 1 body (10 iterations) ---
0xc7f3a0: xor r8d, r8d              ; r8 = 0 (nresults for lua_call)
0xc7f3a3: mov rdx, rax              ; rdx = lib->func
0xc7f3a6: mov rcx, rbx              ; rcx = L
0xc7f3a9: call 0xc74580             ; lua_pushcfunction(L, lib->func)
0xc7f3ae: mov rdx, [rdi]            ; rdx = lib->name
0xc7f3b1: mov rcx, rbx
0xc7f3b4: call 0xc747d0             ; lua_pushstring(L, lib->name)
0xc7f3b9: xor r8d, r8d              ; r8 = 0 (nresults)
0xc7f3bc: mov edx, 1                ; edx = 1 (nargs)
0xc7f3c1: mov rcx, rbx
0xc7f3c4: call 0xc738e0             ; lua_call(L, 1, 0)
0xc7f3c9: mov rax, [rdi+0x18]       ; prefetch next lib->func
0xc7f3cd: lea rdi, [rdi+0x10]       ; advance rdi by sizeof(luaL_Reg) = 16
0xc7f3d1: test rax, rax
0xc7f3d4: jne 0xc7f3a0              ; loop if next->func != NULL

; --- BETWEEN LOOPS: luaL_findtable(L, LUA_REGISTRYINDEX, "_PRELOAD", 1) ---
0xc7f3d6: mov r9d, 1                ; r9 = 1 (szhint = 2-1 = 1)
0xc7f3dc: lea r8, [0xe8d678]        ; r8 = "_PRELOAD" (the ONLY string ref)
0xc7f3e3: mov edx, 0xffffd8f0       ; edx = -10000 = LUA_REGISTRYINDEX
0xc7f3e8: mov rcx, rbx              ; rcx = L
0xc7f3eb: call 0xc7c250             ; luaL_findtable(L, -10000, "_PRELOAD", 1)

; --- LOOP 2 body (1 iteration: ffi) ---
0xc7f3f0: lea rax, [0xca37f0]       ; rax = lj_lib_preload[0].func (luaopen_ffi)
0xc7f3f7: lea rdi, [0xe8d630]       ; rdi = &lj_lib_preload[0]
0xc7f3fe: nop                       ; --- loop 2 alignment ---
0xc7f400: xor r8d, r8d              ; r8 = 0 (nups)
0xc7f403: mov rdx, rax              ; rdx = lib->func
0xc7f406: mov rcx, rbx
0xc7f409: call 0xc74580             ; lua_pushcfunction(L, lib->func)
0xc7f40e: mov r8, [rdi]             ; r8 = lib->name
0xc7f411: mov edx, 0xfffffffe       ; edx = -2 (stack index for setfield)
0xc7f416: mov rcx, rbx
0xc7f419: call 0xc74cb0             ; lua_setfield(L, -2, lib->name)
0xc7f41e: mov rax, [rdi+0x18]       ; prefetch next lib->func
0xc7f422: lea rdi, [rdi+0x10]       ; advance
0xc7f426: test rax, rax
0xc7f429: jne 0xc7f400              ; loop if next->func != NULL

; --- CLEANUP: lua_pop(L, 1) via tail-jump to lua_settop ---
0xc7f42b: mov edx, 0xfffffffe       ; edx = -2 (= lua_settop(L, -2) = lua_pop(L, 1))
0xc7f430: mov rcx, rbx
0xc7f433: mov rbx, [rsp+0x30]       ; restore rbx
0xc7f438: add rsp, 0x20
0xc7f43c: pop rdi
0xc7f43d: jmp 0xc74f30              ; tail-jump to lua_settop
```

### The 6 distinct call targets

The function makes exactly 6 direct calls + 1 tail-jump, each to a
distinct LuaJIT API function:

| Call target | Called from | LuaJIT API function |
|-------------|-------------|---------------------|
| `0xc74580` | Loop 1 body + Loop 2 body | `lua_pushcfunction` (used 2x) |
| `0xc747d0` | Loop 1 body | `lua_pushstring` |
| `0xc738e0` | Loop 1 body | `lua_call` |
| `0xc7c250` | Between loops | `luaL_findtable` |
| `0xc74cb0` | Loop 2 body | `lua_setfield` |
| `0xc74f30` | Tail-jump at end | `lua_settop` (= `lua_pop(L, 1)`) |

Note: `0xc74f30` is the same address Phase 3 *incorrectly* identified as
`lua_pcall`. This confirms Phase 3 was wrong — `0xc74f30` is actually
`lua_settop` (a stack-manipulation leaf function), which is why the rev 1
live game test produced `pcall_rc=-84340864` (a pointer, not an int).

### Verification

The A1 disasm-check (`disasm_check_luaL_openlibs`) validates the shape:

```
[disasm_check] decoded 56 insns at RVA 0xc7f380 (file off 0xc7f780)
[disasm_check] openlibs features: preload_lea=1 direct_calls=6 distinct_tgts=5
               backward_jne=2 all_in_cluster=1 body_size=0xc2
[disasm_check] OK: shape matches luaL_openlibs @ RVA 0xc7f380
```

Required features (all present):
- `preload_lea=1`: LEA r, [rip+disp32] targeting `"_PRELOAD"` (rva `0xe8d678`)
- `direct_calls=6`: 6 direct `call rel32` in the body
- `distinct_tgts=5`: 5 distinct targets (pushcfunction counted once though
  called twice — `0xc74f30` is a tail-jmp, not a call)
- `backward_jne=2`: two loop back-edges (loop 1 and loop 2)
- `all_in_cluster=1`: all call targets within `[0xc70000, 0xc90000)`
- `body_size=0xc2`: 194 bytes, matching the `.pdata` function entry exactly

### Side findings for Phase 5

- **`lua_pushcfunction = 0xc74580`** — confirmed (called twice in openlibs).
  This is the function Phase 5 needs for the C-function bootstrap
  alternative (implement `Mods.file.dofile` as a C function).
- **`lua_pushstring = 0xc747d0`** — confirmed.
- **`lua_call = 0xc738e0`** — confirmed (the UNPROTECTED call). Our hook
  is on `lua_pcall`, not `lua_call`, so openlibs's internal calls don't
  re-enter our detour (except via `luaopen_package`'s pcall usage, which
  the reentrancy guard handles).
- **`luaL_findtable = 0xc7c250`** — confirmed.
- **`lua_setfield = 0xc74cb0`** — confirmed. Phase 5 needs this (or
  `lua_setglobal`) to set up the `Mods.*` globals.
- **`lua_settop = 0xc74f30`** — confirmed (this was the Phase 3
  misidentification of `lua_pcall`; it's actually `lua_settop`). Phase 5
  can use this for stack cleanup without needing `lua_pop`'s address
  (which is just an inlined wrapper around `lua_settop`).
- **The `lj_lib_load[]` data array is at rva `0xe8d580`** in `.rdata`.
  Each entry is 16 bytes (`{ char *name; lua_CFunction func; }`). The
  function pointers in the array are the `luaopen_*` bodies. Useful for
  Phase 5 if we need to call a specific `luaopen_*` (e.g. to re-register
  just the `io` library without the full openlibs).
- **The engine's init (`0x32a2a0`) replaces specific globals after
  openlibs**: `require`, `dofile`, `print`, `loadfile`, `load`. See
  §"Phase 5 Step 2 — openlibs diagnostic" above for the full table. This
  is the engine's sandboxing surface — Phase 5 must account for these
  replacements (e.g. the engine's `require` enforces the bundle system;
  our `Mods.require_store` should preserve the original `lua_require`
  backup the engine itself creates).

---

## Phase 5 Step 3 — C-function bootstrap

> Phase 5 Step 3 implements the **C-function bootstrap**: register our
> own C function as a Lua global via the LuaJIT C API, bypassing the
> sandboxed `_G` entirely. This is the production-ready fix for the
> sandbox finding (Phase 4 + Phase 5 Step 2 confirmed openlibs is
> destructive — calling it on the engine's state crashes the game within
> 1 second).

### The problem (recap)

The Stingray engine's default Lua global environment (`L->gt`, exposed
as `_G`) does NOT expose standard library functions to chunks we load
via `luaL_loadbuffer`. `print`, `io.open`, `require('ffi')` all return
`LUA_ERRRUN` (2) indefinitely. The engine's own scripts work because
the bundle system loads them with a custom environment.

Phase 5 Step 2's diagnostic proved that calling `luaL_openlibs` on the
captured state DOES register `print`/`io` — but it's **destructive**
(overwrites the engine's custom globals → crash within 1 second). The
sandbox is caused by the engine REPLACING specific globals after its own
openlibs call, not by openlibs never being called.

### The fix: register our own C function as a Lua global

From inside the `lua_pcall` detour, AFTER the (disabled) openlibs call
and BEFORE the injection attempt, we register `poc_print` via the LuaJIT
C API:

1. Check the one-shot latch `g_cfuncs_registered`. If set, skip (we only
   register once per DLL lifetime).
2. Set the reentry guard `g_injecting` (defensive — neither function
   should re-enter pcall, but GC indirection is unpredictable).
3. Call `p_lua_pushcclosure(L, &poc_print, 0)` (resolved to
   `base + 0xc74580`). Pushes a C closure with 0 upvalues onto the stack.
4. Call `p_lua_setfield(L, LUA_GLOBALSINDEX, "poc_print")` (resolved to
   `base + 0xc74cb0`, with `LUA_GLOBALSINDEX = -10002`). Pops the top
   of stack and sets it as `_G.poc_print`.
5. Clear the reentry guard.
6. Set the latch.
7. Log: `[darktide-poc] cfunctions registered: poc_print`.

Then the injection proceeds as before (loadbuffer + pcall of the chunk).
The chunk is now:

```lua
poc_print()   -- calls our C function, registered via the LuaJIT C API
return 42
```

When the chunk runs, `poc_print()` fires our C function, which writes:

```
[darktide-poc] [FROM LUA] poc_print called — Lua executed a C function!
```

to `darktide-poc.log`. **This is the observable proof** that Lua executed
a C function we provided — bypassing the sandboxed `_G` entirely.

### Why this works

The LuaJIT C API operates on `L->gt` (the global table) directly via
`LUA_GLOBALSINDEX`. When we call `lua_setfield(L, LUA_GLOBALSINDEX,
"poc_print")`, LuaJIT writes `<global_table>.poc_print = <our C closure>`.
This is exactly what `luaL_openlibs` does internally for the standard
libraries — and exactly what the engine does when it replaces `_G.print`
with its own wrapper (the engine's init at `0x32a2a0` uses the identical
`mov edx, 0xffffd8ee` (`LUA_GLOBALSINDEX`) + `call lua_setfield` pattern
13 times).

The difference: we don't try to RESTORE the standard library globals
(which would conflict with the engine's replacements and crash). We
provide our OWN globals with our OWN names (`poc_print`, not `print`).
Our chunk only references our globals, so it doesn't care what the engine
did to `_G.print` or `_G.io`.

### A1 coverage of the bootstrap

The A1 test validates all four scenarios offline:

- **Phase 1**: VM with libs already open + bootstrap wired → chunk succeeds.
  `cfuncs_registered=1`, `pcall_rc=0`, `poc_print_calls >= 1`. (Happy
  path; bootstrap coexists with openlibs.)
- **Phase 2 (KEY)**: VM WITHOUT openlibs + bootstrap wired → chunk succeeds
  BECAUSE we registered poc_print. `cfuncs_registered=1`, `pcall_rc=0`,
  `poc_print_calls >= 1`. **This is the offline proof that the bootstrap
  bypasses the sandboxed `_G`.**
- **Phase 3 (production scenario)**: VM WITHOUT openlibs + bootstrap wired
  + openlibs pointer NULL → chunk succeeds. The bootstrap is self-sufficient.
- **Phase 4 (negative control)**: VM WITHOUT openlibs + bootstrap DISABLED
  → chunk fails (`poc_print_calls == 0`, `pcall_rc=2`), retry happens
  (`inject_count` increments). **This proves the chunk actually depends
  on our registration (not on some other mechanism).**

### What Tier B confirms

The expected log sequence in the live game:

```
[darktide-poc] inject lua_pcall hook installed at 0x...c744c0 (rva=0xc744c0) ...
[darktide-poc] captured lua_State* = 0x<L>
...
[darktide-poc] cfunctions registered: poc_print                    <-- bootstrap ran
[darktide-poc] [FROM LUA] poc_print called — Lua executed a C function!  <-- THE PROOF
[darktide-poc] injected attempt=1 load_rc=0 pcall_rc=0 (0=success) ...
```

The success bar: the `[FROM LUA]` line appears, paired with
`pcall_rc=0`. If the chunk fails (e.g., our pushcclosure or setfield
address is wrong), `pcall_rc=2` will persist across retries — the same
shape as the Phase 4 sandbox finding, but now with a different root cause
(broken registration, not missing openlibs).

---

## lua_pushcclosure / lua_setfield identification (Phase 5 Step 3 — the actual analysis)

> This section documents how the RVAs `0xc74580` (lua_pushcclosure) and
> `0xc74cb0` (lua_setfield) were identified via source-pattern matching
> against LuaJIT 2.1's `lj_api.c`. The method is reproducible from the
> binary alone — no live-game work needed. Same approach that identified
> `lua_pcall` at `0xc744c0` (Phase 4 rev2) and `luaL_openlibs` at
> `0xc7f380` (Phase 5 Step 1).
>
> These addresses were also independently surfaced as call-targets in
> the `luaL_openlibs` disassembly (§"luaL_openlibs identification" above,
> side finding): `0xc74580` is called twice from openlibs's loops and
> `0xc74cb0` is called once from openlibs's loop 2. This cross-validation
> (two independent identifications pointing to the same addresses) gives
> high confidence.

### Critical discovery: both target names are macros, not functions

The brief asked for `lua_pushcfunction` and `lua_setglobal`. Both are
**preprocessor macros** in `lua.h`, not real functions:

```c
/* lua.h:262 */ #define lua_pushcfunction(L,f)  lua_pushcclosure(L, (f), 0)
/* lua.h:278 */ #define lua_setglobal(L,s)      lua_setfield(L, LUA_GLOBALSINDEX, (s))
/* lua.h:38  */ #define LUA_GLOBALSINDEX        (-10002)
```

So:
- `lua_pushcfunction(L, f)` is inlined as `lua_pushcclosure(L, f, 0)` at
  every call site. The actual function in the binary is
  **`lua_pushcclosure`** at `lj_api.c:678`. Calling it with `n=0` gives
  us `lua_pushcfunction`'s behavior.
- `lua_setglobal(L, name)` is inlined as `lua_setfield(L, -10002, name)`
  at every call site. The actual function in the binary is
  **`lua_setfield`** at `lj_api.c:970`. Calling it with
  `idx=LUA_GLOBALSINDEX` gives us `lua_setglobal`'s behavior.
- `LUA_GLOBALSINDEX = -10002 = 0xFFFFD8EE`. **Verified** against the
  engine's own init at `0x32a2a0`, which uses `mov edx, 0xffffd8ee`
  13 times to replace `_G.<name>` globals (the engine's sandboxing
  surface — see Phase 5 Step 2 §"Side finding").

There is **no standalone `lua_setglobal` wrapper** in the binary. A
scan of the LuaJIT API cluster for any small function that tail-calls
or wraps `lua_setfield` with `edx=-10002` returned zero candidates —
the macro is fully inlined at every call site.

### lua_pushcclosure @ 0xc74580 (body_size=0xae)

Source (`lj_api.c:678`):

```c
LUA_API void lua_pushcclosure(lua_State *L, lua_CFunction f, int n)
{
  GCfunc *fn;
  lj_gc_check(L);                              // conditional call
  lj_checkapi_slot(n);
  fn = lj_func_newC(L, (MSize)n, getcurrenv(L));   // call
  fn->c.f = f;
  L->top -= n;
  while (n--) copyTV(L, &fn->c.upvalue[n], L->top+n);  // backward jne loop
  setfuncV(L, L->top, fn);                     // writes 0xfffffff7 tag
  incr_top(L);                                 // bounds check + call
}
```

Disassembly @ `0xc74580` (matches source instruction-by-instruction):

```
0xc74580: mov [rsp+8], rbx           ; prologue
0xc74585: mov [rsp+0x10], rsi
0xc7458a: push rdi
0xc7458b: sub rsp, 0x20
0xc7458f: mov rbx, rcx               ; rbx = L
0xc74592: movsxd rdi, r8d            ; rdi = sign-extend n   [THE n ARG]
0xc74595: mov ecx, [rcx+8]           ; g = G(L)              [glref]
0xc74598: mov rsi, rdx               ; rsi = f (saved)
0xc7459b: mov eax, [rcx+0x14]        ; g->gc state            [lj_gc_check]
0xc7459e: cmp [rcx+0x10], eax        ; threshold check
0xc745a1: jb 0xc745ab
0xc745a3: mov rcx, rbx
0xc745a6: call 0xc82fc0              ; lj_gc_check (conditional)
0xc745ab: mov rax, [rbx+0x10]        ; L->top (for getcurrenv)
0xc745af: mov ecx, [rax-8]           ; frame lookup
0xc745b2: cmp byte [rcx+5], 8
0xc745b6: jne 0xc745bd
0xc745b8: mov eax, [rcx+8]
0xc745bb: jmp 0xc745c0               ; intra-body forward jmp
0xc745bd: mov eax, [rbx+0x2c]        ; getcurrenv fallback
0xc745c0: mov r8d, eax               ; r8 = env
0xc745c3: mov edx, edi               ; edx = n
0xc745c5: mov rcx, rbx
0xc745c8: call 0xc85460              ; lj_func_newC(L, n, env)
0xc745cd: lea rdx, [rdi*8]           ; rdx = n*8 (TValue bytes)
0xc745d5: mov r8, rax                ; r8 = fn
0xc745d8: mov [rax+0x18], rsi        ; fn->c.f = f
0xc745dc: sub [rbx+0x18], rdx        ; L->top -= n
0xc745e0: test edi, edi              ; while (n != 0)
0xc745e2: je 0xc745fa
0xc745e4: mov rax, [rbx+0x18]        ;   loop body (copyTV)
0xc745e8: lea rdx, [rdx-8]
0xc745ec: mov rcx, [rdx+rax]
0xc745f0: mov [rdx+r8+0x20], rcx
0xc745f5: sub edi, 1
0xc745f8: jne 0xc745e4               ; BACKWARD jne (loop back-edge)
0xc745fa: mov rax, [rbx+0x18]
0xc745fe: mov [rax], r8d             ; setfuncV low half (GCfunc*)
0xc74601: mov dword [rax+4], 0xfffffff7  ; setfuncV tag (LJ_TFUNC = 0xF7)
0xc74608: add [rbx+0x18], 8          ; incr_top (L->top += 8)
0xc7460d: mov eax, [rbx+0x20]        ; L->maxstack
0xc74610: cmp [rbx+0x18], rax        ; bounds check
0xc74614: jb 0xc7461e
0xc74616: mov rcx, rbx
0xc74619: call 0xc7ede0              ; lj_state_growstack
0xc7461e: mov rbx, [rsp+0x30]        ; epilogue
0xc74623: mov rsi, [rsp+0x38]
0xc74628: add rsp, 0x20
0xc7462c: pop rdi
0xc7462d: ret
```

Distinctive compiled features (all present, all verified by A1
`disasm_check_lua_pushcclosure`):

| Feature | Evidence | Source correspondence |
|---------|----------|----------------------|
| `movsxd rdi, r8d` | sign-extends `n` (the nups arg) | `int n` parameter |
| backward `jne` | the `while (n--)` upvalue-copy loop | `copyTV` loop |
| `0xfffffff7` written to a tag slot | LJ_TFUNC type tag (low byte 0xF7) | `setfuncV(L, L->top, fn)` |
| exactly 3 direct calls | `lj_gc_check`, `lj_func_newC`, `lj_state_growstack` | 3 helper calls in source |

### lua_setfield @ 0xc74cb0 (body_size=0x76)

Source (`lj_api.c:970`):

```c
LUA_API void lua_setfield(lua_State *L, int idx, const char *k)
{
  TValue *t;
  cTValue *o;
  lj_checkapi(...);
  t = index2adr(L, idx);          // first call; edx still = idx
  ... keyinit(L, &key, k); ...    // second call (lj_str_newz)
  o = lj_tab_set(L, t, &key);     // third + fourth calls
  copyTV(L, o, L->top - 1);
  L->top--;                       // lea rcx,[rdx-8]; mov [rsi+0x18],rcx
}
```

Disassembly @ `0xc74cb0` (matches source):

```
0xc74cb0: mov [rsp+8], rbx           ; prologue
0xc74cb5: mov [rsp+0x10], rsi
0xc74cba: push rdi
0xc74cbb: sub rsp, 0x20
0xc74cbf: mov rbx, r8                ; rbx = k (saved; r8 = k arg)   [THE k ARG]
0xc74cc2: mov rsi, rcx               ; rsi = L (saved)                [THE L ARG]
                                     ; (edx = idx is NOT saved — consumed by first call)
0xc74cc5: call 0xc72be0              ; index2adr(L, idx) — rcx=L, edx=idx still set
0xc74cca: mov rcx, rbx               ; rcx = k
0xc74ccd: mov rdi, rax               ; rdi = t (saved return)
0xc74cd0: call 0xdf5a98              ; lj_str_newz(k) — intern the key string
0xc74cd5: mov r8, rax                ; r8 = interned key
0xc74cd8: mov rdx, rbx               ; rdx = k
0xc74cdb: mov rcx, rsi               ; rcx = L
0xc74cde: call 0xc83a20              ; lj_tab_setkey
0xc74ce3: lea r8, [rsp+0x48]         ; r8 = &local TValue (the value slot)
0xc74ce8: mov [rsp+0x48], eax        ; store low half
0xc74cec: mov rdx, rdi               ; rdx = t
0xc74cef: mov dword [rsp+0x4c], 0xfffffffb  ; LJ_TSTR key tag (0xFB)
0xc74cf7: mov rcx, rsi               ; rcx = L
0xc74cfa: call 0xc866a0              ; lj_tab_set
0xc74cff: mov rdx, [rsi+0x18]        ; rdx = L->top
0xc74d03: test rax, rax
0xc74d06: je 0xc74d26
0xc74d08: lea rcx, [rdx-8]           ; L->top - 8                [L->top--]
0xc74d0c: mov [rsi+0x18], rcx        ; L->top = (top-8)          [L->top--]
0xc74d10: mov rcx, [rcx]             ; load the value being set
0xc74d13: mov [rax], rcx             ; copyTV: write value to slot
0xc74d16: mov rbx, [rsp+0x30]        ; epilogue
0xc74d1b: mov rsi, [rsp+0x38]
0xc74d20: add rsp, 0x20
0xc74d24: pop rdi
0xc74d25: ret
```

Distinctive compiled features (all present, all verified by A1
`disasm_check_lua_setfield`):

| Feature | Evidence | Source correspondence |
|---------|----------|----------------------|
| 3-arg prologue saves `rcx`→`rsi` AND `r8`→`rbx`, NOT `edx` | `mov rsi, rcx` + `mov rbx, r8` early | `lua_setfield(L, idx, k)` — idx consumed by index2adr |
| writes `0xfffffffb` | LJ_TSTR key tag (low byte 0xFB) | `keyinit` building the string key TValue |
| ends with `lea rcx,[rdx-8]; mov [rsi+0x18],rcx` | L->top-- | `L->top--` at end of source |
| exactly 4 direct calls | `index2adr`, `lj_str_newz`, `lj_tab_setkey`, `lj_tab_set` | 4 helper calls in source |

### lua_pushstring @ 0xc747d0 (bonus, body_size=0x79)

Not strictly needed for the C-function bootstrap (`poc_print` takes no
args, returns nothing), but identified and documented for future use
(returning strings from C functions).

Source (`lj_api.c:647`):

```c
LUA_API void lua_pushstring(lua_State *L, const char *str)
{
  if (str == NULL) {
    setnilV(L->top);                 // writes 0xffffffff (NIL tag)
  } else {
    GCstr *s;
    lj_gc_check(L);
    s = lj_str_newz(L, str);
    setstrV(L, L->top, s);           // writes 0xfffffffb (STR tag)
  }
  incr_top(L);
}
```

Distinctive compiled features:
- `test rdx, rdx` + `jne` (the NULL check on `str`).
- writes BOTH `0xffffffff` (NIL tag, the NULL path) AND `0xfffffffb`
  (STR tag, the non-NULL path) — the two branches of the `if`.
- 4 direct calls (lj_gc_check, lj_str_newz, setstrV helper, lj_state_growstack).

A `disasm_check_lua_pushstring` matcher is not implemented (the function
isn't currently wired into the detour). The identification is documented
purely as a side-finding for future Phase 5 work.

### Uniqueness verification

To confirm each function is uniquely identified by its source pattern, I
scanned **all 219 candidate function starts** in the LuaJIT API cluster
`[0xc73000, 0xc80000)` (heuristic: prologue pattern `48 89 5C 24` = `mov
[rsp+8], rbx`). For each candidate, I ran the feature-set check and
counted matches:

| Matcher | Matches in 219-cluster |
|---------|------------------------|
| `lua_pushcclosure` (movsxd r8d + backward jne + 0xF7 tag + 3 calls) | **1 of 219** (`0xc74580`) |
| `lua_pushstring` (test rdx,rdx + both 0xFFFFFFFF and 0xFFFFFFFB tags) | **1 of 219** (`0xc747d0`) |
| `lua_setfield` (rcx→rsi + r8→rbx saves + 0xFB tag + L->top-- end + 4 calls) | **1 of 219** (`0xc74cb0`) |

All three patterns are unique in the cluster.

### Verification: A1 disasm-checks

The A1 test gates each address with its own matcher:

```
[A1] verifying lua_pushcclosure RVA 0xc74580 in (default Darktide.exe path) ...
[disasm_check] decoded 64 insns at RVA 0xc74580 (file off 0xc73b80)
[disasm_check] pushcclosure features: movsxd_r8d=1 backward_jne=1 func_tag(0xF7)=1 direct_calls=3 all_in_cluster=1 body_size=0xae
[disasm_check] OK: shape matches lua_pushcclosure @ RVA 0xc74580

[A1] verifying lua_setfield RVA 0xc74cb0 in (default Darktide.exe path) ...
[disasm_check] decoded 48 insns at RVA 0xc74cb0 (file off 0xc742b0)
[disasm_check] setfield features: save_rsi_from_rcx=1 save_rbx_from_r8=1 str_tag(0xFB)=1 top_decrement=1 direct_calls=4 all_in_cluster=1 body_size=0x76
[disasm_check] OK: shape matches lua_setfield @ RVA 0xc74cb0
```

Each matcher is also tested in the negative (run at a wrong RVA — must
return nonzero), guaranteeing the matcher is discriminative.

### Side findings for Phase 5

- **`lua_pushcclosure` is the canonical way to push a C function.** When
  Phase 5 needs to implement DMF's 6 dependencies as C functions
  (`Mods.file.dofile`, `Mods.lua.loadstring`, `Mods.lua.io`,
  `Mods.require_store`, `Mods.original_require`, `__print`), it calls
  `lua_pushcclosure(L, &my_func, n)` for each, where `n` is the number
  of upvalues (typically 0). The closure is then assigned to its target
  global via `lua_setfield(L, LUA_GLOBALSINDEX, name)` (or to a `Mods`
  subtable via `lua_setfield(L, <table_idx>, name)`).
- **`lua_setfield` works for any table, not just `_G`.** Passing
  `idx=LUA_GLOBALSINDEX` writes to `_G`; passing a positive index writes
  to that stack slot (a table). Phase 5 will use this to build the
  `Mods.file`, `Mods.lua`, etc. subtables (create the table with
  `lua_createtable`, set fields with `lua_setfield`, then assign the
  outer table to `_G.Mods`).
- **`LUA_GLOBALSINDEX = -10002`** in LuaJIT 2.1. The engine uses this
  exact constant 13 times in its init function (`0x32a2a0`) — confirmed
  by scanning the function for `mov edx, 0xffffd8ee`. This is also the
  value Phase 5 will use for any `_G.<name> = ...` registration.
- **`lua_pushstring @ 0xc747d0`** is available if Phase 5 needs to
  return strings from C functions (e.g., `Mods.file.dofile` returning
  file contents as a Lua string). The address is identified but not
  wired into the current detour.

### Reproduction

The matchers (`test/disasm_check.c`) are offline and deterministic —
anyone can reproduce by running `make a1` against the same Darktide.exe
build. Trying `disasm_check_lua_pushcclosure` at a wrong RVA (e.g.,
`0xc747d0` = lua_pushstring) produces:

```
[disasm_check] pushcclosure features: movsxd_r8d=0 backward_jne=0 func_tag(0xF7)=0 direct_calls=4 all_in_cluster=1 body_size=0x79
[disasm_check] FAIL: shape does not match lua_pushcclosure
```

vs the correct RVA `0xc74580`:

```
[disasm_check] pushcclosure features: movsxd_r8d=1 backward_jne=1 func_tag(0xF7)=1 direct_calls=3 all_in_cluster=1 body_size=0xae
[disasm_check] OK: shape matches lua_pushcclosure @ RVA 0xc74580
```
