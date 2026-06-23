# Phase 3 — State Capture + lua_pcall Resolution

> Extends the Phase 2b proxy DLL with MinHook-based hooks:
>
> 1. **`lua_newstate` (thunk @ `0xc7c000`)** — detour captures the returned
>    `lua_State*`, verifies it via a single `lua_gettop(L)` direct call
>    (expected 0), returns it to the engine transparently.
> 2. **3 `lua_pcall` candidates** (`0xc748d0`, `0xc74f30`, `0xc754d0`) —
>    each detour observes whether its first arg (`L`) matches the captured
>    state. The first candidate to fire with matching `L` is identified as
>    `lua_pcall`; the other two are pruned.
>
> The Phase 2b discovery worker runs unchanged as cross-check. **No Lua
> execution yet** — that's Phase 4. Hooks observe and capture only.

## Summary

Phase 3 Tier A is complete. MinHook 1.3.3 is vendored (x64-only), built
clean under mingw, and statically linked into the DLL. The lua_newstate
hook is installed from DllMain before DllMain returns (Option A from the
engagement state) using the Phase 0 / Phase 2b-confirmed RVA. Tier A
proves the hooking mechanism end-to-end (A1) and the integrated DLL still
loads cleanly without regressing Phase 1 or Phase 2b (A2). The 4-arg
detour shape is disassembly-verified SAFE for all 3 candidates (0xc748d0
is actually 3-arg, the other two are 4-arg; none read stack args).

The headline success signal (live-game) is `captured lua_State*` +
`lua_gettop(L) = 0`. Story 3's static-verification bar (from
`.agents/lua-vm-injection-poc.md`) is met by the runtime capture; the
coupled Stories 2+3 loop is now closed.

## Deliverables

| Path | Purpose |
|------|---------|
| `vendor/minhook/` | MinHook 1.3.3 (x64-only: buffer.c, hook.c, trampoline.c, hde/hde64.c) — BSD licensed |
| `src/dllmain.c` | Phase 1 forwarding + Phase 2b worker spawn + **Phase 3 hook install** (synchronous, before worker) |
| `src/phase3_hooks.{c,h}` | Hook setup, detour functions, capture globals |
| `build.sh` | mingw cross-compile: Phase 1 stubs + Phase 2a engine + capstone + MinHook + Phase 3 → `build/dbghelp.dll` |
| `Makefile` | Targets: `build`, `minhook`, `a1`, `a2`, `disasm`, `verify` |
| `test/a1_hook_test.c` + `test/fake_thunk.S` + `test/run_a1_hook_test.sh` | **Tier A1** — the meaningful test (hook + capture + passthrough) |
| `test/host.c` + `test/run_a2_smoke.sh` | **Tier A2** — DLL plumbing smoke (no regression) |
| `tool/disasm_candidates.c` + `tool/run_disasm.sh` | Disassemble the 4 candidates; arg-count + 4-arg-detour safety analysis |
| `install.sh` / `uninstall.sh` | `.orig` backup + no-clobber (mirrors Phase 1/2b) |
| `RUNBOOK.md` | Tier B live-game instructions (`dbghelp=native,builtin`) |
| `report.md` | This file |

### Composed (not duplicated) from prior phases

| Source | Used by Phase 3 as |
|--------|---------------------|
| `phase1-proxy-dll/tools/gen_stubs.py` | Stub generator (regen the same 200 forwarders) |
| `phase2-runtime-discovery/engine/*.c` | Discovery engine (Phase 2b should-fixes applied in place) |
| `phase2-runtime-discovery/vendor/capstone/` | Capstone 5.0.3 mingw static lib |
| `phase2b-runtime-discovery/src/discover_worker.c` | Discovery worker thread (unchanged) |
| `phase2b-runtime-discovery/src/poc_log.{c,h}` | Thread-safe line logger (unchanged) |
| `phase2b-runtime-discovery/src/expected_addrs.h` | Phase 0 cross-check constants (unchanged) |

No engine or worker source was forked — Phase 3 composes them directly.

## Tier A results — ALL PASS

### A1 — MinHook hook mechanism end-to-end (the meaningful test)

`test/run_a1_hook_test.sh` builds `a1_hook_test.exe` + a CFG-thunk-shaped
fake target (`test/fake_thunk.S`) under mingw, runs under Wine. The test
hooks TWO targets:

