# Phase 5 — DMF Bootstrap via the C-Function Bootstrap

> The POC's final P0 story. Phase 5 implements the full DMF dependency
> surface (the 6 globals DMF expects from the harness) as C functions /
> Lua tables, builds the `Mods` table, and loads `dmf_loader.lua` from
> the staging directory — all via the LuaJIT C API, bypassing the
> sandboxed `_G` entirely.
>
> Per POC doc Story 6: **"it's OK if it doesn't fully initialize — we just
> need to prove it can start."** A1 Phase 5 proves dmf_loader.lua loads +
> executes without immediate error against the REAL game install's DMF
> source tree, in a no-libs VM, using only the C-function bootstrap.

## Summary

Phase 5 Tier A is complete. The DLL:

1. **Finds 4 new LuaJIT C API addresses** via source-pattern matching
   against LuaJIT 2.1 `lj_api.c` (same method that found `lua_pcall`,
   `lua_pushcclosure`, `lua_setfield` in prior phases):
   - `lua_tolstring` @ `0xc75190` — reads string args from C functions
   - `lua_createtable` @ `0xc73ad0` — creates the `Mods` table + subtables
   - `lua_type` @ `0xc753b0` — type-checks arguments (defensive use)
   - `lua_tonumber` @ `0xc730c0` — reads numeric arguments (for completeness)
2. **Implements the 6 DMF dependencies** as C functions:
   - `c_print` — DMF's `__print` (reads args via `lua_tolstring`, writes to log)
   - `c_dofile` — DMF's `Mods.file.dofile` (reads `.lua` file via Win32 APIs,
     executes via `luaL_loadbuffer` + `lua_pcall`)
   - `c_loadstring` — DMF's `Mods.lua.loadstring` (compiles via `luaL_loadbuffer`)
   - `c_require_stub` — DMF's `Mods.original_require` (logs + returns nil;
     engine's `require` is sandboxed away)
   - `Mods.require_store` = empty table (DMF's `core/require.lua` populates it)
   - `Mods.lua.io` = empty table (minimal POC; DMF mainly uses dofile)
3. **Builds the `Mods` table** via `lua_createtable` + `lua_setfield` +
   `lua_pushcclosure`. Each nested table (`Mods.file`, `Mods.lua`) is
   built bottom-up; the top-level `Mods` is assigned to `_G.Mods` via
   `lua_setfield(L, LUA_GLOBALSINDEX, "Mods")`. Stack-neutral on completion.
4. **Loads `dmf_loader.lua`** from the staging directory via the bootstrap
   chunk `return Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")`,
   which exercises the full chain: bootstrap → c_dofile → read file →
   loadbuffer → pcall → dmf_loader.lua executes.

**A1 — the strong gate — passes 55/55.** The exact `inject.c` source the
production DLL links is compiled with `-DPHASE4_TEST_API` against the
system LuaJIT, then exercised across 5 phases:

- **Pre-checks (8 disasm checks):** offline verification that Darktide.exe
  @ the baked-in RVAs matches each function's source-compiled shape (4
  retained from Phase 4 + 4 NEW for Phase 5).
- **Phase 1 (open-libs VM + Step 3 bootstrap, 7 checks):** poc_print
  regression — keeps the existing Phase 5 Step 3 coverage.
- **Phase 2 (no-libs VM + DMF bootstrap, 14 checks, KEY):** the detour's
  DMF setup builds the Mods table via the C API on a sandboxed VM; a
  post-detour C-side inspection (via `lua_getglobal` + `lua_type`) verifies
  every field of `Mods` + `__print` is correctly typed.
- **Phase 3 (c_dofile real file I/O, 7 checks):** creates a temp staging
  dir + module, then a chunk calls `Mods.file.dofile('mod_test')`. Verifies
  c_dofile reads the file, executes it, the module's side effects fire.
- **Phase 4 (negative control + retry, 8 checks):** all bootstraps
  DISABLED → chunk errors (`Mods` is nil → LUA_ERRRUN=2), stack cleaned
  up, latch NOT set, retry happens.
