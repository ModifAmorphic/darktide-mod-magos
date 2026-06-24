# Lua VM Injection — Working Theory

> **Status:** Complete. Validated by POC (all stories passed).
>
> Architecture document for injecting DMF directly into the engine's
> LuaJIT VM via DLL injection, bypassing the bundle system entirely.
> Covers the injection mechanism, function discovery methodology,
> bootstrap approach (C-function bootstrap — sandbox solution), timing
> analysis, and DMF's dependency surface.
>
> Companion to `lua-vm-injection-anchors.md` (same directory) — raw evidence,
> string offsets, and function discovery data.

---

## Table of Contents

1. [Approach Overview](#1-approach-overview)
2. [End-to-End Sequence](#2-end-to-end-sequence)
3. [The Engine's Lua Layer](#3-the-engines-lua-layer)
4. [Injection Mechanism](#4-injection-mechanism)
5. [Function Discovery](#5-function-discovery)
6. [The Bootstrap Shim](#6-the-bootstrap-shim)
7. [Injection Timing](#7-injection-timing)
8. [What DMF Needs From Us](#8-what-dmf-needs-from-us)
9. [Risks and Unknowns](#9-risks-and-unknowns)
10. [What Must Be Verified During Implementation](#10-what-must-be-verified-during-implementation)

---

## 1. Approach Overview

### Name convention

Two deployment approaches were evaluated during research:

- **Bundle Virtualization** — hook file I/O APIs (`CreateFileW`) to
  virtualize `bundle_database.data` and the mod bundle. Mod code still
  enters the VM through the normal bundle execution path.
- **Lua VM Injection** *(this document)* — inject a DLL that locates the
  engine's `lua_State` and loads DMF directly via the LuaJIT C API.
  Bundles are bypassed entirely.

### Core idea

The Stingray engine creates a LuaJIT virtual machine early in its
startup sequence. That VM is managed by a C++ class called
`stingray::LuaEnvironment`. Once the VM exists and the engine has
registered its API (globals like `Managers`, `CLASS`, `require`,
`print`, etc.), we can load and execute our own Lua code in that same
VM using standard LuaJIT C API functions (`luaL_loadstring`,
`lua_pcall`, `lua_setglobal`).

The injected DLL's job is narrow and well-defined:

1. Get inserted into the game process (via the mod manager launcher)
2. Locate the engine's `lua_State` pointer
3. Wait for the right moment in the engine's startup
4. Set up a small number of globals that DMF expects
5. Load and execute the DMF bootstrap Lua file from a staging directory

After step 5, DMF's own initialization takes over — it loads its
modules, hooks game functions, and manages user mods exactly as it does
today. The DLL's active role is essentially complete once DMF is
running.

### What this eliminates

- **No `bundle_database.data` patching.** The engine reads its own
  unmodified database. The format fragility that historically causes
  post-update crashes is completely out of the picture.
- **No patch_999 bundle, no trampoline, no `mod_loader` file.** The
  entire bundle-based bootstrap chain is replaced by direct VM
  injection.
- **No game-directory footprint.** Nothing is written to the game
  directory. All mod files live in a staging directory managed by the
  mod manager.
- **No Steam verification breakage.** Since nothing touches the game
  directory, Steam file verification has no effect on the mod system.

---

## 2. End-to-End Sequence

```
┌─────────────────────────────────────────────────────────────┐
│  MOD MANAGER (launcher)                                      │
│                                                               │
│  1. User clicks "Launch Modded"                               │
│  2. Manager prepares staging directory                        │
│     - DMF Lua files                                           │
│     - User mods                                               │
│     - mod_load_order.txt                                      │
│     - bootstrap.lua (the shim)                                │
│  3. Manager sets env var: DARKTIDE_MOD_STAGING=<path>         │
│  4. Manager creates Darktide.exe (CREATE_SUSPENDED)           │
│  5. Manager injects hook DLL via CreateRemoteThread           │
│  6. Manager resumes the main thread                           │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  INJECTED DLL (inside Darktide.exe process)                   │
│                                                               │
│  DllMain runs (game is suspended, then resumed):              │
│                                                               │
│  7. Read staging path from DARKTIDE_MOD_STAGING env var       │
│  8. Install function discovery hooks                          │
│     - Parse PE headers (.text, .rdata, .pdata sections)       │
│     - Find anchor strings in .rdata                           │
│     - Locate engine functions via string → xref → .pdata      │
│     - Disassemble engine functions to find LuaJIT call sites  │
│     - Capture addresses: lua_newstate, luaL_loadstring,      │
│       lua_pcall, lua_getglobal, lua_setglobal                 │
│  9. Hook lua_newstate (or the engine's VM creation point)     │
│     - When the engine creates the VM, capture lua_State*      │
│ 10. Hook a post-init anchor to detect when the engine's       │
│     Lua API registration is complete                          │
│     (see §7 for timing analysis)                              │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  ENGINE STARTUP (Darktide.exe main thread, resumed)           │
│                                                               │
│ 11. Engine initializes Stingray subsystems                    │
│ 12. Engine creates LuaEnvironment                             │
│     → calls lua_newstate → our hook fires                    │
│     → we now have the lua_State*                              │
│ 13. Engine registers its Lua API                              │
│     - Sets globals: Managers, CLASS, require, print, etc.    │
│     - Registers engine types and functions                    │
│ 14. Engine begins loading game scripts from bundles           │
│     → our post-init hook detects this point                   │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  BOOTSTRAP INJECTION (DLL, triggered by timing hook)          │
│                                                               │
│ 15. Verify the engine's globals exist (Managers, CLASS, etc.)│
│ 16. Set up the bootstrap shim via Lua C API:                  │
│     - Create Mods global table                                │
│     - Set Mods.file.dofile (reads from staging)               │
│     - Set Mods.lua.loadstring, Mods.lua.io                    │
│     - Set Mods.require_store = {}                             │
│     - Set Mods.original_require = the engine's require        │
│     - Set __print = the engine's print                        │
│ 17. Load bootstrap Lua from staging:                          │
│     - luaL_loadstring(L, file_contents)                      │
│     - lua_pcall(L, 0, 0, 0)                                   │
│ 18. Bootstrap loads DMF loader from staging                   │
│ 19. DMF initializes, hooks game functions, loads user mods    │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│  STEADY STATE — DMF is running                                │
│                                                               │
│  • DMF hooks StateGame.update, GameStateMachine._change_state│
│    (same hooks as the current mod_loader)                     │
│  • DMF update loop fires each frame via the engine's         │
│    "Lua->update" callback                                     │
│  • User mods are loaded and running                            │
│  • The DLL's hooks remain installed but mostly idle           │
└─────────────────────────────────────────────────────────────┘
```

---

## 3. The Engine's Lua Layer

### What we confirmed from binary analysis

The engine's Lua integration is structured around a C++ class called
`stingray::LuaEnvironment`. The following was confirmed from strings
and build paths embedded in `Darktide.exe`:

**Source files (build paths in binary):**

```
stingray/runtime/application/script/lua_environment.cpp
stingray/runtime/application/script/lua_resource.cpp
stingray/runtime/application/script/lua_stack.h
stingray/runtime/application/performance/lua_memory.cpp
stingray/runtime/application/script/interface/if_lua_memory.cpp
```

**Named functions (C++ symbols in binary, usable as discovery anchors):**

| Function | Purpose |
|----------|---------|
| `stingray::LuaEnvironment::Internal::lua_panic` | Panic handler — receives `lua_State*` as parameter. Registered via `lua_atpanic` during init. |
| `stingray::LuaEnvironment::Internal::default_error_callback` | Default error callback for Lua errors. |
| `stingray::LuaEnvironment::clear_temp_variables` | Clears temporary Lua variables (housekeeping). |
| `LuaEnvironment::dump_state` | Dumps Lua state for debugging. |
| `stingray::LuaMemory::ids_by_filter` | Lua memory tracking by filter. |
| `stingray::LuaMemory::read_from_file` | Reads Lua memory data from file. |
| `stingray::lua_resource::bytecode` | Lua bytecode resource type — how compiled Lua enters the VM from bundles. |
| `stingray::script_interface::copy_lua_variable_to_c` | Lua→C variable bridge. |
| `stingray::script_interface::push_c_variable_to_lua` | C→Lua variable bridge. |

**LuaJIT version:** `LuaJIT 2.1.1771479498` (at file offset `0x00e8b108`).
This is an open-source runtime with a stable, documented C API.

**Single VM:** Evidence points to a single `LuaEnvironment` (no evidence
of multiple separate Lua VMs). The string *"at the Game level. Set a
callback in the lua environment using"* suggests one main environment
at the game level.

**Update loop:** The string `"Lua->update"` confirms the engine calls a
Lua update function each frame. The string *"Deadlock detected. Update
was not called for %f seconds. Triggering crash inside Lua: %s"*
confirms the update loop has deadlock detection.

### What this means for injection

The `LuaEnvironment` class is the primary anchor. Its initialization
function (not yet named in the binary, but locatable via the panic
function it registers) calls `lua_newstate` and stores the resulting
`lua_State*`. The panic function (`lua_panic`) receives the
`lua_State*` as its sole parameter, which means tracing its
registration site leads directly to the VM creation code.

---

## 4. Injection Mechanism

### Windows (native)

The standard CreateRemoteThread + LoadLibrary pattern:

1. `CreateProcessW(Darktide.exe, ..., CREATE_SUSPENDED, ...)`
2. `VirtualAllocEx(handle, NULL, path_size, MEM_COMMIT, PAGE_READWRITE)`
3. `WriteProcessMemory(handle, remote_buf, dll_path, path_size, NULL)`
4. `CreateRemoteThread(handle, NULL, 0, LoadLibraryW, remote_buf, 0, NULL)`
5. `WaitForSingleObject(thread, INFINITE)` — DLL loads, `DllMain` runs
6. `ResumeThread(main_thread)` — game starts running

This is textbook injection. The DLL's `DllMain` installs all hooks
before the game's main thread resumes, so the hooks are active before
any engine code runs.

### Linux/Proton

Two options:

**Option A — Same CreateRemoteThread injection:** Wine/Proton
implements the Windows process management APIs, including
`CreateRemoteThread`. The same injection code works if the manager
itself runs under Wine/Proton alongside the game.

**Option B — WINEDLLOVERRIDES (simpler):** If the DLL is named to
match a dependency the game loads, Wine's DLL override mechanism can
load it automatically without injection. The manager sets
`WINEDLLOVERRIDES` as an environment variable before launching the
game through Proton. No `CreateRemoteThread` needed.

Option B is simpler but requires the DLL to masquerade as a system
DLL (e.g., `winhttp.dll`, `dbghelp.dll`). Option A is more flexible
and keeps the DLL name arbitrary.

---

## 5. Function Discovery

This is the most technically complex part of the approach. The DLL
must locate specific function addresses inside `Darktide.exe` at
runtime, without hardcoded offsets.

### Two categories of functions

**Category A — Engine functions** (found via string anchors):

These functions have their names embedded in the binary as assert/log
strings. Discovery is deterministic:

1. Find the name string in `.rdata` (e.g.,
   `"stingray::LuaEnvironment::Internal::lua_panic"`)
2. Search `.text` for instructions that reference that string's
   address (typically `lea reg, [rip + offset]`)
3. Look up the referencing address in `.pdata` to get the containing
   function's start address

This works regardless of compilation changes. The string is the
anchor; the bytes don't matter.

**Category B — LuaJIT C API functions** (found by tracing from
Category A):

LuaJIT's functions (`lua_newstate`, `luaL_loadstring`, `lua_pcall`,
etc.) do NOT have human-readable name strings. They must be found by
tracing calls from Category A engine functions:

1. Find the Category A engine function (e.g., `LuaEnvironment` init)
2. Disassemble the function using a runtime disassembler (Capstone)
3. Walk instructions looking for `call` opcodes
4. Compute call target addresses
5. Identify which target is the LuaJIT function we need (by examining
   the target function's behavior — e.g., `lua_newstate` allocates
   memory and initializes a known structure)

This is more complex than Category A and is the primary engineering
challenge. The disassembly and call-target analysis adds ~300-500
lines of code plus the Capstone dependency (~200KB).

### The .pdata advantage (with caveats)

`Darktide.exe` has a `.pdata` section of 522 KB (43,563 function
entries). Each entry is a `RUNTIME_FUNCTION` record (12 bytes) mapping
a function's start RVA, end RVA, and unwind info. This gives us a
function boundary map for most functions — **but it is NOT complete.**

Phase 0 analysis verified three categories of callable code that fall
in `.pdata` gaps: CFG/hot-patch thunks (`E9 rel32`), leaf functions
(no stack frame), and import thunks (`FF 25`). Discovery must handle
these separately — see the anchors doc §1 and §7 for details.

### Functions we need and how to find each

| Function | Category | Discovery method |
|----------|----------|-------------------|
| `lua_State*` (the VM pointer) | B | Hook `lua_newstate`; capture return value. Find `lua_newstate` by tracing from `LuaEnvironment` init code. |
| `luaL_loadstring` / `luaL_loadbuffer` | B | Trace from `lua_resource::bytecode` (which loads Lua source/bytecode). |
| `lua_pcall` | B | Trace from the engine's script execution path. |
| `lua_setglobal` / `lua_getglobal` | B | Trace from `script_interface::push_c_variable_to_lua`. |
| `LuaEnvironment` init | A | String anchor: `"stingray::LuaEnvironment::Internal::lua_panic"` → trace backward to the function that registers it via `lua_atpanic`. |
| `lua_resource::bytecode` | A | String anchor: `"stingray::lua_resource::bytecode"`. |
| Engine update callback | A | String anchor: `"Lua->update"`. |

### Alternative: pattern scanning (fallback)

If tracing from Category A functions fails (e.g., calls are indirect
via function pointers), we fall back to pattern scanning — matching
known LuaJIT 2.1 byte signatures. This is heuristic, not deterministic,
but LuaJIT 2.1 is stable and signatures are well-documented in the
modding community. This is a safety net, not the primary approach.

---

## 6. The Bootstrap Shim

Before DMF can initialize, the DLL must set up a small number of Lua
globals that DMF expects. These are the ONLY things DMF needs from
the harness — the rest DMF provides itself.

### What the shim sets up

```lua
-- These are set by the DLL via lua_setglobal before loading DMF:

Mods = {
    file = {
        dofile = <function>      -- Reads and executes a Lua file from staging
    },
    lua = {
        loadstring = loadstring, -- Standard Lua loadstring
        io = io,                 -- Standard Lua io library
    },
    require_store = {},          -- Empty table; DMF's require module populates it
    original_require = require,  -- The engine's require function
    message = {
        notify = <function>,     -- Optional: notification helper
        echo = <function>,       -- Optional: chat echo helper
    }
}

__print = print                  -- Backup of the engine's print function
```

### What `Mods.file.dofile` does

This is the critical function. Its implementation in the current
`mod_loader` (lines 198-201) is:

```lua
local function mod_dofile(file_path, args)
  return handle_io(file_path, nil, nil, args, true, "exec_result")
end
```

Which boils down to:

1. Construct a full path from the staging directory + `file_path`
2. `io.open(path, "r")`
3. Read all contents
4. `loadstring(contents, path)`
5. Execute the resulting function with `args`
6. Return the result

The DLL can implement this as either:
- A **Lua C function** registered via `lua_pushcfunction` +
  `lua_setglobal` (the DLL reads files using Windows APIs and pushes
  results via the Lua C API)
- A **Lua function** loaded via `luaL_loadstring` that uses
  `io.open` and `loadstring` — **but only after the environment issue
  is resolved** (see correction below)

> **⚠ Phase 4-5 finding — the default _G is sandboxed. SOLVED via C-function bootstrap.**
>
> The original version of this section assumed "the engine's Lua
> already has `io` and `loadstring` available." **This is FALSE for
> directly-injected code.** Phase 4 verified that `print`, `io.open`,
> `require`, and the FFI module are all inaccessible from chunks loaded
> via `luaL_loadbuffer` in the default environment.
>
> **Root cause (Phase 5):** The engine DOES call `luaL_openlibs` during
> its own init (at `0x32a2a0`), then replaces `_G.print`, `_G.require`,
> `_G.dofile`, `_G.loadfile`, `_G.load` with engine wrappers. Our
> injected chunks see a stripped `_G`. Re-calling `luaL_openlibs`
> ourselves is **destructive** — it overwrites the engine's custom
> wrappers, crashing the game within 1 second (verified).
>
> **Solution (Phase 5 — PROVEN in live game): C-function bootstrap.**
> Implement each of DMF's 6 dependencies as a C function in the DLL,
> registered as a Lua global via `lua_pushcclosure` + `lua_setfield(L,
> LUA_GLOBALSINDEX, name)`. This writes directly to `L->gt`, bypassing
> the sandboxed `_G` entirely. It's the same mechanism the engine
> itself uses. Proven in the live game: a C function registered as a
> global was called from a Lua chunk (`pcall_rc=0`, game stable).
>
> The 6 dependencies implemented as C functions:
> - `c_print` → `__print` (writes to log)
> - `c_dofile` → `Mods.file.dofile` (reads files via Win32 APIs,
>   executes via `luaL_loadbuffer` + `lua_pcall`)
> - `c_loadstring` → `Mods.lua.loadstring` (compiles via `luaL_loadbuffer`)
> - `c_require_stub` → `Mods.original_require` (logs + returns nil;
>   engine's `require` is sandboxed away)
> - `Mods.require_store` = empty table
> - `Mods.lua.io` = empty table (minimal POC)
>
> With this approach, the Lua-function implementation of
> `Mods.file.dofile` is NOT used — the C function replaces it entirely,
> avoiding the need for `io.open` and `loadstring` in the Lua environment.

---

## 7. Injection Timing

### The problem

We must inject DMF code into the VM at the right moment:

- **Too early:** The engine's globals (`Managers`, `CLASS`, `require`,
  `print`) don't exist yet. DMF will fail to initialize because it
  depends on these.
- **Too late:** Game scripts have already run. Some systems may have
  cached function references that DMF needs to hook, making hooking
  ineffective.

### The window

The engine's startup sequence (from Lua's perspective) is:

```
1. lua_newstate — VM created (no globals yet)
2. Engine registers its Lua API (Managers, CLASS, require, print, etc.)
3. Engine loads scripts/main.lua (the game's entry point)
4. main.lua sets up the game state machine
5. StateRequireScripts loads required scripts
6. StateGame begins the main loop
```

The safe injection window is **between step 2 and step 5** — after the
API is registered, before or during script loading.

### How to detect the window

**Approach A — Hook `lua_resource::bytecode` (Category A anchor):**

The engine loads each script via `lua_resource::bytecode`. The first
call to this function means the engine is starting to load scripts,
which means the API registration (step 2) is complete. Hook this
function, and on first invocation, run the bootstrap.

**Approach B — Check for a known global:**

Poll `lua_getglobal(L, "Managers")` after each engine callback. When
it returns non-nil, the API is registered. This is simpler but
requires a polling mechanism (e.g., hook a function called during
startup and check on each call).

**Approach C — Hook `main.lua` loading directly:**

The engine loads `scripts/main.lua` early. If we hook the bytecode
loading function and detect when `main.lua` is being loaded, we can
inject just before or after it. This is the most precise timing.

Approach A or C is recommended for investigation. Both use Category A
string anchors for discovery.

### What the current mod_loader tells us about timing

The current `mod_loader` (the modified `main.lua`) hooks these Lua
functions, which tells us about the engine's Lua lifecycle:

```lua
-- From binaries/mod_loader, line 240-284:
Mods.hook.set("Base", "_G.CLASS.StateRequireScripts._require_scripts", ...)
Mods.hook.set("Base", "_G.CLASS.StateGame.update", ...)
Mods.hook.set("Base", "_G.CLASS.GameStateMachine._change_state", ...)
```

These hooks are set up inside `Main.init()`, which runs after the
engine's API is fully registered. This confirms that by the time
`Main.init()` runs, the VM is ready and all globals exist. Our
injection point should be at or before `Main.init()`.

---

## 8. What DMF Needs From Us

### Exhaustive dependency list

This was determined by grepping all DMF Lua files for `Mods.*`
references. The complete list:

| Reference | Where used | What it does |
|-----------|-----------|--------------|
| `Mods.file.dofile` | `dmf.mod`, `dmf_loader.lua` | Loads and executes a Lua file from disk |
| `Mods.lua.loadstring` | `core/hooks.lua` | Compiles Lua source strings (for the hook chain system) |
| `Mods.lua.io` | `core/hooks.lua` | Reference to Lua's `io` library |
| `Mods.require_store` | `core/require.lua` | Table storing instances of required modules |
| `Mods.original_require` | `core/require.lua` | The engine's original `require` function |
| `__print` | `dmf_loader.lua` | Backup of the original `print` function |

That's the entire dependency surface. Six items. All are trivially
reproducible by the bootstrap shim (see §6).

### What DMF provides itself

Everything else DMF needs, it builds during its own initialization:

- **Hook system** (`DMFMod:hook`, `:hook_safe`, `:hook_origin`) —
  implemented in `core/hooks.lua`, uses `Mods.lua.loadstring`
- **Require tracking** (`mod:hook_require`) — implemented in
  `core/require.lua`, uses `Mods.require_store` and
  `Mods.original_require`
- **Mod manager** (`new_mod`, `get_mod`) — implemented in
  `dmf_mod_manager.lua`
- **Logging** (`mod:info`, `:warning`, `:error`) — implemented in
  `core/logging.lua`, uses `__print`
- **Options, keybindings, events, settings, etc.** — all
  self-contained in their respective modules

The DLL does NOT need to provide any of these. DMF builds them.

### User mod loading

DMF loads user mods the same way it does today — by reading
`mod_load_order.txt` from the staging directory and executing each
mod's `.mod` file. The `.mod` file calls `new_mod("mod_name")` to
register itself. This mechanism is entirely within DMF and doesn't
require DLL involvement.

The only difference from today: the load order file and mod files
live in the staging directory instead of the game directory.

---

## 9. Risks and Unknowns

### Confirmed low risk

- **No anti-cheat.** Client-side EAC is not active (server-side only
  via EOS). DLL injection is not detected. See
  `DEPLOYMENT_OPTIONS_SURVEY.md` §2 for full analysis.
- **Single Lua VM.** Evidence points to one `LuaEnvironment`, not
  multiple. We only need to find and hook one `lua_State`.
- **LuaJIT 2.1 is stable.** The C API hasn't changed materially in
  years. Pattern signatures (if needed as fallback) are reliable.
- **DMF's dependency surface is tiny.** Six items, all simple. The
  bootstrap shim is straightforward.

### Medium risk — requires investigation during implementation

- **Injection timing precision.** We have a theory (inject after API
  registration, before `StateRequireScripts`) but haven't verified
  the exact C++ sequence. The timing hooks (§7) need prototyping.

- **LuaJIT function discovery via disassembly.** Tracing from engine
  functions to LuaJIT call targets requires runtime disassembly
  (Capstone). This is established technique but adds complexity. If
  calls are indirect (function pointers, vtables), tracing becomes
  harder and may require pattern scanning as fallback.

- **Engine function internal stability.** String anchors always get
  us to the right engine function. But the internal structure of
  that function (where it calls LuaJIT functions) could change across
  updates, requiring re-analysis of call sites.

### Low risk but worth noting

- **Antivirus false positives.** DLL injection uses techniques similar
  to malware. This is an ongoing support burden, not a one-time fix.
  Code signing and AV vendor whitelisting are the mitigations.
  (Accepted risk — other modding tools deal with the same.)

- **Fatshark could enable client-side EAC.** The EOS EAC plumbing is
  compiled into the binary. If enabled in a future update,
  injection-based approaches would stop working. The fallback is
  Bundle Virtualization. (Accepted risk.)

### Unknown — cannot assess without prototyping

- **Whether `lua_newstate` is called directly or through an
  allocator/factory pattern.** If the engine uses a custom allocator
  or wraps `lua_newstate` in a factory, the tracing approach needs to
  account for that.

- **Whether the engine validates the source of loaded Lua code.**
  Does the engine check if a script came from a bundle vs. being
  injected at runtime? Unlikely (the Lua VM doesn't distinguish), but
  the engine could theoretically tag loaded scripts.

- **Cross-process injection on Proton.** `CreateRemoteThread` works
  under Wine, but the exact mechanics of injecting from a native
  Linux process into a Wine process may have quirks. The
  `WINEDLLOVERRIDES` approach sidesteps this entirely.

---

## 10. What Must Be Verified During Implementation

Ordered by priority:

### P0 — Showstopper if it fails

1. **Can we capture the `lua_State*`?**
   - Locate `LuaEnvironment` init via the `lua_panic` string anchor
   - Trace to `lua_newstate` call
   - Hook it and capture the return value
   - If this fails, the entire approach fails

2. **Can we execute arbitrary Lua via the C API?**
   - With the captured `lua_State*`, call `luaL_loadstring` +
     `lua_pcall` on a test string (`print("hello from injected DLL")`)
   - If this works, the fundamental approach is proven

### P1 — Needed for a working prototype

3. **Can we detect the correct injection timing?**
   - Hook `lua_resource::bytecode` or monitor for `Managers` global
   - Verify the engine's globals exist at the detected point
   - Verify DMF can initialize successfully at that point

4. **Can we set up the bootstrap globals?**
   - Set `Mods` table, `__print`, etc. via `lua_setglobal`
   - Verify DMF's `Mods.file.dofile` works (reads from staging)

### P2 — Needed for production

5. **Cross-platform injection** (Windows CreateRemoteThread + Proton
   WINEDLLOVERRIDES or equivalent)

6. **Engine update resilience** — verify function discovery works
   across at least one game update

7. **Antivirus compatibility** — test with Windows Defender and
   common third-party AV

---

## References

- `DEPLOYMENT_OPTIONS_SURVEY.md` — broader landscape of approaches
- `docs/reference/analysis-verification.md` — verification of the framework analysis
- `docs/reference/darktide-framework-analysis.md` — the original framework analysis
- `the reverse-engineering documentation (darktide-re-docs)bundles/bundle-format.md`
  — bundle format documentation
- `the reverse-engineering documentation (darktide-re-docs)engine/stingray-overview.md`
  — engine overview
- LuaJIT 2.1 C API reference: https://luajit.org/extensions.html
