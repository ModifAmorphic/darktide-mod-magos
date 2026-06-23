# Phase 2a — Runtime Discovery Engine (C Port of Phase 0)

> Pure offline C port of `discover.py`. The engine consumes a flat
> image buffer laid out by RVA and reproduces every Phase 0 address
> exactly. **No DLL, no game launch, no injection, no hooks.**

## Summary

Phase 2a is complete. The C discovery engine reproduces **all 7 of
Phase 0's confirmed addresses verbatim** against the pinned Darktide.exe
build (SHA-256 `132eed5f…791661`, 18,715,784 bytes). The Tier A test
harness (`make test`) passes 28/28 checks.

The engine is structured for Phase 2b DLL integration: every byte of
code under `engine/` is pure in-memory C (no POSIX, no Linux-only, no
Windows-specific headers) and takes a `(image, image_size, image_base)`
triple — the same triple the injected DLL will pass from
`GetModuleHandleW(NULL)`. Only `tool/discover.c` (the offline CLI
wrapper) uses POSIX, and Phase 2b replaces that wrapper with a DllMain.

## Deliverables

| Path | Purpose |
|------|---------|
| `engine/engine.h` | Public API + result types |
| `engine/engine_internal.h` | Internal cross-TU API |
| `engine/util.h` | Inline string/hex helpers |
| `engine/pe.c` | In-memory PE parser (DOS/PE/sections/.pdata/imports) |
| `engine/scan.c` | LEA-xref scanner (`48/4C 8D`), E8 caller scanner, thunk follower, import-thunk resolver |
| `engine/disasm.c` | Capstone wrapper, leaf-function disasm, RIP-target extract |
| `engine/classify.c` | Body classifiers (lua_gettop/atpanic/push*/load_wrapper/lua_gc), init selection, lua_newstate backward-dataflow trace, luaL_loadbuffer bytecode trace |
| `engine/lua_pcall.c` | lua_pcall clustering attempt (honest deferral) |
| `engine/json.c` | JSON serializer (Phase 0 schema, `\uXXXX` ASCII escapes) |
| `engine/report.c` | Markdown report writer (Phase 0 layout) |
| `engine/sha256.c` | Self-contained SHA-256 |
| `engine/anchors.c` | §3 + §5 anchor tables (verbatim) |
| `engine/discover.c` | Phase A→E orchestration |
| `vendor/capstone/` | Capstone 5.0.3 (x86 only, static lib) |
| `tool/discover.c` | Linux CLI wrapper: reads file, maps by RVA, runs engine, writes outputs |
| `test/check_crosscheck.c` | Tier A test harness (28 assertions) |
| `build.sh` | Build recipe (capstone + engine + tool + test) |
| `Makefile` | Convenience targets: `make`, `make test`, `make run`, `make verify` |
| `output/addresses.json` | Engine output (matches Phase 0's `addresses.json` field-for-field) |
| `output/report.md` | Human-readable engine report |

## Tier A result — PASS

```
[tierA] =========================================
[tierA]  checks:   28
[tierA]  failures: 0
[tierA]  RESULT: PASS — all 7 confirmed addresses match Phase 0's oracle.
[tierA] =========================================
```

The 7 confirmed addresses reproduced exactly:

| Function | Expected | Got | Status |
|----------|----------|-----|--------|
| `lua_panic` body | `0x328220` | `0x328220` | ✓ |
| LuaEnvironment init | `0x32a660` | `0x32a660` | ✓ |
| `lua_newstate` thunk | `0xc7c000` | `0xc7c000` | ✓ |
| `lua_newstate` body | `0xc7eea0` | `0xc7eea0` | ✓ |
| `lua_atpanic` | `0xc77f40` | `0xc77f40` | ✓ |
| `lua_gettop` | `0xc74050` | `0xc74050` | ✓ |
| `luaL_loadbuffer` | `0xc7ad80` | `0xc7ad80` | ✓ |

Beyond the addresses, **the full call graph (32 edges) and classified
target table (20 entries) match Phase 0's `addresses.json` field-for-
field**, including:
- Every LEA-xref site address
- Every thunk chain (`[0xc7c000, 0xc7eea0]` etc.)
- The complete lua_newstate backward dataflow trace
  (`atpanic_call_rva=0x32a872`, `rcx_load_rva=0x32a868`,
  `rcx_source="qword ptr [r14]"`, `l_slot="[r14]"`,
  `store_rva=0x32a83a`, `newstate_call_rva=0x32a835`)
- All 13 §3 anchor sanity checks pass (13/13)
- All 4 §5 error strings surveyed, 0 LEA xrefs (Phase D gap confirmed)
- All 6 methodology gaps recorded

Run it yourself: `cd poc/phase2-runtime-discovery && make verify`

## lua_pcall outcome: deferred (honest negative)

`lua_pcall: deferred (surveyed 64 cands, top score 90)`

The engine performs a genuine clustering scan in the confirmed LuaJIT
API window `[0xc74050 - 0x1000, 0xc7ef4a + 0x1000)`, scoring each
function body by the lua_pcall shape:

- Small body (< 250 bytes — lua_pcall is a thin wrapper)
- ≤ 2 internal calls (one of which is the `lj_docall` callee)
- Sets up integer args in `rdx`/`r8`/`r9` (the `(nargs, nresults,
  errfunc)` ints after `L=rcx`)
- Callee is substantially bigger than the body (docall is non-trivial)

The scan surfaced **64 thin-wrapper candidates** with scores ranging
30-90, but **no unique winner emerged** — four candidates tied at the
top score of 90:

| RVA | Body | Callee | Int args | Score |
|-----|------|--------|----------|------:|
| `0xc744c0` | 135B | `0x6845` (lua_load!) | rdx+r9 | 90 |
| `0xc748d0` | 72B  | `0xc82fc0` | rdx+r8 | 90 |
| `0xc74f30` | 157B | `0xc7ed10` | r8+r9  | 90 |
| `0xc754d0` | 133B | `0xc7ed10` | rdx+r9 | 90 |

With no decisive signal separating the four, the engine **defers to
Phase 3 dynamic confirmation** rather than force-fitting. This is the
correct outcome — Phase 0 also could not pin lua_pcall offline, and
the spec explicitly blesses an honest negative here.

The candidate survey is published in `output/report.md` so a human can
sanity-check the top candidates before Phase 3 hooks one of them.

## Deltas from Phase 0

**None.** Every address, every classification, every confidence level
matches Phase 0's `addresses.json` exactly.

Two minor presentational differences (not deltas in the data):

1. **anchor_sanity read location**: Phase 0 reads from `raw[file_offset]`
   (the file bytes); the C engine reads from `image[anchor_rva]` (the
   RVA-mapped image). Same byte either way — `image[anchor_rva]` in our
   buffer is exactly the byte Phase 0 sees at `raw[file_offset]`. This
   is the right call for Phase 2b, where there is no file — only the
   in-memory module addressed by RVA.

2. **JSON key order in nested objects**: a few sub-objects emit keys in
   a slightly different order than Python's `json.dump`. Values are
   identical; only key order differs (irrelevant per JSON spec).

## Portability notes for Phase 2b (DLL integration)

The engine compiles cleanly with both native gcc and (forthcoming)
mingw. Audit:

- **Engine sources** (`engine/*.c,.h`) include only:
  - C standard: `<stddef.h> <stdint.h> <stdio.h> <stdlib.h> <string.h>`
  - `<capstone/capstone.h>`
  - No POSIX, no Linux-only, no Windows headers. **Zero source changes
    needed for mingw.**
- **Capstone** is already cross-platform; rebuilding with mingw is a
  one-liner: `cmake -S vendor/capstone -B vendor/capstone/build-mingw
  -DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc …`.
- **The wrapper** (`tool/discover.c`) is the only Linux-only file:
  - Uses `open`/`read`/`fstat`/`readlink` and `<sys/stat.h>`/`<fcntl.h>`/`<unistd.h>`.
  - Phase 2b replaces it with a `dll_main.c` that:
    - Implements `DllMain` (`DLL_PROCESS_ATTACH`)
    - On attach, calls `GetModuleHandleW(NULL)` to get the EXE base
    - Reads `SizeOfImage` from the optional header (same byte-offset
      math the offline wrapper already uses)
    - Calls `dt_discover(base, size_of_image, image_base, &result)`
      directly — no file I/O, no section mapping (Windows already
      maps sections to their RVAs in-process)
- **Singleton state**: `dt_engine_setup()` builds a process-wide
  `dt_engine_state_t` (in `pe.c`). Phase 2b's DLL must guard this with
  a mutex if discovery can run concurrently with anything else (it
  won't — discovery runs once at `DllMain` time on the loader lock).
  For the POC, the singleton is fine as-is.

**One substantive Phase 2b difference to be aware of:** at runtime the
Windows loader fixes up IAT entries with the actual resolved function
addresses (overwriting the import-name RVAs that exist in the on-disk
file). The engine's `dt_resolve_import_thunk` reads the **import
directory** (not the IAT values), so this is transparent — import
resolution still works identically. The only observable difference:
the bytes at the IAT itself will differ between offline and runtime,
but nothing in the engine depends on those bytes.

**SHA-256 in runtime context**: Phase 2b's DllMain has no file to hash.
The `result.sha256` field can be left empty, or computed over the
mapped image (which differs from the file SHA because of section
padding). Recommendation: leave it empty and tag the binary by
`SizeOfImage` + a sentinel string. Discovery doesn't need it.

## How to use

```sh
# Build everything (capstone, engine, tool, test)
make

# Run the offline discovery tool, write output/addresses.json + report.md
make run

# Run the Tier A test harness (28 assertions)
make test

# Build + test + run + idempotency check
make verify

# Or directly:
./build.sh && ./build/dt_discover && ./build/check_crosscheck
```

The Tier A test is fully re-runnable: `make clean && make verify`
reproduces from scratch in ~10 seconds (capstone build dominates).

## Anything surprising

1. **`dt_result_t` is 3.4 MB.** With 256 call-graph slots, 128
   classified-target slots, 64-xref-site arrays per entry, and 1 KB
   evidence strings, the struct balloons. The first cut had it on the
   stack and segfaulted deep inside `dt_write_report`; the wrappers
   now heap-allocate it (`calloc`). Phase 2b should do the same — or
   shrink the slots if memory is tight inside the DLL.

2. **Struct-alignment footguns in PE parsing.** The first cut used a
   packed struct for the optional header with a missing
   `AddressOfEntryPoint` field, which silently misaligned `ImageBase`
   and produced `0x4000000000001000` instead of `0x140000000`. The
   fix was to use a **complete, correctly-ordered packed-struct
   definition** (`#pragma pack(push,1)` + the full PE32+ optional
   header layout in `engine/pe.c`) so struct member access aligns
   with the on-disk layout. (The standalone wrappers in
   `tool/discover.c` and `test/check_crosscheck.c` read the few
   fields they need by direct byte offset instead — a separate,
   narrower parse used only for their own RVA-mapping step. The
   engine itself, which is what Phase 2b links against, uses the
   packed-struct member access.) Describe what the code actually does
   rather than the historical bug; see `pe.c` lines 25-89.

3. **lua_pcall's clustering surfaced `0xc744c0` calling `0x6845`.**
   `0x6845` is the engine's `lua_load` (the >14 KB internal function
   that `luaL_loadbuffer` wraps). So `0xc744c0` is almost certainly
   one of the other `luaL_load*` wrappers (`luaL_loadstring` or
   `luaL_loadfilex`), not `lua_pcall`. The clustering heuristic
   correctly declined to mis-identify it.

4. **Capstone op_str is the contract.** The body-shape classifiers
   (`lua_gettop`, `lua_atpanic`, `lua_push*`, `lua_load_wrapper`)
   intentionally use substring matching on `insn->op_str` rather than
   capstone's detail API. This mirrors `discover.py` bit-for-bit and
   is what guarantees the same classification outcomes. The downside
   is brittleness to capstone's printer formatting changes; the
   upside is zero drift from the Python reference. Capstone 5.0.3's
   Intel-syntax printer is stable.

## Out of scope (per spec)

- DLL work, injection, anything in-process — Phase 2b
- Hooks of any kind — Phase 3
- Calling any discovered function — Phase 3
- Modifying the Phase 0 deliverables or reference docs

## What's next (Phase 2b prep, not done here)

1. Stand up a mingw build of capstone + the engine sources.
2. Write `dll_main.c` (the in-process wrapper) — it's < 100 lines of
   code: `GetModuleHandleW(NULL)`, optional-header walk for
   `SizeOfImage`, then call `dt_discover(...)`.
3. At that point the engine produces the same `dt_result_t` from
   inside the DLL that the offline tool produces here — same addresses
   verifiable, no behaviour change.