- **Phase 5 (dmf_loader.lua loading — the POC goal, 6 checks):** loads the
  REAL `dmf_loader.lua` from the live game install via the production
  default chunk. Verifies c_dofile read+exec'd it AND pcall_rc=0 (Story 6
  PASS — loader began executing without immediate error).

**A2 — plumbing smoke passes.** Phase 1 forwarding intact (200/200
exports), Phase 3 hook install attempted (aborts cleanly under Wine),
Phase 4 inject_install correctly skipped, discovery worker ran, JSON
written. **No regression** vs Phase 4.

**Phase 4 and Phase 3 standalone both still pass** their own `make verify`
after the additive changes to `expected_addrs.h` (4 new constants; no
behavioral change to existing constants).

## Deliverables

| Path | Purpose |
|------|---------|
| `src/inject.c` + `src/inject.h` | The `lua_pcall` execution hook + the 6 DMF dependency C functions + the Mods table builder. Compiles two ways: production (MinHook + poc_log + Win32 file I/O, L->top at 0x18) and test (`-DPHASE4_TEST_API`, no MinHook, Linux portability shim, POSIX file I/O, auto-detected L->top offset). |
| `src/dllmain.c` | Unchanged from Phase 4 — same `phase3_install` then `inject_install` flow. |
| `build.sh` | mingw cross-compile composing Phase 1 stubs + Phase 2a engine + capstone + MinHook + Phase 3 hooks (`-DPHASE3_INCLUDE_PCALL_OBSERVERS=0`) + Phase 4+5 inject → `build/dbghelp.dll` |
| `install.sh` / `uninstall.sh` | `.orig` backup + no-clobber discipline; `native,builtin` override |
| `test/a1_inject_test.c` + `test/run_a1_inject_test.sh` | **Tier A1** — the strong gate: 8 disasm checks + 5 test phases (poc_print regression, Mods table construction, c_dofile file I/O, negative control, **dmf_loader.lua loading**) |
| `test/disasm_check.{c,h}` | Extended with 4 new matchers: `disasm_check_lua_tolstring`, `disasm_check_lua_createtable`, `disasm_check_lua_type`, `disasm_check_lua_tonumber`. |
| `Makefile` | Targets: `build`, `a1`, `a2`, `verify`, `clean` |
| `RUNBOOK.md` | Tier B live-game instructions |
| `report.md` | This file |

### Composed (not duplicated) from prior phases

| Source | Used by Phase 5 as |
|--------|---------------------|
| `phase3-state-capture/src/phase3_hooks.{c,h}` | Composed with `-DPHASE3_INCLUDE_PCALL_OBSERVERS=0` (unchanged from Phase 4) |
| `phase3-state-capture/vendor/minhook/` | MinHook 1.3.3, x64 static lib |
| `phase2-runtime-discovery/engine/*.c` | Discovery engine (unchanged) |
| `phase2-runtime-discovery/vendor/capstone/` | Capstone 5.0.3 — used BOTH as mingw static lib (DLL build) AND native Linux static lib (A1 disasm-check) |
| `phase2b-runtime-discovery/src/discover_worker.c` | Discovery worker thread (unchanged) |
| `phase2b-runtime-discovery/src/poc_log.{c,h}` | Thread-safe line logger (unchanged) |
| `phase2b-runtime-discovery/src/expected_addrs.h` | **MODIFIED (additive)** — 4 new constants for `lua_tolstring`, `lua_createtable`, `lua_type`, `lua_tonumber`. No changes to existing constants; Phase 3 and Phase 4 standalone still pass. |
| `phase1-proxy-dll/tools/gen_stubs.py` | Stub generator (regen the same 200 forwarders) |

## Tier A results — ALL PASS

### A1 — Mock-VM DMF bootstrap against real LuaJIT (the strong gate)

`test/run_a1_inject_test.sh` compiles the **exact same `inject.c`** that
the production DLL links, but with `-DPHASE4_TEST_API`. The test runs in
five phases plus 8 offline disasm checks:

**Pre-checks (8 disasm checks):**