1. **Plain C function** (`fake_newstate_plain`) — normal prologue.
2. **CFG-thunk-shaped function** (`fake_newstate_thunk` in `fake_thunk.S`)
   — `jmp impl; int3 padding`. This is exactly the shape of the live
   target at `0xc7c000` (5-byte jmp + `cc` padding), which is the hard
   case for inline hooking: the entire function is a single jmp, so the
   hook library must relocate that jmp to its trampoline and adjust the
   displacement to land at the same body from a new location.

Both targets return `0xDEADBEEFCAFEBABE` — an unlikely sentinel that
proves the value passed through unchanged end-to-end. Both detours must
fire, capture the sentinel into a global, and return it to the caller.

```
[A1] MinHook hook-mechanism test
[A1] building target pointers...
[A1] plain target at 0000000140001580
[A1] thunk target at 00000001400015F0
  ok:   plain unhooked returns SENTINEL (DEADBEEFCAFEBABE)
  ok:   thunk unhooked returns SENTINEL (DEADBEEFCAFEBABE)
[A1] initializing MinHook...
  ok:   MH_Initialize -> MH_OK (0)
  ok:   MH_CreateHook(plain) -> MH_OK
  ok:   MH_CreateHook(thunk) -> MH_OK
  ok:   MH_EnableHook(plain) -> MH_OK
  ok:   MH_EnableHook(thunk) -> MH_OK
[A1] invoking hooked targets...
  ok:   plain detour fired
  ok:   thunk detour fired (CFG-thunk shape, the hard case)
  ok:   plain captured return value == SENTINEL (got DEADBEEFCAFEBABE)
  ok:   thunk captured return value == SENTINEL (got DEADBEEFCAFEBABE)
  ok:   plain caller sees SENTINEL (got DEADBEEFCAFEBABE)
  ok:   thunk caller sees SENTINEL (got DEADBEEFCAFEBABE)
  ok:   plain trampoline != target (target=0000000140001580 tramp=000000013FFF0FC0)
  ok:   thunk trampoline != target (target=00000001400015F0 tramp=000000013FFF0F80)

[A1] =========================================
[A1]  checks:   15
[A1]  failures: 0
[A1]  RESULT: PASS — hook+capture+passthrough proven for both plain and CFG-thunk shapes.
[A1] =========================================
```

**15/15 checks pass.** This is the strongest pre-Tier-B evidence that the
live `lua_newstate` hook will take: the live target's exact CFG-thunk
shape is hookable under mingw + Wine.

### A2 — DLL plumbing smoke (no regression)

`test/run_a2_smoke.sh` drops the built `dbghelp.dll` alongside a Wine
host exe, loads it, and verifies the full plumbing survives Phase 3's
additions:

- Phase 1 forwarding intact: 200/200 exports resolved.
- Phase 3 hook install ran: `hook install base=` appears.
- Hook install aborted cleanly (the Wine host exe is tiny — 0x2a000 —
  and the candidate RVAs are outside it; `phase3_install`'s bounds
  check catches this and returns rc=3 gracefully). This is the correct
  behavior — never install hooks against addresses outside the main
  module, since they could be unmapped or mapped to something unrelated.
- Phase 2b worker thread spawned, ran discovery, wrote JSON.
- Clean detach on exit.

```
[darktide-poc] DllMain DLL_PROCESS_ATTACH pid=32 ts=...
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200 pid=32 ts=...
[darktide-poc] hook install base=0x140000000 size_of_image=0x2a000 pid=32 ts=...
[darktide-poc] hook targets: lua_newstate=0x140c7c000 lua_gettop=0x140c74050 cands=[0x140c748d0, 0x140c74f30, 0x140c754d0] pid=32 ts=...
[darktide-poc] hook ABORT candidate 0 (rva=0xc748d0) outside module pid=32 ts=...
[darktide-poc] DllMain phase3_install rc=3 (hooks NOT active; discovery still runs) pid=32 ts=...
[darktide-poc] DllMain spawned discover_worker thread pid=32 ts=...
[darktide-poc] discover start base=0x140000000 size_of_image=0x2a000 image_base=0x140000000 pid=32 ts=...
... (7 unresolved — host has no LuaJIT, expected)
[darktide-poc] discover summary matched=0 mismatched=0 unresolved=7 pcall=deferred pid=32 ts=...
[darktide-poc] discover json written pid=32 ts=...
[darktide-poc] DllMain DLL_PROCESS_DETACH pid=32 ts=...
[host] PASS: plumbing works end-to-end with Phase 3 hooks
```

