# Production Spec: Technical Grounding for Next Steps

> **Status:** Planning document. Ties each production work item to
> specific POC findings, confirmed addresses, and technical constraints.
> Not an app design — the next session decides architecture. This doc
> ensures that decisions are grounded in what the POC proven.
>
> **Prerequisite reading:** `docs/production-summary.md` (high-level
> overview), `docs/poc/lua-vm-injection-anchors.md` (confirmed
> addresses and methodology).

---

## Tier 1: Core

### 1.1 CreateRemoteThread Injection (zero-footprint delivery)

**What the POC proved:** The proxy DLL approach (dbghelp.dll in
binaries/) works on both Linux/Proton and Windows native. The DLL's
internal work — discovery, hooks, bootstrap — is fully validated and
injection-mechanism-agnostic.

**What production needs:** Replace the proxy DLL with launcher-based
`CreateRemoteThread` injection. The mod manager creates
`Darktide.exe` with `CREATE_SUSPENDED`, injects the DLL from a staging
directory, then resumes. Zero files in the game directory.

**Technical details from the POC:**
- The DLL currently enters via `DllMain` (`DLL_PROCESS_ATTACH`) —
  same entry point for both proxy DLL and CreateRemoteThread
- `DllMain` installs hooks in this order: (1) `lua_newstate` hook
  via MinHook, (2) discovery worker thread (32MB stack), (3)
  `lua_pcall` hook + inject mechanism
- The proxy DLL requires 200 export forwarders to `dbghelp.dll`.
  CreateRemoteThread injection eliminates this entirely — the DLL
  doesn't need to masquerade as anything
- On Linux/Proton: if CreateRemoteThread has issues, the proxy DLL +
  `WINEDLLOVERRIDES="dbghelp=native,builtin"` approach is a proven
  fallback (see `poc/phase1-proxy-dll/`)

**Key constraint:** The `lua_newstate` hook MUST be installed before
the engine's `main()` calls `lua_newstate`. With `CREATE_SUSPENDED` +
inject + resume, the hook is active before any game code runs. This
is the same timing guarantee the proxy DLL provides via `DllMain`.

**Reference code:** `poc/phase4-execute-lua/src/dllmain.c` (current
DllMain flow), `poc/phase1-proxy-dll/` (proxy DLL implementation to
replace)

---

### 1.2 Full DMF Dependency Implementation

**What the POC proved:** DMF's 6 dependencies can be implemented as C
functions registered via `lua_pushcclosure` + `lua_setfield(L,
LUA_GLOBALSINDEX, name)`. The C-function bootstrap bypasses the
sandboxed `_G` entirely. DMF's loader (`dmf_loader.lua`) loads and
executes successfully.

**What production needs:** The POC stubbed two dependencies:
- `Mods.original_require` returns nil (logs + bails)
- `Mods.lua.io` is an empty table

Production needs real implementations.

**Technical details:**

**Mods.original_require** — The engine's `require` is sandboxed away
from our chunks (the engine replaces it with a wrapper during init at
`0x32a2a0`). Two options:
- (a) Find the engine's real `require` function pointer and call it
  from a C wrapper. The engine init code at `0x32a2a0` replaces
  `_G.require` — the original is likely stored or callable via the
  engine's bundle loading path (`lua_resource::bytecode` at
  `0x3298b0`/`0x32ab30`)
- (b) Implement `require` as a file-based module loader that reads
  from the staging directory. Simpler, but doesn't load engine
  modules — only mod modules. This is probably sufficient since DMF
  mods use `require` for their own files, not engine internals.

**Mods.lua.io** — DMF's `core/io.lua` module uses `io.open` for
settings save/load. Two options:
- (a) Implement `io.open`/`io.read`/`io.write`/`io.close` as C
  functions using Win32 APIs (`CreateFileW`, `ReadFile`, `WriteFile`).
  Full control, no sandbox dependency.
- (b) Find and expose the engine's `io` library. The engine called
  `luaL_openlibs` during init, which registered `io` into `_G`. The
  engine didn't replace `io` (only `print`, `require`, `dofile`,
  `loadfile`, `load`). So `io` exists in the engine's `_G` but our
  chunks can't see it because the environment is sandboxed. Finding
  the real `_G` table and copying `io` from it would work.

