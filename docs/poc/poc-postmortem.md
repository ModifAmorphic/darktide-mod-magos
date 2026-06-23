# POC Post-Mortem: Lua VM Injection

> Honest assessment of what we set out to prove, what we accomplished,
> and where the gaps are. Written as a retrospective, not a victory lap.

---

## The #1 Question: Zero Game-Directory Footprint

**Did we prove DLL injection can work without altering game directory
files?**

**Partially.** Here's the precise breakdown:

**What we proved:** Everything the DLL does once inside the process —
runtime function discovery, `lua_State` capture, Lua code execution, DMF
bootstrap, C-function sandbox bypass. All of this is
injection-mechanism-agnostic. Whether the DLL gets in via proxy DLL,
`CreateRemoteThread`, or any other method, the internal work is
identical and fully validated.

**What we did NOT prove:** The zero-footprint injection mechanism
itself (`CreateRemoteThread`). The POC used a proxy DLL (`dbghelp.dll`)
placed in the game's `binaries/` directory — that's one added file. On
Linux/Proton, it also required a `WINEDLLOVERRIDES` Steam launch option.
This was an explicit POC shortcut to avoid building a launcher process,
and it was documented as such from the start.

**What this means for production:** The proxy DLL approach leaves a
small footprint (one file + a launch option toggle). The production
design replaces it with `CreateRemoteThread` injection from a mod
manager launcher — zero files in the game directory. The DLL internals
don't change; only the delivery mechanism does. The POC de-risked the
hard part (what the DLL does inside the process). The zero-footprint
delivery is lower-risk engineering that wasn't tested.

**Bottom line:** The POC proved the approach is viable. The
zero-footprint claim is designed but not validated. A Phase 2 POC (or
early production work) should validate `CreateRemoteThread` injection on
both Windows and Proton.

---

## Goals vs Outcomes

### Goal 1: Zero game-directory footprint
**Status: PARTIALLY PROVEN**

As described above. The DLL's internal capabilities are fully proven.
The zero-footprint delivery mechanism (CreateRemoteThread) is designed
but untested. The proxy DLL used in the POC adds one file to the game
directory.

### Goal 2: No game-update / Steam-verification breakage
**Status: PROVEN IN PRINCIPLE**

The POC does not modify any original game files' content. No
`bundle_database.data` patching. No bundle modifications. The proxy DLL
is an added file (not in the game's Steam depot manifest, so Steam
verification ignores it). Runtime function discovery handles address
shifts across updates via source-pattern matching.

**Caveat:** If a game update changes the binary structurally — new
LuaJIT version, renamed engine functions, removed string anchors —
discovery could fail. This is a known risk. The string anchors
(`stingray::LuaEnvironment::*`) have been stable across years of
Darktide updates, but there's no guarantee.

### Goal 3: Eliminate bundle_database.data fragility
**Status: FULLY ACCOMPLISHED**

The bundle system is completely bypassed. No `bundle_database.data`
patching, no magic signature hunting, no record format fragility, no
dtkit-patch. DMF is loaded directly into the Lua VM via the LuaJIT C
API. The thing that historically crashes on updates is entirely out of
the picture.

This was the strongest argument for choosing Lua VM Injection over
Bundle Virtualization, and it delivered completely.

### Goal 4: Clean modded vs vanilla switching
**Status: PROVEN (proxy DLL approach)**

Without the proxy DLL (or without the `WINEDLLOVERRIDES` on Linux), the
game launches vanilla with no mods. Adding the proxy enables mods.
Removing it disables them. No uninstall script, no file verification, no
cleanup needed — just remove the file or the launch option.

For the production `CreateRemoteThread` design, this is even cleaner:
launch through the mod manager = modded, launch from Steam = vanilla.
No files to add or remove.

### Goal 5: Prove DLL injection is viable end-to-end
**Status: FULLY ACCOMPLISHED**

All P0 stories proven in the live game:
- DLL injection into the process ✓
- Runtime function discovery inside the live process ✓
- `lua_State` pointer capture ✓
- Arbitrary Lua code execution ✓
- DMF bootstrap from staging directory ✓

---

## POC Story Outcomes

| Story | Priority | Status | Notes |
|-------|----------|--------|-------|
| 1. Process injection | P0 | ✅ Full | Proxy DLL, 200/200 export forwarding, game stable |
| 2. Function discovery | P0 | ✅ Full (with corrections) | 16 addresses confirmed; methodology corrected mid-POC |
| 3. lua_State capture | P0 | ✅ Full | MinHook on lua_newstate, verified with lua_gettop=0 |
| 4. Lua execution | P0 | ✅ Full (with workaround) | `return 42` executed; standard libs not available (sandbox) |
| 5. Timing | P1 | ✅ Full (with workaround) | Retry-on-error replaced precise timing; succeeded on first attempt |
| 6. DMF bootstrap | P1 | ✅ Full (with workaround) | C-function bootstrap replaced Lua-environment approach; DMF loaded, hooks active |