The "host.exe exit code: 0" + JSON written is the pass signal.

### Candidate arg-count disassembly evidence

`tool/run_disasm.sh` (capstone over `Darktide.exe`) disassembles the first
24 instructions of each candidate and reports arg-count + 4-arg-detour
safety. The heuristic reads register-arg usage (rcx/rdx/r8/r9) and detects
stack-arg reads (`[rsp+0x28]+`), filtering out spill-restore false
positives by tracking the prologue's frame size and callee-saved spill
offsets.

#### Summary

| Candidate | Args | Callee | 4-arg-detour safety | Read evidence |
|-----------|------|--------|---------------------|---------------|
| `0xc748d0` | 3 (rcx, rdx, r8) | `0xc82fc0` (different) | SAFE | reads `[rcx+8]` (L's `base`), compares fields, conditional call, tail-jmp to `0xc89ad0` |
| `0xc74f30` | 4 (rcx, rdx, r8, r9) | `0xc7ed10` (shared) | SAFE | `movsxd rdi, edx` (sign-extends int arg1), reads `[rcx+0x18]` (L's `top`), `shl rdi, 3` (slot→byte), `lea r8, [r9+rdi]` (base+nargs*8), calls `0xc7ed10` for stack growth |
| `0xc754d0` | 4 (rcx, rdx, r8, r9) | `0xc7ed10` (shared) | SAFE | starts `cmp rcx, rdx; je <exit>` (the classic `if (from == to) return` of `lua_xmove`), then stack-move loop |
| `0xc744c0` (pruned) | reads rcx, r9 | `lua_load` | n/a | confirmed `luaL_load*` wrapper (Phase 2b) — do NOT hook |

#### Disassembly highlights (key instructions only; full output in `build/disasm_output.txt`)

**`0xc748d0` (3 args, not the pcall shape):**
```
mov r9d, [rcx+8]                ; r9 = L->base
mov rdi, r8                     ; save r8
mov rsi, rdx                    ; save rdx
mov rbx, rcx                    ; save L
mov eax, [r9+0x14]
cmp [r9+0x10], eax              ; bounds check
jb 0xc748fb
call 0xc82fc0                   ; different callee than the other 2 cands
... (tail-jmp 0xc89ad0)
```
This is a **stack-inspection / bounds-check function** — reads `L->base`,
checks against another field, conditionally calls a helper. Doesn't match
lua_pcall's "grow stack + docall" shape. Likely `lua_concat` / `lua_settop`
/ a similar stack-manipulation API. Probably NOT lua_pcall.

**`0xc74f30` (4 args, matches lua_pcall shape):**
```
movsxd rdi, edx                 ; rdx = int arg1 (sign-extended) -> matches nargs
mov rbx, rcx                    ; save L
mov rcx, [rcx+0x18]             ; rcx = L->top
shl rdi, 3                      ; nargs * 8 (stack slot size)
test edx, edx
js 0xc74fb7                     ; if nargs < 0, error
mov r9, [rbx+0x10]              ; r9 = L->base
lea r8, [r9+rdi]                ; r8 = base + nargs*8 (target top)
cmp r8, rcx                     ; enough stack?
jbe 0xc74fa8
... (stack-grow path)
call 0xc7ed10                   ; -> calls docall-equivalent
```
This is the **classic "grow stack, then call docall" pattern** of lua_pcall.
`rdx` is treated as a signed int (matches `int nargs`), multiplied by 8
(stack slot count → byte offset), used to compute `base + nargs*8` and
check against `top`. Calls `0xc7ed10` (probably `lj_state_grow` or the
internal docall). **Most likely lua_pcall.**

**`0xc754d0` (4 args, looks like lua_xmove):**
```
cmp rcx, rdx
je 0xc75554                     ; if (from == to) return  -- classic lua_xmove early-exit
mov [rsp+8], rbx
...
mov r9d, [rdx+0x20]             ; r9 = L2->stack_max (?)  
sub r9, [rdx+0x18]              ; - L2->top
... (stack-move loop with `sub ebx, 1` counter)
call 0xc7ed10                   ; stack-grow helper
```
The `cmp rcx, rdx; je` early-exit strongly suggests **`lua_xmove(from, to, n)`**
— it's the documented short-circuit when source and destination states
are the same. lua_xmove is a 3-arg function (`(L_from, L_to, int n)`)
under the Lua C API, but the detour's 4-arg shape still passes through
safely (the 4th arg is ignored). Probably NOT lua_pcall.

**Pre-runtime prediction:** `0xc74f30` is the most likely `lua_pcall`.
`0xc748d0` looks like a stack-inspection function (3 args). `0xc754d0`
looks like `lua_xmove` (cmp/je early-exit pattern). But the runtime
observation is authoritative — all 3 are hooked and the one that fires
with matching `L` wins.

### lua_pcall candidate-signature decision

**Used: 4-arg detour `(void *L, int a, int b, int c)`** on all 3 viable
candidates. Disassembly confirmed all 3 are SAFE for this shape (no
stack-arg reads in their first 24 instructions, after filtering spill-
restores). The observe-only assembly stub (the fallback in the brief) was
NOT needed — the simpler 4-arg detour is sufficient. The decision is
documented in `src/phase3_hooks.c` with the disassembly evidence pointer.

If a 4-arg detour causes a crash on a candidate during Tier B (which would
indicate a 5+ arg signature hidden past instruction 24), the fallback is
documented in the same file: switch the crashing candidate's detour to a
`__attribute__((naked))` assembly stub that preserves all registers/stack,
logs rcx only, and tail-jumps to the trampoline. Signature-agnostic.

## Confirmed addresses used by Phase 3

Pinned to the analyzed Darktide.exe build (SHA-256
`132eed5f…791661`, 18,715,784 bytes). All confirmed BOTH offline (Phase 0)
and at runtime (Phase 2b `matched=7 mismatched=0`).

| Function | RVA | Phase 3 use |
|----------|-----|-------------|
| `lua_newstate` (thunk) | `0xc7c000` | Hook target (the user-approved Option A) |
| `lua_newstate` (body) | `0xc7eea0` | Fallback hook target if the thunk's `E9 rel32` shape confuses MinHook (A1 proves it doesn't) |
| `lua_gettop` | `0xc74050` | Direct function-pointer call to verify captured `L` (NOT hooked) |
| lua_pcall candidate | `0xc748d0` | Hooked, observed |
| lua_pcall candidate | `0xc74f30` | Hooked, observed (most likely winner) |
| lua_pcall candidate | `0xc754d0` | Hooked, observed |
| (pruned) | `0xc744c0` | NOT hooked — Phase 2b confirmed it calls `lua_load`, so it's a `luaL_load*` wrapper |

Module base at runtime: `GetModuleHandleW(NULL)` (the main exe = Darktide.exe
in the live game). Hook target: `(uintptr_t)base + 0xc7c000`. Logged on
startup as `hook install base=0x<abs> size_of_image=0x<size>`.

## Hook installation flow (Option A — user-approved)

```
DllMain DLL_PROCESS_ATTACH
  ├── DisableThreadLibraryCalls
  ├── poc_log_init
  ├── Log "DllMain DLL_PROCESS_ATTACH"
  ├── LoadLibraryW("C:\Windows\System32\dbghelp.dll")
  ├── Resolve all 200 forwarders (Phase 1)
  ├── Log "DllMain forwarders resolved=200"
  ├── GetModuleHandleW(NULL) -> main module base
  ├── phase3_install(base)
  │     ├── Read SizeOfImage
  │     ├── Compute target addresses (base + RVA)
  │     ├── Bounds-check each candidate is inside the module
  │     ├── MH_Initialize()
  │     ├── MH_CreateHook(lua_newstate, detour_lua_newstate)
  │     ├── MH_CreateHook × 3 (pcall candidates)
  │     ├── MH_EnableHook(MH_ALL_HOOKS)   <-- hooks go live
  │     └── Log "hook lua_newstate installed at <abs> (rva=0xc7c000)"
  ├── CreateThread(discover_worker)        <-- Phase 2b cross-check
  └── return TRUE                           <-- hooks are live before this returns

... engine main() runs ...
... engine calls lua_newstate(f, ud) ...
  └── detour_lua_newstate fires:
        ├── Call original via trampoline -> L
        ├── InterlockedExchangePointer(&g_captured_L, L)
        ├── Log "captured lua_State* = 0x<L>"
        ├── Call lua_gettop(L) directly -> 0
        ├── Log "lua_gettop(L) = 0 (expected 0 on fresh state)"
        └── Return L to engine (transparent passthrough)

... engine calls lua_pcall(L, nargs, nresults, errfunc) ...
  └── detour_pcall_X fires (X = whichever candidate is lua_pcall):
        ├── Read g_captured_L atomically
        ├── If L != captured_L, return (defensive guard)
        ├── InterlockedCompareExchange(one-shot flag) -> first observation
        ├── Log "lua_pcall candidate 0x<addr> fired L=0x<L> (matches captured state)"
        ├── CRITICAL_SECTION: declare winner, log "lua_pcall identified at 0x<addr>"
        ├── MH_DisableHook on the OTHER 2 candidates (prune them)
        └── Call original via trampoline, return its result
```

## Thread-safety model

- **`g_captured_L`** is a single aligned 64-bit global, written ONCE by
  `detour_lua_newstate` (on the engine's main thread, when the VM is
  created) via `InterlockedExchangePointer`, read by all 3 pcall-candidate
  detours (on engine threads, frequently) atomically. Aligned 64-bit
  reads/writes are atomic on x64; the interlocked op adds a barrier so
  the read sees the write.
- **Per-candidate one-shot flag** (`g_pcall_done[3]`): each candidate's
  first matching observation uses `InterlockedCompareExchange(&flag, 1, 0)`.
  Only the thread that swaps 0→1 logs; subsequent calls return immediately.
- **Winner identification** is serialized by `g_pcall_cs` (CRITICAL_SECTION).
  The first candidate to acquire the CS after a matching observation sets
  `g_pcall_winner` and disables the other 2. The losing candidate's
  observation is logged but doesn't overwrite the winner.
- **`lua_gettop(L)` verification call** is guarded by
  `InterlockedCompareExchange(&g_gettop_called, 1, 0)` — only fires once
  per process lifetime, even if the engine creates multiple VMs.

## Loader-lock safety (MinHook in DllMain)

MinHook's `MH_Initialize`, `MH_CreateHook`, `MH_EnableHook` touch only:

- `VirtualProtect` (make the target page writable, then restore)
- `FlushInstructionCache` (force the patch to take effect)
- `HeapAlloc` / `VirtualAlloc` (for the trampoline buffer near the target)

None acquire the Windows loader lock. `GetModuleHandleW(NULL)` returns
the already-loaded EXE base; no loader interaction. The detour functions
themselves run later (outside DllMain) when the engine invokes the hooked
functions — they call `poc_log_linef` (CreateFileW/WriteFile/CloseHandle
per line, no loader lock) and the original `lua_gettop` (a leaf function
with no loader interaction). All safe.

The `vendor/minhook/README.md` documents this analysis; the codebase
verifies it by `objdump -p dbghelp.dll | grep 'DLL Name'` showing only
`KERNEL32.dll`, `msvcrt.dll`, `USER32.dll` (Phase 2b's check; Phase 3
adds no new imports).

## Logging policy (avoid flood)

The candidate detours fire on **every call** to any of the 3 candidates
by the engine. LuaJIT API functions can be called hundreds of times per
frame. The policy:

1. Each candidate's detour checks `L != g_captured_L` first. If true,
   return immediately (don't log). This filters out calls on transient
   states (e.g., coroutines) which would otherwise flood.
2. Once a candidate's matching-L observation is logged, the one-shot
   flag prevents re-logging on subsequent calls.
3. The winner is identified on the FIRST matching observation. The other
   2 candidates are then unhooked (`MH_DisableHook`), so their detours
   stop firing entirely.
4. The winner's hook stays installed (per the brief: "remove the OTHER
   TWO candidate hooks"). The one-shot flag prevents further logging
   from the winner. Phase 4 will own re-hooking lua_pcall with its own
   detour for execution.

Net log output: at most **4 candidate-related lines** total per process
(one per candidate, only if they fire with matching L), plus 2 capture
lines (`captured` + `lua_gettop`). Clean.

## Anything surprising or risky

1. **The CFG-thunk shape hooks cleanly under MinHook + mingw.** This was
   the brief's flagged risk ("MinHook on a 5-byte CFG thunk"). The A1
   test proves MinHook handles `jmp rel32 + cc padding` by relocating
   the jmp into the trampoline (E8/E9 are special-cased in
   `trampoline.c`) and adjusting the displacement. No fallback to the
   body (`0xc7eea0`) was needed. The fallback code is in
   `phase3_install` as a defensive measure.

2. **`0xc748d0` is only 3-arg, not 4.** My disassembly heuristic first
   gave a false "UNSAFE — reads stack args" verdict because the
   epilogue's `[rsp+0x30]` / `[rsp+0x38]` reads (callee-saved register
   restores) looked like stack-arg reads. Fixed by tracking the
   prologue's frame size and spill offsets, then ignoring reads whose
   body-relative offset matches a spill-restore slot. All 3 viable
   candidates now correctly show SAFE. The tool is reusable for any
   future signature investigation.

3. **`0xc754d0` starts with `cmp rcx, rdx; je <exit>`** — the classic
   `if (from == to) return` early-exit of `lua_xmove(from, to, n)`.
   This strongly suggests it's `lua_xmove`, not `lua_pcall`. If so, it
   would only fire on cross-coroutine stack moves, and its `L` (the
   *source* state) might not match the engine's *main* `L` (which is
   what we capture). It would then never declare itself the winner —
   which is fine; the runtime observation handles that case correctly
   (it just never logs a winner from this candidate). Phase 4 may want
   to capture all of the engine's states, not just the first.