**Confirmed addresses for C-function registration** (from
`docs/poc/lua-vm-injection-anchors.md` §9):

| Function | RVA | Purpose |
|----------|-----|---------|
| `lua_pushcclosure` | `0xc74580` | Register C function as Lua callable |
| `lua_setfield` | `0xc74cb0` | Set table field (used as `lua_setglobal` macro) |
| `lua_pushstring` | `0xc747d0` | Push string onto Lua stack |
| `lua_tolstring` | `0xc75190` | Read string argument from Lua |
| `lua_createtable` | `0xc73ad0` | Create Lua table |
| `lua_type` | `0xc753b0` | Type-check argument |
| `lua_tonumber` | `0xc730c0` | Read numeric argument |
| `LUA_GLOBALSINDEX` | `-10002` | Pseudo-index for `_G` |

**Reference code:** `poc/phase5-dmf-bootstrap/src/inject.c` — contains
`c_print`, `c_dofile`, `c_loadstring`, `c_require_stub`, and the
`setup_mods_globals()` function that builds the Mods table.

---

### 1.3 Load Order Management

**What the POC proved:** DMF reads `mod_load_order.txt` from the
staging directory via `Mods.file.dofile` (our `c_dofile`). The file
format is simple: one mod name per line, `--` comments allowed.

**What production needs:** A UI that manages this file — drag-and-drop
reordering, enable/disable checkboxes, automatic dependency ordering.

**Technical details:**
- File path: `<staging>/mod_load_order.txt`
- Format: one mod folder name per line, `--` prefix for comments
- DMF always prepends `dmf` to the load order (hardcoded in
  `dmf_mod_manager.lua` via `table.insert(mod_load_order, 1, "dmf")`)
- The mod manager writes this file; DMF reads it
- No dependency metadata in the current format — dependencies are
  documented in mod READMEs and community knowledge. Production could
  extend the format or read dependency info from mod metadata files.

**Reference:** `docs/reference/darktide-framework-analysis.md` §Component:
Darktide-Mod-Loader — documents the current `mod_load_order.txt` format
and the `table.insert(mod_load_order, 1, "dmf")` behavior.

---

### 1.4 Staging Directory Management

**What the POC proved:** The DLL reads a staging path and resolves mod
files relative to it. `c_dofile` reads `.lua` files via Win32 APIs
(`CreateFileW`/`ReadFile`) and executes them via `luaL_loadbuffer` +
`lua_pcall`.

**What production needs:** The staging directory lives outside the
game folder. The mod manager populates it with:
- `dmf/` — DMF framework Lua files (from the DMF distribution)
- `mods/` — user-installed mods
- `mod_load_order.txt` — generated by the UI
- The hook DLL itself (for CreateRemoteThread injection)

**Technical details:**
- The POC used a `DARKTIDE_MOD_STAGING` environment variable, set by
  the launcher before creating the game process
- `c_dofile` resolves relative paths against this staging root and
  appends `.lua` (matching the original `mod_loader`'s
  `get_file_path` logic)
- The staging directory structure mirrors the current
  `<game>/mods/` layout so DMF's path expectations are unchanged:
  ```
  <staging>/
  ├── dmf/scripts/mods/dmf/dmf_loader.lua
  ├── dmf/scripts/mods/dmf/modules/...
  ├── <mod_name>/
  └── mod_load_order.txt
  ```

**Key constraint:** `c_dofile` currently appends `.lua` and resolves
relative to staging. If mod files have different extensions or
directory structures, the path resolution logic in `c_dofile` needs
to handle them.

---

### 1.5 Game Path Detection

**What the POC proved:** Not directly addressed (the POC hardcoded the
game path). The existing `dtkit-patch` tool demonstrates the approach.

**What production needs:** Automatic detection of the Darktide install
directory.

**Technical details:**
- Steam app ID: `1361210`
- The `dtkit-patch` Rust tool uses the `steam_find` crate to locate
  Steam installations and resolve app paths
