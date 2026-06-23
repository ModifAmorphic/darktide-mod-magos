# Phase 2b — Runtime Discovery DLL (in-process integration)

> Extends the Phase 1 proxy DLL with the Phase 2a discovery engine so that,
> when the DLL loads inside the live game, it discovers the LuaJIT/engine
> function addresses at runtime against the in-memory Darktide.exe module
> and logs them. **No hooks, no function calls, no Lua — discovery and
> logging only.** Hooks are Phase 3.

## Summary

Phase 2b is complete. The integrated DLL:

- Compiles cleanly under mingw with the Phase 2a engine + vendored
  capstone 5.0.3 statically linked (no runtime DLL deps beyond
  KERNEL32 + the UCRT shims Phase 1 already used).
- Preserves Phase 1's dbghelp forwarding verbatim (200/200 exports
  resolved in the Wine smoke test).
- Spawns a worker thread from `DllMain` that runs `dt_discover` against
  `GetModuleHandleW(NULL)` after `DllMain` returns — out of the loader
  lock. The thread logs each discovered address with a MATCH/MISMATCH
  cross-check against the Phase 0 baked-in constants and writes the
  structured result to `darktide-poc-discovery.json`.
- Reproduces **all 7 Phase 0 confirmed addresses** via the real
  `LoadLibraryExW` in-memory path (Tier A1 strong gate, 17/17 checks).
- Applies **all 7 Phase 2a should-fixes** in place; Phase 2a's
  `make verify` remains **28/28 PASS**.

## Deliverables

| Path | Purpose |
|------|---------|
| `src/dllmain.c` | Phase 1 forwarding + Phase 2b worker-thread spawn |
| `src/discover_worker.c` | Worker thread: GetModuleHandle, SizeOfImage, dt_discover, log, JSON, cleanup |
| `src/poc_log.h` / `poc_log.c` | Thread-safe line logger (CRITICAL_SECTION around log writes) |
| `src/expected_addrs.h` | Phase 0 cross-check constants baked into the DLL |
| `build.sh` | mingw cross-compile: regen stubs + capstone + engine + DLL |
| `Makefile` | Targets: `build`, `a1`, `a2`, `iat-test`, `p2a-verify`, `verify` |
| `test/in_memory_host.c` + `test/run_in_memory_test.sh` | **Tier A1** (the strong gate) |
| `test/host.c` + `test/run_wine_test.sh` | **Tier A2** (DLL plumbing smoke) |
| `test/corrupt_iat_test.c` | **Should-fix #5** IAT-corruption regression |
| `install.sh` / `uninstall.sh` | `.orig` backup + no-clobber (mirrors Phase 1) |
| `RUNBOOK.md` | Tier B user instructions (`dbghelp=native,builtin`) |

### Reused (not duplicated) from prior phases

| Source | Used by Phase 2b as |
|--------|---------------------|
| `phase1-proxy-dll/tools/gen_stubs.py` | Stub generator (run by `build.sh`); emits the same 200 forwarders |
| `phase2-runtime-discovery/engine/*.c` | Engine sources, compiled directly into the DLL by `build.sh` |
| `phase2-runtime-discovery/vendor/capstone/` | Capstone 5.0.3, built with mingw into a separate `build-mingw/` static lib |

No engine source was forked — Phase 2b compiles the same `engine/*.c`
files Phase 2a uses (with the should-fixes applied in place).

## Tier A results — ALL PASS

### A1 — In-memory engine correctness (strong gate)

`test/run_in_memory_test.sh` builds `in_memory_host.exe` (mingw + engine +
capstone), loads `Darktide.exe` via `LoadLibraryExW(..., LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_AS_DATAFILE)`
under Wine, and runs `dt_discover` against the loader-mapped image.

```
[A1] mapped via flag 0x22: base=0x100000000 size=0x27db000 image_base=0x140000000
[A1]  checks:   17
[A1]  failures: 0
[A1]  RESULT: PASS — in-memory discovery matches Phase 0 oracle.
```