Every story passed, but three required workarounds that changed the
original plan:

- **Story 4:** Sandbox workaround (C functions instead of standard libs)
- **Story 5:** Timing workaround (retry-on-error instead of precise hook point)
- **Story 6:** Bootstrap workaround (C-function registration instead of Lua environment setup)

---

## What Went Right

1. **The string-anchor discovery methodology worked.** All 13 anchor
   strings were present at their documented offsets. The
   string → xref → .pdata → function chain reliably located engine
   functions. This was the approach's foundation, and it held up.

2. **Source-pattern matching is the reliable identification method.**
   After the lua_pcall misidentification (Phase 3's "first-to-fire"
   heuristic failed), source-pattern matching against LuaJIT 2.1 source
   proved definitive. Every function identified this way was confirmed
   correct.

3. **The retry-on-error timing mechanism was elegant.** Instead of
   guessing when globals are ready, the chunk self-checks and retries
   on the engine's own `lua_pcall` calls. In the live game, it
   succeeded on the first attempt. This eliminated the timing problem
   entirely for the POC.

4. **The C-function bootstrap solved the sandbox cleanly.** The
   sandboxed `_G` was the POC's biggest surprise. The solution —
   register C functions as globals via `lua_pushcclosure` +
   `lua_setfield` — is the same mechanism the engine itself uses. It
   bypasses the sandbox entirely and is proven in the live game.

5. **DMF's dependency surface is tiny.** Only 6 globals needed from the
   harness. All implementable as C functions. This made the bootstrap
   tractable despite the sandbox.

6. **The engagement-state workflow kept sessions in sync.** The shared
   `ENGAGEMENT-STATE.md` file eliminated copy-pasting between research
   and coding sessions. Questions for Research, doc corrections, and
   handoff status all flowed through one file.

---

## What Went Wrong (and How It Was Fixed)

### 1. lua_pcall misidentification (Phase 3 → corrected Phase 4)

**What happened:** Phase 3's dynamic resolution ("first candidate to
fire with matching L") picked `0xc74f30` — a 2-arg stack-check helper,
not `lua_pcall`. It also wrongly pruned the real pcall (`0xc744c0`) by
misidentifying its callee as `lua_load`.

**Root cause:** "First to fire" is unreliable — multiple functions fire
during engine init. The pruning logic misidentified `lj_vm_pcall` as
`lua_load` based on incomplete disassembly analysis.

**Fix:** Source-pattern matching against LuaJIT 2.1's `lj_api.c:1120`.
Instruction-by-instruction comparison. Definitive.

**Lesson:** Dynamic heuristics are for hypothesis generation, not
confirmation. Source-pattern matching against known source is the
authoritative identification method.

### 2. Sandboxed _G (Phase 4 — required redesign)

**What happened:** Standard Lua library functions (`print`, `io`,
`require`) are inaccessible from `luaL_loadbuffer` chunks. The original
theory assumed they'd be available. They're not.

**Root cause:** The engine calls `luaL_openlibs` during init, then
replaces `_G.print`, `_G.require`, `_G.dofile`, `_G.loadfile`, `_G.load`
with engine wrappers. Our chunks see the stripped default environment.
Re-calling `luaL_openlibs` is destructive (overwrites engine wrappers,
game crashes).

**Fix:** C-function bootstrap. Register DMF's 6 dependencies as C
functions via `lua_pushcclosure` + `lua_setfield(L, LUA_GLOBALSINDEX)`.
Bypasses the sandbox entirely. Same mechanism the engine uses.

**Lesson:** Game engines sandbox their Lua environments. Never assume
standard library availability for injected code. The C-function approach
should be the default, not a fallback.

### 3. .pdata is not a complete function map (Phase 0)

**What happened:** CFG thunks, leaf functions, and import thunks all
fall in `.pdata` gaps. The original theory assumed `.pdata` mapped every
function.

**Fix:** Added three gap-handling categories to the discovery
methodology. CFG thunks followed via `E9 rel32`. Leaf functions
disassembled directly. Import thunks resolved via IAT.

**Lesson:** Windows PE tooling assumptions need empirical validation.
"Documented behavior" and "actual behavior" can differ for edge cases.

### 4. Error strings can't be LEA-xref'd (Phase 0)