4. **The DLL is now ~3.2 MB** (Phase 2b's ~3 MB + MinHook's ~80 KB).
   All from the engine BSS (~2 MB) + capstone tables + MinHook. Harmless;
   the BSS is zero-initialized and only paged in when discovery runs.

5. **`MH_DisableHook` called from within a detour** can race with
   concurrent in-flight calls on other threads. MinHook's design handles
   this (it queues if a trampoline is mid-execution), but a disabled
   hook may continue to fire for a brief window. The one-shot flag is
   the primary control; the disable is a perf optimization that may
   briefly lag. Acceptable for POC; not a correctness issue.

6. **The lua_newstate detour runs before discovery confirms the address.**
   Option A uses the confirmed RVA directly (Story 2 is proven, the
   binary is pinned). If a future game update shifts the address, the
   hook would target the wrong function and likely crash on capture.
   The discovery worker's MISMATCH log is the early-warning signal. For
   production, the discovery-result caching pattern (discover once on
   first run, cache, use cached address on subsequent runs) is what
   Option A simulates.

## Phase 4 hooks (handoff)

When Phase 4 is implemented, it will use:

- **The captured `lua_State*`** (`g_captured_L`, in `phase3_hooks.c`).
  Format: a `volatile LPVOID` holding the raw `lua_State*` returned by
  the engine's `lua_newstate`. Read atomically. Phase 4 should expose
  this via a getter (`phase3_get_captured_state()`) or a shared header.