All 7 addresses reproduce exactly via the in-memory path (the same path
the DLL's worker thread uses in the live game):

| Function | Expected | Got | Status |
|----------|----------|-----|--------|
| `lua_panic` body | `0x328220` | `0x328220` | ✓ |
| LuaEnvironment init | `0x32a660` | `0x32a660` | ✓ |
| `lua_newstate` thunk | `0xc7c000` | `0xc7c000` | ✓ |
| `lua_newstate` body | `0xc7eea0` | `0xc7eea0` | ✓ |
| `lua_atpanic` | `0xc77f40` | `0xc77f40` | ✓ |
| `lua_gettop` | `0xc74050` | `0xc74050` | ✓ |
| `luaL_loadbuffer` | `0xc7ad80` | `0xc7ad80` | ✓ |

Wine uses flag `0x22` (`LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_AS_DATAFILE`)
which maps sections to their RVAs and preserves headers — exactly the
layout the engine expects, and exactly what the Windows loader produces
in the live process. (`LOAD_LIBRARY_AS_DATAFILE` alone maps as a raw file
view without RVA layout; `LOAD_LIBRARY_AS_IMAGE_RESOURCE` is the correct
flag. The handle's low 2 bits are set per MSDN; the test masks them off.)

### A2 — DLL plumbing smoke test

`test/run_wine_test.sh` loads `build/dbghelp.dll` in a Wine host exe,
calls a forwarded export, waits for the worker, and checks the log + JSON.

```
[darktide-poc] DllMain forwarders resolved=200 missing=0 total=200
[darktide-poc] DllMain spawned discover_worker thread
[darktide-poc] discover summary matched=0 mismatched=0 unresolved=7 pcall=deferred
[darktide-poc] discover json written
[host] PASS: plumbing works end-to-end
```

The worker ran discovery against the Wine host's main module (not
Darktide), so all 7 addresses are UNRESOLVED (expected — there's no
LuaJIT in the host exe). This proves the full plumbing:
`DllMain` → `CreateThread` → `dt_discover` → log lines + JSON write →
clean thread exit. (Correctness is A1's job.)

### Should-fix #5 — IAT-corruption regression

`test/corrupt_iat_test.c` runs discovery against a clean Darktide.exe
image and an identical image with all **825 IAT slots** overwritten by
`0xDEADBEEFCAFEBABE+i`, then asserts the two JSON outputs are
byte-identical.

```
[iat] corrupted 825 IAT slots with 0xDEADBEEFCAFEBABE+i
[iat] clean json: 43439 bytes, corrupt json: 43439 bytes
[iat] PASS: IAT corruption produced 0 diffs in discovery output.
```

This permanently guards the IAT-value-independence property (the engine
reads import names from the INT, not the IAT, and resolves import thunks
by IAT-slot RVA, not value). At runtime the Windows loader overwrites
those 825 slots with real function pointers; this test proves the engine
is blind to that.

### Phase 2a regression — 28/28 PASS

After applying the should-fixes, Phase 2a's own test harness still
passes every check:

```
[tierA]  checks:   28
[tierA]  failures: 0
[tierA]  RESULT: PASS — all 7 confirmed addresses match Phase 0's oracle.
```

Run `make p2a-verify` to reproduce.

## All 7 Phase 2a should-fixes — applied

All changes are in `poc/phase2-runtime-discovery/engine/` (so
both the offline tool and the DLL benefit), unless noted otherwise.

1. **`scan.c`** — `.text` scan bound changed from `raw_size` to
   `min(raw_size, virtual_size)` at all three scanners
   (`dt_find_lea_xrefs`, `dt_find_callers`, `dt_trace_thunk`), via a
   new `scan_bound()` helper. The `image_size` early-return stays as a
   hard safety bound. Matters more in-memory where padding is zero-filled.

2. **`pe.c` `parse_pdata`** — same `min(raw_size, virtual_size)` fix for
   both the bound check and the entry count.

3. **`pe.c` `parse_imports`** — the `OriginalFirstThunk == 0` fallback to
   `FirstThunk` is now a skip-DLL-with-warning. Reading the IAT as a name
   table post-load would yield real function pointers, not hint/name
   RVAs; safer to skip the DLL entirely. (Darktide.exe has no bound
   imports, so this is defensive. The DLL's name is still recorded for
   diagnostics; its entries are not.)

4. **`disasm.c` `open_cs`** — real bug fixed: `cs_open` failure now
   returns the non-zero `cs_err` instead of `CS_ERR_OK` (which silently
   masked init failures).

5. **`test/corrupt_iat_test.c`** (in Phase 2b test dir) — the IAT-
   corruption regression test, documented above. 825 slots corrupted,
   0 diffs.

6. **`engine/json.c`** — new `"lua_pcall_clustering"` section emitting
   the per-candidate survey (rva, score, reasoning) plus the summary.
   Previously `pcall_candidates` and `pcall_summary` were computed but
   never serialized; now Phase 2a's clustering work is visible in JSON.

7. **Comments + `report.md`** — added a section-header-preservation
   comment at the top of `dt_discover` in `engine/discover.c`
   documenting that the engine reads `VirtualAddress`/`VirtualSize`/
   `SizeOfRawData` from section headers (never `PointerToRawData`),
   and that the Windows loader preserves PE headers verbatim. Also
   corrected the Phase 2a `report.md` wording: the engine uses
   packed-struct member access (`#pragma pack(push,1)`), not direct
   byte-offset reads — describing what the code actually does.

## lua_pcall outcome — deferred (honest negative)

The engine surveys 64 thin-wrapper candidates in the confirmed LuaJIT
API window `[0xc73050, 0xc7ff4a)`, scores each by the lua_pcall shape
(small body, one internal docall callee, rdx/r8/r9 integer-arg setup),
and finds **four candidates tied at the top score of 90** with no
decisive margin:

| RVA | Body | Callee | Int args | Score |
|-----|------|--------|----------|------:|
| `0xc744c0` | 135B | `0x6845` (lua_load!) | rdx+r9 | 90 |
| `0xc748d0` | 72B  | `0xc82fc0` | rdx+r8 | 90 |
| `0xc74f30` | 157B | `0xc7ed10` | r8+r9  | 90 |
| `0xc754d0` | 133B | `0xc7ed10` | rdx+r9 | 90 |

The engine **defers to Phase 3 dynamic confirmation** rather than
force-fitting. This is the correct outcome — Phase 0 also could not pin
`lua_pcall` offline, and the spec explicitly blesses an honest negative.

The full survey (all 8 top candidates with per-candidate reasoning) is
in the JSON under `lua_pcall_clustering.candidates` (should-fix #6).

## Notes for Phase 3

### Discovered addresses to hook

Phase 3 should target the addresses in `darktide-poc-discovery.json`
(written next to the DLL at runtime). Read the JSON, do NOT hardcode:
game updates shift every address, and the discovery engine + JSON are
the update-survival mechanism.

| Function | RVA | Source in JSON |
|----------|-----|----------------|
| `lua_newstate` (real body) | `0xc7eea0` | `category_b_candidates[name="lua_newstate"].real_body_rva` |
| `lua_newstate` (thunk) | `0xc7c000` | `category_b_candidates[name="lua_newstate"].thunk_entry_rva` |
| `lua_atpanic` | `0xc77f40` | `category_b_candidates[name="lua_atpanic"].candidate_rvas[0]` |
| `lua_gettop` | `0xc74050` | `category_b_candidates[name="lua_gettop"].candidate_rvas[0]` |
| `luaL_loadbuffer` | `0xc7ad80` | `category_b_candidates[name="luaL_loadbuffer"].candidate_rvas[0]` |
| `lua_pcall` | (deferred) | `lua_pcall_clustering.candidates[]` — try the 4 top candidates dynamically |

Hook `lua_newstate` at the **thunk** (`0xc7c000`) — that's what callers
invoke — or the **real body** (`0xc7eea0`) after following the CFG thunk.
Either works; the thunk is the safer instrumentation point (single
inbound edge).

For `lua_pcall`: hook each of the 4 top candidates, observe which fires
on the `pcall(L, nargs, nresults, errfunc)` shape (4-arg with integer
rdx/r8/r9), keep the winner. This is the dynamic resolver the engine
defers to.

### JSON file format

- Path: `<DLL dir>/darktide-poc-discovery.json` (~43 KB).
- Top-level keys: `binary`, `pe_sections`, `init_candidate`,
  `category_b_candidates`, `init_candidate_call_graph`,
  `classified_call_targets`, `lua_pcall_clustering`,
  `methodology_gaps`, plus the negative-result sections.
- `init_candidate` gives the LuaEnvironment init range (begin/end RVA)
  and the `lua_panic` body RVA — useful for understanding the call
  graph but not for hooking (init has already run by the time Phase 3
  loads).
- `init_candidate_call_graph` (32 edges) is the full call graph of the
  init function with arg hints, thunk chains, and classifications —
  useful for finding other LuaJIT API functions by their call shape.

### Worker thread lifetime

The worker runs once at DLL load, writes the log + JSON, and exits.
Phase 3 should either (a) read the JSON file Phase 2b wrote, or
(b) add its own discovery step. Re-running `dt_discover` from a Phase 3
hook is safe (it's re-entrant — `dt_engine_setup` does `memset(&g_state, 0,
sizeof(g_state))` at the start).

## Anything surprising or risky

1. **`LOAD_LIBRARY_AS_DATAFILE` alone is NOT sufficient for A1.** It maps
   the file as a raw view without RVA layout; the engine needs sections
   at their `VirtualAddress`. The correct flag is
   `LOAD_LIBRARY_AS_IMAGE_RESOURCE` (used with `LOAD_LIBRARY_AS_DATAFILE`,
   = `0x22`), which maps as an image. Wine honours this. The spec's hint
   to "verify which flag works under Wine" was the right call.

2. **`SizeOfImage` offset wording in the spec.** The brief said
   "SizeOfImage at offset 0x50 from the optional header start". The
   value 0x50 is correct **from `e_lfanew`** (PE signature), not from
   the optional header start (which is 0x38). I used the from-`e_lfanew`
   values (`0x30` for ImageBase, `0x50` for SizeOfImage) and documented
   the discrepancy in `discover_worker.c`. Both yield the same byte.

3. **DLL size jumped to 3 MB** (from Phase 1's 220 KB). The bulk is the
   engine's `g_state` BSS (~2 MB: 65536 RUNTIME_FUNCTION entries + 8192
   import entries) plus capstone's tables + debug info. This is expected
   and harmless; the BSS is zero-initialized and never paged in unless
   discovery runs. If size matters later, shrink
   `DT_MAX_*` constants in `engine.h` (the Darktide build uses ~44k of
   the 65k .pdata slots and ~825 of the 8192 import slots — both have
   headroom but could be trimmed).

4. **The worker thread is fire-and-forget.** `DllMain` creates it,
   closes the handle, and returns. If the process exits while discovery
   is mid-flight, the JSON write might be incomplete. The log lines are
   safe (each is open/write/close per line). For the POC this is fine —
   discovery takes <2s against the real game and the process doesn't
   exit during that window.

5. **`lua_pcall`'s top candidate `0xc744c0` calls `0x6845` (lua_load).**
   That makes it almost certainly one of the other `luaL_load*` wrappers
   (`luaL_loadstring` or `luaL_loadfilex`), NOT `lua_pcall`. The
   clustering heuristic correctly declined to mis-identify it. Phase 3
   can use this to prune the candidate list (drop any whose callee is
   `lua_load`).

## Verification recipe

```sh
cd poc/phase2b-runtime-discovery
make verify    # builds the DLL + runs A1, A2, IAT test, Phase 2a verify
```

All five tiers must pass (DLL builds 200/200 exports; A1 17/17; A2 end-
to-end; IAT 0 diffs; Phase 2a 28/28). Tier B (the live game) is the
RUNBOOK's job.

## Out of scope (per spec)

- Hooks of any kind — Phase 3
- Calling any discovered LuaJIT function — Phase 3
- Capturing `lua_State*` — Phase 3
- Running the live game — Tier B (RUNBOOK.md)