**What happened:** LuaJIT error messages are interned via `lj_str_new()`
at VM init and referenced by `GCstr*` handle, not by `.rdata` address.
Zero LEA cross-references. The original anchors doc §5 proposed using
these strings as function-discovery anchors.

**Fix:** Section marked non-functional. The LuaJIT API cluster
(`0xc7xxxx`) is the reliable anchor surface instead.

**Lesson:** String-presence ≠ string-referencability. LuaJIT's string
interning is an implementation detail that invalidates the obvious
discovery approach.

### 5. Worker thread stack overflow (Phase 2b)

**What happened:** The discovery worker thread (default 1MB stack)
overflowed during Capstone disassembly. Crashed the game
(`STATUS_STACK_OVERFLOW 0xC00000FD`).

**Fix:** `CreateThread(NULL, 32*1024*1024, ...)` — 32MB stack.

**Lesson:** DLL worker threads need explicit, generous stack sizes.
Default 1MB is insufficient for heavy analysis workloads. Tier A tests
(structured, small inputs) can't catch this — only the live game
exercises the real data volume.

### 6. Wine override quirk (Phase 1)

**What happened:** `WINEDLLOVERRIDES=dbghelp=native` blocked the proxy
from loading the real builtin `dbghelp.dll` for export forwarding. Wine
overrides are keyed on module name, not path.

**Fix:** `dbghelp=native,builtin` — allows both the native proxy and
the builtin fallback.

**Lesson:** Wine's DLL override system has non-obvious semantics. Always
test the full chain (proxy → real DLL → export forwarding) under Wine,
not just the load step.

---

## What Remains Unproven

These items were out of POC scope and need validation before or during
production:

1. **CreateRemoteThread injection** (zero-footprint delivery). The POC
   used a proxy DLL. Production uses CreateRemoteThread from a launcher.
   Untested on both Windows and Proton.

2. **~~Windows native testing.~~** ✅ VALIDATED. The proxy DLL works on
   native Windows without `WINEDLLOVERRIDES`. Full chain confirmed:
   injection, discovery, capture, execution, DMF bootstrap, engine
   hooks active. One minor difference: 3 of 200 dbghelp exports
   unresolved on Windows (vs 200/200 on Linux) — not called during
   normal operation, no impact.

3. **Game-update resilience.** Runtime discovery handles address shifts,
   but no game update occurred during the POC to validate this. The
   binary SHA-256 is pinned; a different binary version is untested.

4. **Full DMF initialization.** DMF's loader started and hooks were
   active (engine log messages flowed through `__print`), but we didn't
   verify that ALL DMF modules loaded correctly or that user mods work.
   The POC's bar was "prove it can start," not "prove full
   functionality."

5. **`Mods.original_require` and `Mods.lua.io` are stubs.** The POC
   implemented `c_require_stub` (logs + returns nil) and an empty
   `Mods.lua.io` table. DMF's `require` and `io` usage may need real
   implementations for full functionality.

6. **Multi-shot injection.** The POC's latch ensures only one successful
   injection. Production needs ongoing Lua execution (for mod update
   loops, event handling, etc.) — the current mechanism fires once.

7. **Antivirus compatibility.** DLL injection techniques trigger AV
   heuristics. No AV testing was done. Code signing and vendor
   whitelisting are untested mitigations.

---

## Metrics

| Metric | Value |
|--------|-------|
| Phases | 6 (0-5) |
| Stories proven | 6/6 (all P0 + P1) |
| Functions discovered | 16 (8 engine/LuaJIT + 8 C API) |
| Methodology corrections | 6 gaps discovered and fixed |
| Binary analysis (offline) | 7 addresses confirmed |
| Runtime discovery (in-process) | 7 addresses re-confirmed (`matched=7 mismatched=0`) |
| Live-game tests | 5 Tier B tests, all passed |
| A1 test count | 55 (Phase 5, the most comprehensive) |
| DMF dependencies | 6 globals, all implemented as C functions |
| Game stability | 60+ seconds of gameplay with DLL active |

---

## The Verdict

The POC proved that Lua VM Injection is a viable approach for Darktide
mod loading. The hardest engineering challenges — runtime function
discovery in a statically-linked LuaJIT, capturing the `lua_State`,
bypassing the sandboxed environment — are all solved and validated in
the live game.

The approach delivers on its core promise: eliminating the
`bundle_database.data` fragility that historically breaks mods on every
game update. The bundle system is completely bypassed.

The one gap between the POC and the production goal (zero game-directory
footprint) is the injection delivery mechanism. The POC proved
everything that matters inside the process; the zero-footprint delivery
(`CreateRemoteThread`) is lower-risk engineering that remains to be
built and tested.