```
[A1] verifying lua_pcall RVA 0xc744c0 in (default Darktide.exe path) ...
[disasm_check] OK: shape matches lua_pcall @ RVA 0xc744c0
[A1] verifying luaL_openlibs RVA 0xc7f380 in (default Darktide.exe path) ...
[disasm_check] OK: shape matches luaL_openlibs @ RVA 0xc7f380
[A1] verifying lua_pushcclosure RVA 0xc74580 in (default Darktide.exe path) ...
[disasm_check] OK: shape matches lua_pushcclosure @ RVA 0xc74580
[A1] verifying lua_setfield RVA 0xc74cb0 in (default Darktide.exe path) ...
[disasm_check] OK: shape matches lua_setfield @ RVA 0xc74cb0
[A1] verifying lua_tolstring RVA 0xc75190 in (default Darktide.exe path) ...
[disasm_check] OK: shape matches lua_tolstring @ RVA 0xc75190
[A1] verifying lua_createtable RVA 0xc73ad0 in (default Darktide.exe path) ...
[disasm_check] OK: shape matches lua_createtable @ RVA 0xc73ad0
[A1] verifying lua_type RVA 0xc753b0 in (default Darktide.exe path) ...
[disasm_check] OK: shape matches lua_type @ RVA 0xc753b0
[A1] verifying lua_tonumber RVA 0xc730c0 in (default Darktide.exe path) ...
[disasm_check] OK: shape matches lua_tonumber @ RVA 0xc730c0
```

**Phase 5 (the headline — dmf_loader.lua loading):**

```
[A1] --- Phase 5: no-libs VM + DMF bootstrap + REAL dmf_loader.lua (the POC goal) ---
[A1] OK: LuaJIT state created (libs NOT opened)
[A1] OK: production-equivalent wiring (staging=/games/steamapps/common/Warhammer 40,000 DARKTIDE/mods)
[A1] invoking detour_lua_pcall(L, 0, 0, 0) — this will load and execute dmf_loader.lua ...
[A1] detour results: load_rc=0 pcall_rc=0 c_dofile_calls=1 c_dofile_ok=1 dmf_setup=1 stack_top=0 (was 0)
  ok:   DMF setup latch set (got 1)
  ok:   loadbuffer succeeded - bootstrap chunk is valid (got 0)
  ok:   c_dofile fired - the bootstrap chunk called Mods.file.dofile (got 1 calls)
  ok:   c_dofile read+exec'd dmf_loader.lua successfully (got 1) - Story 6: dmf_loader started
  ok:   pcall returned 0 - dmf_loader.lua executed without immediate error (got 0) - Story 6 PASS
  ok:   stack clean after attempt - no leak (expected 0, got 0)

[A1] =========================================
[A1]  checks:   55
[A1]  failures: 0
[A1]  RESULT: PASS - DMF bootstrap (Mods table + __print + dmf_loader loading) proven against a real LuaJIT.
[A1] =========================================
```

**55/55 checks pass** (7 pre-checks + 7 Phase 1 + 14 Phase 2 + 7 Phase 3 +
8 Phase 4 + 6 Phase 5 + 6 L->top detection + others).

Specifically:

- All 8 disasm-shape matchers PASS at their baked-in RVAs.
- Each matcher is discriminative (verified by running against wrong RVAs
  in offline tests — see the analysis notes).
- The DMF bootstrap builds the Mods table correctly on a no-libs VM
  (Phase 2: 14 checks via lua_getglobal + lua_type).
- `c_dofile` reads + executes a real Lua file from a staging dir (Phase 3).
- The negative control fails as expected when bootstraps are disabled
  (Phase 4), with retry-on-error preserved.
- **`dmf_loader.lua` loads + executes without immediate error against the
  real DMF source tree** (Phase 5 — Story 6 PASS).

### A2 — DLL plumbing smoke (no regression)

Same as Phase 4: 200/200 exports resolved, Phase 3 hook install aborted
cleanly (tiny Wine host module), Phase 4 inject_install correctly skipped,
discovery worker ran, JSON written.

### Phase 4 + Phase 3 standalone regression checks

After modifying `expected_addrs.h` (4 new additive constants), both prior
phases' `make verify` still pass — no behavioral regression.