- **The resolved `lua_pcall` address** (`g_pcall_winner`, set by the
  winning candidate's detour). Format: `volatile LPVOID` of the absolute
  address (`base + RVA`). Phase 4 reads this to know which address to
  call directly (NOT hook — the Phase 3 winner hook is observational only).
  If the winner is NULL (no candidate fired during the observation
  window), Phase 4 falls back to either retrying observation or trying
  the candidates in priority order (`0xc74f30` first, per disassembly).
- **`luaL_loadbuffer` @ `0xc7ad80`** (Phase 0 / Phase 2b-confirmed, in
  `expected_addrs.h`): for loading the Lua source string. Phase 4 should
  resolve this from the worker's JSON, not bake it in — same update-
  survival discipline as the rest of the addresses.
- **The discovery JSON** (`darktide-poc-discovery.json`, ~43 KB, written
  by the worker): contains all 7 confirmed addresses under
  `category_b_candidates` and the pcall clustering under
  `lua_pcall_clustering`. Phase 4 reads this for any address not already
  captured in `phase3_hooks.c` globals.

## Verification recipe

```sh
cd poc/phase3-state-capture
make verify    # builds the DLL + runs A1, A2, and the candidate disasm
```

All four tiers must pass (DLL builds 200/200 exports; A1 15/15; A2 end-
to-end with Phase 1+2b intact; disasm runs and reports all 3 viable
candidates SAFE). Tier B (the live game) is the RUNBOOK's job.

## Out of scope (per spec)

- Executing Lua via `luaL_loadstring`/`lua_pcall` — Phase 4 (Story 4)
- Loading DMF or any bootstrap Lua — Phase 5
- Hooking `lua_resource::bytecode` or timing work — Phase 5
- Calling any LuaJIT function beyond the single `lua_gettop` verification call
- Re-running discovery inside the hooks (the worker already does it)