- Xbox Game Pass path: via Windows registry (`winreg` crate)
- The game binary is at `<game>/binaries/Darktide.exe`
- The bundle directory is at `<game>/bundle/`

**Reference:** `docs/reference/darktide-framework-analysis.md` §Component:
dtkit-patch — documents the detection approach and CLI flags.

---

## Tier 2: Mod Management UX

### 2.1 Mod Enable/Disable

Toggling a mod = adding/removing its name from `mod_load_order.txt`.
No file deletion needed — disabled mods stay in staging, just not in
the load order. The mod manager writes the updated file before
launching.

### 2.2 Dependency Resolution

**POC learning:** The current modding ecosystem has NO dependency
metadata in the file format. Dependencies are community knowledge.
Production should define a metadata format (e.g., a `mod.json` or
`<mod>.toml` in each mod's directory) with declared dependencies, then
topologically sort the load order automatically.

**DMF constraint:** DMF's `dmf_mod_manager.lua` enforces that mods
can only be created during the loading phase. The load order file
must be complete and correct before launch — no runtime mod
installation.

### 2.3 Conflict Detection

**POC learning:** DMF's hook system (`core/hooks.lua`) chains multiple
hooks on the same function. Conflicts aren't errors — they're
expected. But two mods hooking the same function without knowing
about each other can produce unexpected behavior.

Production could statically analyze mod Lua source for
`mod:hook(CLASS.X, "method", ...)` patterns and flag overlaps. This
is pattern matching on source code, not runtime analysis.

---

## Tier 3: Polish

### 3.1 Code Signing

**POC learning:** The DLL is 3.2 MB and triggers no special Windows
Defender behavior on the test machine (no exclusion added). However,
DLL injection techniques are flaggable by AV heuristics, and behavior
may differ across AV products and definition updates.

Production should sign the DLL and launcher with a code signing
certificate. Submit to major AV vendors for whitelisting (Microsoft,
Bitdefender, Kaspersky, etc.). This is an ongoing relationship, not a
one-time fix.

### 3.2 Cross-Platform

**POC learning:** The proxy DLL works on both platforms. Key
differences:
- Windows: DLL search order finds the proxy in `binaries/`
  automatically. No launch option needed.
- Linux/Proton: `WINEDLLOVERRIDES="dbghelp=native,builtin"` required
  (plain `native` breaks export forwarding — Wine overrides are
  module-name-keyed, not path-keyed)
- The DLL, hooks, discovery, and bootstrap are identical on both
  platforms (same binary, same RVAs, same results)
- Wine resolved 197/200 dbghelp exports; Windows resolved all 200.
  The 3 unresolved on Wine aren't called during normal operation.

For CreateRemoteThread: Windows native is straightforward. Proton
supports `CreateRemoteThread` via Wine's process management, but
quirks are possible. The proxy DLL + WINEDLLOVERRIDES approach is the
proven fallback.

### 3.3 Game-Update Detection

**Risk level:** Low. String anchors are engine-framework code
(`stingray::` namespace), not game content. They only change in a
major engine overhaul, not content patches. The Stingray engine is
internally maintained by Fatshark with no external updates. Same
engine base runs Vermintide 2 — architecture stable for years.

The DLL's discovery engine reports what it found and what didn't
match. The mod manager can detect mismatches after a game update and
alert the user. Recovery: re-run the offline discovery tool against
the new binary, update source-pattern signatures if needed, ship a
new DLL.

---

## Architecture Decisions Already Locked

These were validated during research and POC. Reversing them would
require re-doing the POC:

1. **Lua VM Injection over Bundle Virtualization** — eliminates
   `bundle_database.data` fragility entirely
2. **C-function bootstrap** — DMF's 6 dependencies registered as C
   functions, bypassing the sandboxed `_G` (calling `luaL_openlibs`
   is destructive — verified)
3. **Source-pattern matching** for LuaJIT function discovery —
   reliable identification method (dynamic "first-to-fire" heuristic
   was unreliable — Phase 3 misidentified `lua_pcall`)
4. **Retry-on-error timing** — the injected chunk self-checks for
   readiness and retries on the engine's `lua_pcall` calls. Succeeded
   on first attempt in live game (1.3–2.4s after VM creation)
5. **DMF Lua files preserved as-is** — only the harness is replaced.
   DMF depends on exactly 6 globals from the harness

---

## POC Code Map

> **Reference only.** This map orients future work to what the POC
> already solved. The POC is a capability proof, not a pre-release;
> production is built from the ground up with its own testability and
> review bar. "Reusable" below is a statement of code quality — proven
> techniques worth consulting — not a prescription to adopt POC code
> as-is.

The `poc/` directory contains the working code from each phase. These
are disposable artifacts, not production code — but they contain
validated implementations that production can reference or adapt:

| Phase | Directory | What's There |
|-------|-----------|-------------|
| 0 | `phase0-offline-discovery/` | Python offline discovery script, addresses.json, report.md |
| 1 | `phase1-proxy-dll/` | Proxy DLL with dbghelp export forwarding (200 forwarders) |
| 2a | `phase2-runtime-discovery/` | Portable C discovery engine (10 TUs + capstone) |
| 2b | `phase2b-runtime-discovery/` | DLL integration (discovery worker thread, 32MB stack) |
| 3 | `phase3-state-capture/` | MinHook integration, lua_newstate hook, lua_State capture |
| 4 | `phase4-execute-lua/` | lua_pcall hook, retry-on-error injection, Lua execution |
| 5 | `phase5-dmf-bootstrap/` | C-function bootstrap, Mods table builder, dmf_loader loading |

**The code most worth referencing for production** (on the `poc`
branch — `git checkout poc -- <path>` to read specific files):
- `poc/phase5-dmf-bootstrap/src/inject.c` — the C-function implementations
  (`c_print`, `c_dofile`, `c_loadstring`, `setup_mods_globals()`)
- `poc/phase2-runtime-discovery/engine/` — the portable C discovery engine
- `poc/phase3-state-capture/src/phase3_hooks.c` — the MinHook hook
  installation code
- `poc/phase4-execute-lua/src/inject.c` — the retry-on-error mechanism and
  `lua_pcall` hook

**Binary SHA-256** (all addresses in this document are for this binary):
`132eed5fe58515774a41199269dd240ef6092f84b1efc8ad4a28e23ea6791661`

---

## Technical Reference: Darktide Binary Facts

> Immutable technical facts about the Darktide engine binary that any
> version of the mod manager needs. These are properties of the game,
> not of any particular implementation.

### lua_State field layout (Darktide build)

The Darktide binary uses LuaJIT 2.1 in non-GC64 mode (LJ_64, 32-bit
MRefs). The `lua_State` struct has these field offsets:

| Offset | Field | Type | How determined |
|--------|-------|------|----------------|
| `0x08` | `glref` (global_State*) | 4-byte MRef | `lua_pcall` disasm: `mov ebx, [rcx+8]` |
| `0x10` | `base` (TValue*) | 8-byte pointer | `lua_pcall` disasm: `mov r9, [rbx+0x10]` |
| `0x18` | `top` (TValue*) | 8-byte pointer | `lua_pcall` disasm: `mov rcx, [rcx+0x18]` |
| `0x24` | `stack` (TValue*) | 4-byte MRef | Stack-save in `lua_pcall` error handling |
| `0x38` | `stacksize` | integer | Stack-grow check in `lj_state_growstack` |

Stack slot size: **8 bytes** (TValue). Confirmed by `lua_gettop`'s
`(top - base) >> 3` computation.

**Note:** System LuaJIT on Linux dev machines may be GC64 (where
`top` is at offset `0x28`). The Darktide binary is non-GC64 — always
use offset `0x18` for `top` in production code.

### global_State field layout (partially mapped)

| Offset | Field | How determined |
|--------|-------|----------------|
| `0x61` | `hookmask` | `lua_pcall` hook save/restore: `movzx edi, byte [rbx+0x61]` |
| `0x118` | panic function slot | `lua_atpanic`: swaps `rdx` into `[g+0x118]` |
| `0xc0` | `tmptv` (temporary TValue) | Fallback for invalid errfunc in `lua_pcall` |

### PE section layout

| Section | RVA | File Offset | Raw Size | Purpose |
|---------|-----|-------------|----------|---------|
| `.text` | `0x1000` | `0x600` | 15,090,176 | All executable code |
| `.rdata` | `0xe68000` | `0xe65600` | 1,882,112 | Read-only data (strings, constants) |
| `.pdata` | `0x26d5000` | `0x10d3600` | 523,264 | Exception handling / function boundaries |

**`.rdata` RVA delta:** `0x2a00` — add this to any `.rdata` file offset
to get its RVA. (`delta = section RVA - section file offset = 0xe68000
- 0xe65600 = 0x2a00`)

### .pdata behavior and gap categories

`.pdata` contains 43,563 `RUNTIME_FUNCTION` entries (12 bytes each:
begin RVA, end RVA, unwind info RVA). **It is NOT a complete function
map.** Three categories of callable code fall in `.pdata` gaps:

1. **CFG/hot-patch thunks** — 5-byte `E9 rel32` + `cc` padding.
   Control Flow Guard thunks redirect to the real function body but
   have no `.pdata` entry. Discovery must follow the `E9` jump.
   Example: `lua_newstate` is invoked at thunk `0xc7c000`; real body
   at `0xc7eea0`.

2. **Leaf functions** — No prologue, unchanged stack pointer, `ret`-only
   epilogue. MSVC omits these from `.pdata`. Discovery must disassemble
   bytes directly and trim at the first `ret`.
   Examples: `lua_gettop` (`0xc74050`), `lua_atpanic` (`0xc77f40`).

3. **Import thunks** — `FF 25 disp32` (`jmp [rip+disp32]`). Redirect
   through the Import Address Table. Must be resolved via the import
   directory, not treated as function bodies.
   Example: `0xdf593c` → `VCRUNTIME140.dll!memmove`.

### LuaJIT identification

- **Version:** LuaJIT 2.1 (build `1771479498`)
- **Version string RVA:** `0xe8db08` (file offset `0xe8b108`)
- **Linking:** Statically linked into `Darktide.exe` — no separate
  `lua51.dll`. Functions are in `.text` but not exported.
- **Bytecode format string:** `luaJIT_BC_%s` present in `.rdata`

### Function discovery methodology

Two methods, used in combination:

**Method A — String-anchor discovery (for engine functions):**
1. The binary contains 724 `stingray::` namespaced function names in
   `.rdata` (used by the engine's own logging/assert system)
2. Find the target string in `.rdata` → get its RVA
3. Search `.text` for `lea reg, [rip + disp32]` instructions where
   `rip + 7 + disp32 == string RVA`
4. Binary-search `.pdata` for the function containing that instruction
5. If the instruction falls in a `.pdata` gap, use the gap-handling
   rules above

**Method B — Source-pattern matching (for LuaJIT C API functions):**
1. LuaJIT functions have no string anchors — they're internal
2. Match the compiled function body against the LuaJIT 2.1 source code
   (`lj_api.c`, `lj_state.c`, `lauxlib.c`, etc.)
3. Compare instruction-by-instruction: register usage, constant values,
   call targets, memory offsets
4. This is the reliable identification method — dynamic "first-to-fire"
   heuristics are unreliable (multiple functions fire during init)

**Finding the LuaEnvironment init function (two-step path):**
The `stingray::LuaEnvironment::Internal::lua_panic` string anchor leads
to `lua_panic`'s own body (it logs its own name), NOT to the init code.
The reliable path to init is:
1. Find `lua_panic` body via its string anchor
2. Search `.text` for LEA instructions referencing `lua_panic`'s address
   (the `&lua_panic` pointer passed to `lua_atpanic` during init)
3. The largest `.pdata` function containing such a LEA is
   `LuaEnvironment` init

### Sandboxed _G: root cause

The engine's script initialization function (at `0x32a2a0`, called from
`LuaEnvironment::init` at `0x32a8d0`) does the following:

1. Calls `luaL_openlibs(L)` — registers all standard Lua libraries
   (`print`, `io`, `table`, `string`, `math`, `require`, etc.) into `_G`
2. Replaces these specific globals with engine wrappers:
   - `_G.print` → engine print wrapper
   - `_G.require` → engine require wrapper
   - `_G.dofile` → engine dofile wrapper
   - `_G.loadfile` → engine loadfile wrapper
   - `_G.load` → engine load wrapper
3. Does NOT replace: `io`, `table`, `string`, `math`, `os`, `coroutine`,
   `debug`, `package`

**Implication for injected code:** `luaL_loadbuffer` chunks see the
default `_G`, which has the engine wrappers but may not have the
standard library functions depending on timing. Calling `luaL_openlibs`
again is **destructive** — it overwrites the engine's custom wrappers
with standard versions, crashing the game within 1 second.

**Solution:** Register DMF's dependencies as C functions via
`lua_pushcclosure` + `lua_setfield(L, LUA_GLOBALSINDEX, name)`. This
writes directly to `_G`, bypassing the sandbox. Same mechanism the
engine itself uses.

### Key LuaJIT constants and macros

| Name | Value | Notes |
|------|-------|-------|
| `LUA_GLOBALSINDEX` | `-10002` (`0xFFFFD8EE`) | Pseudo-index for `_G` |
| `LUA_REGISTRYINDEX` | `-10000` | Pseudo-index for the registry |
| `lua_pushcfunction(L, f)` | macro → `lua_pushcclosure(L, f, 0)` | Not a real function |
| `lua_setglobal(L, name)` | macro → `lua_setfield(L, LUA_GLOBALSINDEX, name)` | Not a real function |
| `sizeof(GCstr)` | `0x14` (20 bytes) | In LJ_64 non-GC64 builds. `strdata(s)` = `(char*)s + 0x14` |
| `LUA_TTAB` tag value | `0xFFFFFFF4` | `~11u` — table type tag |
| `LUA_MULTRET` | `-1` | Pass to `lua_pcall` for variable returns |

### Confirmed function addresses

All 16 functions confirmed at runtime in the live game on both
Linux/Proton and Windows native. Addresses are RVAs (relative to module
base). At runtime: `actual_address = module_base + RVA`.

| Function | RVA | Category | Notes |
|----------|-----|----------|-------|
| `lua_newstate` (thunk) | `0xc7c000` | Engine hook target | CFG thunk; what callers invoke |
| `lua_newstate` (body) | `0xc7eea0` | Real body | After CFG thunk follow |
| `lua_atpanic` | `0xc77f40` | Leaf function | No .pdata entry |
| `lua_gettop` | `0xc74050` | Leaf function | No .pdata entry |
| `luaL_loadbuffer` | `0xc7ad80` | Wrapper | Calls `lua_load` internally |
| `lua_pcall` | `0xc744c0` | Core | Calls `lj_vm_pcall` at `0x6845` |
| `luaL_openlibs` | `0xc7f380` | **Do NOT call** | Destructive — overwrites engine wrappers |
| `lua_pushcclosure` | `0xc74580` | C bootstrap | Register C function as Lua callable |
| `lua_setfield` | `0xc74cb0` | C bootstrap | Set table field (used as setglobal macro) |
| `lua_pushstring` | `0xc747d0` | C bootstrap | Push string onto stack |
| `lua_tolstring` | `0xc75190` | C bootstrap | Read string argument |
| `lua_createtable` | `0xc73ad0` | C bootstrap | Create Lua table |
| `lua_type` | `0xc753b0` | C bootstrap | Type-check argument |
| `lua_tonumber` | `0xc730c0` | C bootstrap | Read numeric argument |
| `lua_settop` | `0xc74f30` | Utility | Stack adjustment |
| `lua_panic` (body) | `0x328220` | Engine anchor | String anchor is self-referential |
| `LuaEnvironment` init | `0x32a660`–`0x32aa2f` | Engine | `&lua_panic` LEA at `0x32a86b` |
| `lj_vm_pcall` | `0x6845` | LuaJIT ASM entry | Callee of `lua_pcall`; dynasm `->vm_pcall:` |
| Engine script init | `0x32a2a0` | Engine | Calls openlibs then replaces globals |