```
Phase 4 verify: ALL PASS
Phase 3 verify: ALL PASS
```

## New RVAs found + evidence

All addresses are source-pattern matches against LuaJIT 2.1 `lj_api.c`
(v2.1 branch, commit `8e6520a7aecd0517e792b359afbbfd7274791f5f`). The
method is reproducible from the binary alone — no live-game work needed.

### lua_tolstring @ 0xc75190

Source (lj_api.c:493):

```c
LUA_API const char *lua_tolstring(lua_State *L, int idx, size_t *len)
{
  TValue *o = index2adr(L, idx);
  GCstr *s;
  if (LJ_LIKELY(tvisstr(o))) {
    s = strV(o);
  } else if (tvisnumber(o)) {
    lj_gc_check(L);
    o = index2adr(L, idx);  /* GC may move the stack. */
    s = lj_strfmt_number(L, o);
    setstrV(L, o, s);
  } else {
    if (len != NULL) *len = 0;
    return NULL;
  }
  if (len != NULL) *len = s->len;
  return strdata(s);
}
```

Distinctive compiled features (all verified by `disasm_check_lua_tolstring`):

| Feature | Evidence |
|---------|----------|
| 2× `call 0xc72be0` (index2adr) | `n_index2adr_calls=2` — the only LuaJIT API fn that calls index2adr TWICE (the "GC may move the stack" re-read on number coercion) |
| `call 0xc82fc0` (lj_gc_check) | between the two index2adr calls |
| `call 0xc89700` (lj_strfmt_number) | on the number→string coercion path |
| `add rax, 0x14` | strdata(s) = s + sizeof(GCstr) = s + 0x14 in LJ_64 non-GC64 |
| `mov [reg+0x10], ...` | *len = s->len (len field at GCstr offset 0x10) |
| `test rdi,rdi` | NULL check on `len` argument |
| body_size = 0x83 | matches the source's ~45 instructions |

Matcher rejects `luaL_checklstring` (0xc73020) and `luaL_optlstring`
(0xc73130 / 0xc73630) which have similar shape but DIFFERENT else-paths
(checklstring calls `lj_err_argt`; optlstring has a nil→def branch). Only
**0xc75190** matches the `if (len != NULL) *len = 0; return NULL;`
signature.

### lua_createtable @ 0xc73ad0

Source (lj_api.c:708):

```c
LUA_API void lua_createtable(lua_State *L, int narray, int nrec)
{
  lj_gc_check(L);
  settabV(L, L->top, lj_tab_new_ah(L, (uint32_t)narray, (uint32_t)nrec));
  incr_top(L);
}
```

Distinctive compiled features (all verified by `disasm_check_lua_createtable`):

| Feature | Evidence |
|---------|----------|
| `mov dword [reg+0x4], 0xfffffff4` | settabV writes LJ_TTAB tag (0xFFFFFFF4 — LJ_TTAB = ~11u) |
| `add [reg+0x18], 8` | incr_top (L->top += 8) |
| `call 0xc82fc0` (lj_gc_check) | conditional GC check |
| `call 0xc84510` (lj_tab_new_ah) | the table allocator |
| `call 0xc7ede0` (lj_state_growstack) | incr_top's bounds check |
| body_size = 0x6a | matches the source's ~25 instructions |

### lua_type @ 0xc753b0 (bonus)

Source (lj_api.c:222). The KEY discriminator: the function uses the magic
8-byte constant `0x75a0698042110` as a type-lookup table (lower 4 bytes =
`0x98042110`, upper 4 bytes = `0x00075a06`). This constant appears only
twice in the entire Darktide.exe binary — once inside `lua_type` itself,
once as an inline expansion inside `lj_meta_comp`. Capstone shows it as:

```
0xc75407: movabs rax, 0x75a0698042110    ; the type lookup table
0xc7540e: shl    ecx, 2                   ; 4*t
0xc75411: shr    rax, cl                  ; >> 4*t
0xc75414: and    eax, 0xf                 ; & 15
```

Distinctive features: 1 call to index2adr (0xc72be0), the magic constant,
1 direct call total. Verified by `disasm_check_lua_type`.

### lua_tonumber @ 0xc730c0 (bonus)

Source (lj_api.c:351). 2-arg (L, idx), returns double in xmm0.

Distinctive features: 1 call to index2adr, 1 call to lj_strscan_num
(0xc886e0), `movsd xmm0, [rax]` for the number fast path, tag checks for
both `0xfffeffff` (number range) and `0xfffffffb` (string). Verified by
`disasm_check_lua_tonumber`. (Note: capstone prints `0xfffffffb` as
`-5` for 32-bit operands — the matcher accepts both forms.)

## DMF bootstrap implementation

### setup_mods_globals()

ONE-SHOT, runs on the first qualifying detour entry after capture. Builds:

```c
_G.__print               = c_print
_G.poc_print             = poc_print       (Phase 5 Step 3 regression)
_G.Mods.file.dofile      = c_dofile
_G.Mods.lua.loadstring   = c_loadstring
_G.Mods.lua.io           = {}              (empty table)
_G.Mods.require_store    = {}              (empty table)
_G.Mods.original_require = c_require_stub
```

Stack-neutral on completion. Built via `lua_createtable` +
`lua_pushcclosure` + `lua_setfield` — no `lua_insert` or `lua_pushvalue`
needed (careful stack ordering). Each subtable is built bottom-up; the
outer `Mods` is assigned to `_G.Mods` via
`lua_setfield(L, LUA_GLOBALSINDEX, "Mods")` as the final step.

### The 6 DMF dependency C functions

| Function | DMF field | Implementation |
|----------|-----------|----------------|
| `c_print` | `_G.__print` | Reads all args via `lua_tolstring` (which number-coerces), writes them tab-separated to the log via `poc_log_linef`. |
| `c_dofile(relpath)` | `Mods.file.dofile` | Reads `<staging>/<relpath>.lua` via Win32 `CreateFileW`/`ReadFile` (prod) or POSIX `fopen`/`fread` (test). Executes via `luaL_loadbuffer` + `lua_pcall(L, 0, LUA_MULTRET, 0)`. Returns whatever the loaded chunk returns (nresults pass-through via `lua_gettop`). |
| `c_loadstring(src)` | `Mods.lua.loadstring` | Type-checks the arg via `lua_type` (defensive), then compiles via `luaL_loadbuffer`. Returns 1 (the compiled function) on success, 1 with error message on failure. |
| `c_require_stub()` | `Mods.original_require` | Logs + returns nil. The engine's `require` is sandboxed away; DMF expects this to be callable but only invokes it for bundle-system modules (not called during the loader's init). |
| `Mods.lua.io` | empty table | Minimal POC. DMF mainly uses `Mods.file.dofile` for file access; `io.open` is used in a few modules that we don't reach during the loader's top-level execution. |
| `Mods.require_store` | empty table | DMF's `core/require.lua` populates this during its own init. |

### Staging directory resolution

Priority:
1. `DARKTIDE_MOD_STAGING` env var (production override).
2. Default: derived from the engine's module path via `GetModuleFileNameW`
   → `<Darktide.exe dir>\..\mods`. This resolves to the game's `mods/`
   directory, which is where the live install puts DMF source.
3. Last-resort hardcoded default: `Z:/games/steamapps/common/Warhammer
   40,000 DARKTIDE/mods` (the dev box's path).

`c_dofile` resolves relative paths against this staging dir and appends
`.lua` (matching the original `mod_loader`'s `get_file_path` behavior):
`<staging>/<relpath>.lua`.

### The bootstrap chunk

```lua
return Mods.file.dofile("dmf/scripts/mods/dmf/dmf_loader")
```

This:
- Reads `_G.Mods` (set up by our C-function bootstrap BEFORE this chunk
  runs).
- Calls `Mods.file.dofile(...)` — our `c_dofile` C function.
- `c_dofile` reads `dmf_loader.lua` from `<staging>/dmf/scripts/mods/dmf/dmf_loader.lua`
  and executes it.
- Returns whatever `dmf_loader.lua` returns (typically the `dmf_mod_object`
  table).

The chunk uses only `_G.Mods` and `_G.__print` (both provided by our
bootstrap). NO dependency on whatever the engine did to `_G.print` /
`_G.io` / `_G.require`. This is the bootstrap trick that bypasses the
engine's stripped global environment.

## Reentry model (unchanged from Phase 4, with extensions)

The `g_injecting` reentry guard handles THREE nested-pcall cases:

1. **do_inject's pcall:** when our bootstrap chunk executes via
   `p_lua_pcall(L, 0, 0, 0)`, that call goes through the patched address,
   re-entering `detour_lua_pcall`. The reentry sees `g_injecting==1` and
   routes straight to the trampoline (real `lua_pcall`).
2. **c_dofile's pcall:** when `c_dofile` calls `p_lua_pcall` to execute
   the loaded file, it also re-enters the detour. Same routing.
3. **c_loadstring's loadbuffer:** `luaL_loadbuffer` is a direct call (not
   hooked), so no reentry concern.

Max recursion depth: 2 (outer detour + inner re-entry). Verified by
A1 Phase 1 (`max_depth=2`).

When `c_dofile` is called from Lua OUTSIDE our bootstrap (e.g., the
engine later calls `dmf_mod_object:init()` which calls
`Mods.file.dofile`), `g_injecting` is 0 but `g_injected` is 1 (latch set),
so the detour skips injection logic — the nested pcall still goes through
transparently.

## Stack hygiene (unchanged from Phase 4)

- `lua_pushcclosure` pushes +1; `lua_setfield` pops -1. Net: 0 per setup step.
- `lua_createtable` pushes +1 (the new table).
- `luaL_loadbuffer` pushes +1 (the chunk). `lua_pcall(L, 0, 0, 0)` pops the
  chunk and pushes 0 results. Net: 0 on success.
- On failure: 1 error object left above where the engine expects the stack.
  We restore `L->top` directly (write to `[L + 0x18]`, the `top` field).
  This pops the error object without needing `lua_pop`/`lua_settop`.

## Loader-lock safety (unchanged from Phase 4)

`inject_install` is called from `DllMain` after `phase3_install`. It
touches only:
- `MH_CreateHook` / `MH_EnableHook` (loader-lock-safe per Phase 3 analysis)
- `GetModuleHandleW(NULL)` (already-loaded)
- `GetModuleFileNameW(NULL, ...)` (already-loaded)
- `GetEnvironmentVariableA` (registry-like, no loader interaction)
- `poc_log_linef` (open/write/close per line, no loader interaction)

The built DLL has no new imports vs Phase 4.

## Anything surprising or risky

1. **The matchers for `lua_type` and `lua_tonumber` had to be tweaked
   *not* to stop at the first `ret`.** Both functions have multiple
   basic blocks with early returns (LUA_TNUMBER fast path, etc.). The
   initial matcher logic (copied from the Phase 4 matchers) stopped at
   the first `ret`, missing the magic-constant / strscan-code paths in
   later blocks. Fixed by iterating all decoded instructions and stopping
   only on `int3` padding (real function boundary). This is a
   generalizable lesson for future LuaJIT matchers: small functions with
   type-dispatch often have multiple returns.

2. **`sizeof(GCstr) = 0x14` in LJ_64 non-GC64 builds** (not 0x18 as I
   initially assumed). The `strdata(s)` macro returns `(char*)s + 0x14`,
   visible as `add rax, 0x14` in the `lua_tolstring` disassembly. The
   GCstr layout is: 4-byte GCref + 2-byte header + 4×1-byte fields +
   4-byte len + 2-byte pad = 0x14.

3. **LJ_TTAB = 0xFFFFFFF4, not 0xFFFFFFFA.** I initially searched for the
   wrong tag value (confused LJ_TUPVAL with LJ_TTAB). The correct
   mapping (from `lj_obj.h`): LJ_TTAB = ~11u = 0xFFFFFFF4. The disasm
   matcher correctly checks for this value.

4. **The verify chunks initially used stdlib functions (`type`, `error`,
   `tostring`)** which aren't available in a no-libs VM. Phase 2/3 of A1
   failed until I either opened libs for the verify chunk OR rewrote the
   chunk to use only Lua primitives. The C-side verification (via
   `lua_getglobal` + `lua_type` from the test) is more robust and works
   regardless of VM state.

5. **`c_dofile`'s return value count.** When the loaded chunk returns
   values via `lua_pcall(L, 0, LUA_MULTRET, 0)`, the return-value count
   must be computed from `lua_gettop(L)` minus the function's stack base.
   For the POC, I compute it as `top - 1` (the 1 is the relpath arg).
   This works for the common case where `dofile(path)` is called as a
   statement (return value unused). For full correctness, the original
   mod_loader's `read_or_execute` does the same — `dofile` returns
   whatever the chunk returns, and callers that need it assign it.

6. **`p_lua_type` was wired but initially unused** (I included it for
   completeness per the brief). To avoid a dead-code warning, I added a
   defensive type check in `c_loadstring` that uses it to reject
   non-string/non-number arguments early. This is also good practice —
   `lua_tolstring` would silently return NULL for non-coercible types,
   but logging the actual type is more debuggable.

## Phase 6 handoff (results doc update)

Phase 5 completes all four P0 stories of the POC. The Phase 6 results
doc (`.agents/lua-vm-injection-poc-results.md`) should be updated to:

1. **Mark Story 6 (Bootstrap) as PASS.** With this evidence:
   - A1 Phase 5: dmf_loader.lua loaded + executed in a no-libs VM using
     only the C-function bootstrap, against the real game install's DMF
     source. 55/55 checks pass.
   - Tier B pending: the user's live-game test (this RUNBOOK).
2. **Update the "Confirmed addresses" table** with the 4 new RVAs:
   `lua_tolstring=0xc75190`, `lua_createtable=0xc73ad0`,
   `lua_type=0xc753b0`, `lua_tonumber=0xc730c0`.
3. **Update the "Critical: sandboxed _G — SOLVED" section** to mention
   that the full DMF dependency surface is now implemented as C functions
   (not just the trivial `poc_print` proof-of-concept).
4. **Add a "Recommendations" note** about what production work remains:
   - Implement the `io` library more fully (DMF modules beyond the
     loader's top-level execution may need `io.open`/`io.read`).
   - Replace `c_require_stub` with the engine's actual `require` (find
     its address — the engine's init at `0x32a2a0` saves the original
     to `_G.lua_require`, which we could read via `lua_getglobal`).
   - Cross-platform testing (Windows native, not just Proton).
   - Game-update resilience (runtime re-derivation of the new RVAs).

## Verification recipe

```sh
cd poc/phase5-dmf-bootstrap
make verify    # builds the DLL + runs A1 and A2
```

All three pieces must pass: DLL builds 200/200 exports; A1 55/55 checks
against real LuaJIT (5 phases + 8 disasm checks); A2 end-to-end with
Phase 1+2b+3+4 intact.

Phase 4 and Phase 3 standalone regression checks:
```sh
cd ../phase4-execute-lua && make verify    # ALL PASS
cd ../phase3-state-capture && make verify  # ALL PASS
```

## Out of scope (per spec)

- Loading user mods (DMF's own mod manager does this; we just bootstrap
  DMF itself).
- Multi-shot injection beyond the bootstrap (the latch is set on success;
  no further injection attempts).
- Loading the engine's `require` properly (stubbed for POC).
- Full `io` library (only an empty table for POC).
- Production-quality error propagation from `c_dofile` (we log + return
  0 instead of using `lua_error`, which would need its address).

---

## Reproduction

The 4 new matchers are offline and deterministic. Anyone can reproduce by
running `make a1` against the same Darktide.exe build (SHA-256
`132eed5f…791661`). Trying any matcher against a wrong RVA returns
nonzero. The full A1 test runs against the system LuaJIT (link via
`pkg-config luajit` or `/usr/include/luajit-2.1`) — no game install
needed except for the disasm checks + the optional Phase 5
(`dmf_loader.lua` loading; soft-skipped if not installed).
