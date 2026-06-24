# Lua VM Injection — Technical Anchors

> Raw evidence and discovery data for the Lua VM Injection approach.
> This document contains the precise binary offsets, function names,
> and PE layout data needed for implementation. All data was extracted
> from the live modded install at
> `/games/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe`.
>
> **Binary analyzed:** `Darktide.exe`, 18,715,784 bytes (17.8 MB)
>
> **Companion document:** `lua-vm-injection-theory.md` (same directory)

---

## Table of Contents

1. [PE Section Layout](#1-pe-section-layout)
2. [LuaJIT Identification](#2-luajit-identification)
3. [Primary Anchor Strings (with offsets)](#3-primary-anchor-strings-with-offsets)
4. [Engine Lua-Related Functions](#4-engine-lua-related-functions)
5. [LuaJIT Error Message Anchors](#5-luajit-error-message-anchors)
6. [Engine File I/O Imports](#6-engine-file-io-imports)
7. [Discovery Methodology](#7-discovery-methodology)
8. [PE → RVA Conversion](#8-pe--rva-conversion)

---

## 1. PE Section Layout

```
Idx  Name      Size       VMA               File Off     Align
 0   .text     0x00e6417f 0x140001000       0x00000600   2**4
 1   BINK      0x00000cb5 0x140e66000       0x00e64800   2**2
 3   .rdata    0x001cb732 0x140e68000       0x00e65600   2**4
 4   .data     0x000a2800 0x141034000       0x01030e00   2**4
 5   .pdata    0x0007fa04 0x1426d5000       0x010d3600   2**2
 6   .rodata   0x00000790 0x142755000       0x01153200   2**2
 7   _RDATA    0x00013210 0x142756000       0x01153a00   2**2
 8   .rsrc     0x00063174 0x14276a000       0x01166e00   2**2
 9   .reloc    0x0000cb94 0x1427ce000       0x011ca000   2**2
```

### Key sections for function discovery

| Section | Purpose | Why it matters |
|---------|---------|----------------|
| `.text` | Code (14.7 MB) | Where all function bodies live. This is what we disassemble. |
| `.rdata` | Read-only data (1.8 MB) | Where anchor strings live. Search this for known names. |
| `.pdata` | Exception data (522 KB) | Contains 43,563 `RUNTIME_FUNCTION` entries mapping function start/end RVAs. Used for function boundary lookup — **but NOT a complete map.** See note below. |

### .pdata is NOT a complete function map

**Verified by Phase 0 analysis.** Three categories of callable code
fall in `.pdata` gaps (addresses not covered by any `RUNTIME_FUNCTION`
entry):

1. **CFG/hot-patch thunks** — 5-byte `E9 rel32` + `cc` padding. Control
   Flow Guard thunks redirect to the real function body but have no
   `.pdata` entry of their own. Example: `lua_newstate` is invoked at
   thunk `0xc7c000` whose real body is at `0xc7eea0`. **Discovery must
   follow the thunk jump.**

2. **Leaf functions** — Functions with no prologue, unchanged stack
   pointer, and `ret`-only epilogue. MSVC omits these from `.pdata`.
   Examples: `lua_gettop` (`0xc74050`), `lua_atpanic` (`0xc77f40`).
   **Discovery must disassemble bytes directly and trim at the first
   `ret`.**

3. **Import thunks** — `FF 25 disp32` (`jmp [rip+disp32]`) entries that
   redirect through the Import Address Table (IAT). **Must be resolved
   through the import directory**, not treated as function bodies.
   Example: `0xdf593c` → `VCRUNTIME140.dll!memmove`.

When a call target falls in a `.pdata` gap, check for these three
patterns before concluding the target is unidentifiable.

### .pdata entry format (Windows x64)

Each `.pdata` entry is 12 bytes:

```
struct RUNTIME_FUNCTION {
    uint32_t BeginAddress;   // RVA of function start
    uint32_t EndAddress;     // RVA of function end (exclusive)
    uint32_t UnwindData;     // RVA of UNWIND_INFO
};
```

To find the function containing a given code address, binary-search the
`.pdata` table for the entry where `BeginAddress <= addr < EndAddress`.
**If no entry contains the address, see the gap-handling note above.**

---

## 2. LuaJIT Identification

**Version string:** `LuaJIT 2.1.1771479498`
**File offset:** `0x00e8b108`
**Context at offset:** `LuaJIT 2.1.1771479498\x00\x00\x00jit.profile`

**Implications:**
- This is LuaJIT 2.1 (the current stable branch). The C API is stable
  and documented at https://luajit.org/extensions.html
- The number `1771479498` is likely a build timestamp/identifier
- LuaJIT is **statically linked** (no `lua51.dll` or similar in the
  import table). Its functions are inside `.text` but not exported.
- The `luaJIT_BC_%s` format string (used by the bytecode loader) is
  also present in the binary, confirming LuaJIT bytecode support.

---

## 3. Primary Anchor Strings (with offsets)

These are the strings to search for in `.rdata` during function
discovery. Each string is referenced by a specific function in `.text`;
finding that reference leads to the function.

### LuaEnvironment anchors (highest priority)

| Offset | String | Purpose |
|--------|--------|---------|
| `0x00f1f698` | `stingray::LuaEnvironment::Internal::lua_panic` | Panic handler — receives `lua_State*`. Registered via `lua_atpanic` during init. **Primary anchor for finding VM creation.** |
| `0x00f1f910` | `stingray::LuaEnvironment::Internal::default_error_callback` | Error callback. Near the panic handler in the code. |
| `0x00f1fa48` | `stingray::LuaEnvironment::clear_temp_variables` | Housekeeping function. |
| `0x00f1fc98` | `LuaEnvironment::dump_state` | Debug state dumper. |
| `0x00f1f4f8` | `lua_environment_api` | API registration identifier. |

### Script loading anchors

| Offset | String | Purpose |
|--------|--------|---------|
| `0x00f1d9d0` | `stingray::lua_resource::bytecode` | Lua bytecode resource type. The function that loads bytecode into the VM. **Anchor for finding `luaL_loadbuffer`.** |
| `0x00f266c8` | `stingray::script_interface::copy_lua_variable_to_c` | Lua→C bridge. **Anchor for finding `lua_getglobal` / `lua_tovalue`.** |
| `0x00f267f8` | `stingray::script_interface::push_c_variable_to_lua` | C→Lua bridge. **Anchor for finding `lua_setglobal` / `lua_pushvalue`.** |
| `0x00f51ef8` | `load_script_data` | Script data loading. |
| `0x00f520b8` | `Lua->update` | The per-frame Lua update callback. |

### Bundle / resource anchors

| Offset | String | Purpose |
|--------|--------|---------|
| `0x00f50bc0` | `stingray::Bundle::open` | Bundle file opening. |
| `0x00f4e298` | `bundle_database.data` | The database filename the engine looks for. |

### Engine build paths (for understanding code organization)

```
stingray/runtime/application/script/lua_environment.cpp
stingray/runtime/application/script/lua_resource.cpp
stingray/runtime/application/script/lua_stack.h
stingray/runtime/application/performance/lua_memory.cpp
stingray/runtime/application/script/interface/if_lua_memory.cpp
stingray/runtime/foundation/resource/bundle.cpp
```

---

## 4. Engine Lua-Related Functions

Complete list of `stingray::` namespaced function names found in the
binary that relate to Lua, scripting, resources, or bundles. Each is
an anchor for function discovery.

### Direct Lua functions

```
stingray::LuaEnvironment::clear_temp_variables
stingray::LuaEnvironment::Internal::default_error_callback
stingray::LuaEnvironment::Internal::lua_panic
stingray::LuaMemory::ids_by_filter
stingray::LuaMemory::read_from_file
stingray::lua_resource::bytecode
stingray::script_interface::copy_lua_variable_to_c
stingray::script_interface::push_c_variable_to_lua
stingray::`anonymous-namespace'::deadlock_crash_lua
```

### Resource loading pipeline (how scripts reach the VM)

```
stingray::Bundle::open                              ← opens a bundle file
stingray::ResourceLoader::add_request               ← queues a load
stingray::ResourceLoader::open_stream               ← opens a data stream
stingray::ResourceLoader::cancel_request
stingray::ResourceLoader::get_result
stingray::ResourceLoader::shutdown
stingray::ResourceManager::load                     ← loads a resource
stingray::ResourceManager::get_resource             ← gets a loaded resource
stingray::ResourceManager::register_type            ← registers resource type
stingray::ResourceManager::resource_lookup          ← looks up by hash
stingray::CompressedResourceReader::submit          ← submits for decompression
stingray::CompressedResourceReader::wait_for_submit ← waits for decompression
stingray::CompressedResourceReader::set_task_complete
```

### Script session functions

```
stingray::script_game_session::game_object_data
stingray::script_game_session::push_parameter
stingray::script_game_session::set_parameter
stingray::script_gui::script_video
stingray::script_input_controller::edge
stingray::script_interface_application::export_mesh_geometry
```

### Total anchor count

The binary contains **724 `stingray::` namespaced function names** total.
The above lists are the subset relevant to Lua VM injection. The full
set is available via `strings Darktide.exe | grep "^stingray::"`.

---

## 5. LuaJIT Error Message Anchors (NON-FUNCTIONAL — see warning)

> **⚠ Phase 0 verified: These strings CANNOT be used for function
> discovery.** The approach documented in the original version of this
> section (LEA-xref'ing error strings to locate LuaJIT functions) does
> not work. The strings exist in the binary at the documented offsets,
> but they produce **zero LEA cross-references** anywhere in `.text`.

These standard LuaJIT 2.1 error messages from `lj_err.c` are present in
the binary at their documented offsets:

| Offset | String |
|--------|--------|
| `0x00e89b86` | `attempt to call a %s value` |
| `0x00e89c97` | `bad argument #%d to '%s' (%s)` |
| `0x00e89c1c` | `loop in gettable` |
| `0x00e89b70` | `invalid key to 'next'` |

Additional messages in the same region (`0x00e89b00`–`0x00e89e00`):
~40 error strings packed back-to-back as a contiguous `lj_err_msg[]`
block.

### Why LEA-xref doesn't work (Phase 0 finding)

The error strings are **not referenced by address** in the code. They
are interned into LuaJIT's string hash table at VM initialization via
`lj_str_new()`, and thereafter referenced by `GCstr*` handle (a
pointer to the interned string object), never by raw `.rdata` address.
A `LEA reg, [error_string]` instruction never appears because the code
never needs the string's address — it uses the pre-interned handle.

### What to do instead

Do NOT attempt to anchor on individual error strings. The confirmed
LuaJIT API function cluster around `0xc7xxxx` (found via engine
function tracing — see §7 and the Phase 0 confirmed addresses in §9)
is the reliable anchor surface. If LuaJIT-internal functions need to
be found beyond what engine tracing provides, look for the
`lj_str_new()` interning loop that processes the `lj_err_msg[]` block
at VM init, or use known function-body byte signatures from a
reference LuaJIT 2.1 build.

---

## 6. Engine File I/O Imports

The engine imports these file-related functions from `KERNEL32.dll`.
These are relevant to the **Bundle Virtualization** approach but are
listed here for completeness. For Lua VM Injection, these are NOT
hooked (we bypass the file system entirely for mod loading).

```
CreateFileW          CreateFileA           ← file opening
CreateFileMappingW   CreateFileMappingA    ← memory-mapped file creation
MapViewOfFile        UnmapViewOfFile       ← mapping views
ReadFile             WriteFile             ← regular I/O
GetFileSize          GetFileSizeEx         ← size queries
SetFilePointer       SetFilePointerEx      ← seeking
FindFirstFileW       FindFirstFileExW      ← directory enumeration
FindNextFileW
GetFileAttributesW   GetFileAttributesA    ← existence/attribute checks
GetFileAttributesExW GetFileAttributesExA
```

**Note:** `CreateFileMapping*` takes a `HANDLE` from `CreateFile*`.
Redirecting at the `CreateFile*` level covers both regular reads and
memory-mapped reads. (Relevant to Bundle Virtualization, not Lua VM
Injection.)

---

## 7. Discovery Methodology

### Step-by-step function discovery process

This is the procedure the DLL would follow at runtime to locate the
functions it needs.

#### Phase 1: Parse the PE binary

```
1. Get Darktide.exe base address (GetModuleHandleW or the DLL's own
   base if loaded into the same process)
2. Parse PE headers to find section table
3. Locate .text, .rdata, .pdata sections (their RVAs and sizes)
4. Parse .pdata into a sorted array of RUNTIME_FUNCTION entries
   (sorted by BeginAddress for binary search)
```

#### Phase 2: Find Category A engine functions (deterministic)

```
5. For each anchor string (e.g., "stingray::LuaEnvironment::Internal::lua_panic"):
   a. Search .rdata for the string → get its RVA
   b. Search .text for LEA instructions referencing that RVA
   c. For each match, binary-search .pdata to find the containing
      function (BeginAddress <= match RVA < EndAddress)
      - If the match falls in a .pdata gap, disassemble bytes
        directly and trim at the first ret (leaf function)
   d. That function's address is what the string identifies
```

**⚠ Important: The lua_panic string leads to lua_panic ITSELF, not
init code.** (Phase 0 finding.) The `lua_panic` function logs its own
name, so the string xref resolves to `lua_panic`'s body (`0x328220`),
not the init code that registers it. The reliable path to
`LuaEnvironment` init is a **two-step process**:

```
1. Find lua_panic body via its string anchor (above)
2. Search .text for LEA instructions referencing lua_panic's ADDRESS
   (not the string — the function pointer itself). These are the
   &lua_panic values passed to lua_atpanic() during init.
3. The largest .pdata function containing such a LEA is
   LuaEnvironment init.
```

Phase 0 confirmed: init is at `0x32a660`–`0x32aa2f` (975 bytes),
with the `&lua_panic` LEA at `0x32a86b`.

#### Phase 3: Find Category B LuaJIT functions (disassembly + tracing)

```
6. For the LuaEnvironment init function (found in Phase 2):
   a. Get the function's bytes from .text
   b. Disassemble using Capstone (CAPSTONE_ARCH_X86, CAPSTONE_MODE_64)
   c. Walk instructions, looking for CALL opcodes:
      - E8 xx xx xx xx     (call rel32 — direct call)
      - FF /2              (call r/m64 — indirect call)
      - FF 25 xx xx xx xx  (jmp [rip+disp32] — import thunk)
   d. For each call target, resolve the real body:
      - If target has a .pdata entry: that's the function boundary
      - If target starts with E9 rel32 (CFG thunk): follow the jump
        to the real body
      - If target is FF 25 (import thunk): resolve via IAT to
        DLL!function name
      - If target has no .pdata and no thunk: it's a leaf function;
        disassemble and trim at first ret
   e. Classify each resolved function by body shape and call context
```

**Phase 0 confirmed identifications** (see §9 for addresses and
evidence):

| Function | How identified | Confidence |
|----------|---------------|------------|
| `lua_newstate` | CFG thunk follow + allocator/ud arg signature | High |
| `lua_atpanic` | `[g+0x118]` panic-fn-slot write pattern | High |
| `lua_gettop` | `(top-base)>>3` return shape | High |
| `luaL_loadbuffer` | Traced from `lua_resource::bytecode`, 4-arg shape | High |
| `lua_pcall` | **Not found offline** — see below | — |

**lua_pcall requires runtime discovery.** It has no string anchor and
the engine's bytecode path does not exhibit a clean
`(L, int, int, int)` call site resolving to a thin `lj_docall` wrapper.
At runtime, locate it by **clustering**: `lua_newstate`, `lua_atpanic`,
`lua_gettop`, and `luaL_loadbuffer` are all in a tight address cluster
(`0xc73xxx`–`0xc7exxx`). `lua_pcall` is a thin wrapper around
`lj_docall` — look for a small function taking
`(L, nargs, nresults, errfunc)` that calls an internal docall routine.
Confirm dynamically in Story 3.

#### Phase 4: Install hooks

```
7. Hook lua_newstate using MinHook or equivalent:
   - Hook the THUNK entry (0xc7c000), not the real body (0xc7eea0),
     since the thunk is what callers actually invoke. Or hook the
     real body — pick one and document it.
   - When the engine calls lua_newstate, our hook calls the original
   - We store the returned lua_State* pointer
   - We return the pointer to the engine (transparent)
8. Hook the timing detection function (lua_resource::bytecode or
   equivalent):
   - On first call (or on Nth call), trigger the bootstrap injection
```

### Handling indirect calls and special call targets

If Phase 3 encounters indirect calls (`FF /2` — `call rax`, `call [rax]`,
etc.), the target address is not in the instruction. Instead:

1. Trace backward to find where the register was loaded
2. Common patterns: loaded from a global variable, a vtable entry,
   or a function pointer table
3. For vtable entries: find the vtable in `.rdata` or `.data`, read
   the pointer at the appropriate offset
4. For function pointer tables: search `.data` for the table,
   read the entry

**Three call-target categories that fall outside .pdata** (Phase 0
verified — see §1 for details):

1. **CFG thunks** (`E9 rel32 + cc`): follow the jump to the real body
2. **Leaf functions** (no prologue, ret-only): disassemble and trim at
   first `ret`
3. **Import thunks** (`FF 25 disp32`): resolve via IAT to `DLL!function`

If all tracing fails, fall back to pattern scanning against a reference
LuaJIT 2.1 build — but note that error-string LEA-xref does NOT work
(see §5).

---

## 8. PE → RVA Conversion

Anchor string offsets in this document are **file offsets** (offset
within the file on disk). To use them at runtime, convert to RVAs
(Relative Virtual Addresses) relative to the module base.

### Conversion formula

```
For a given section:
  RVA = file_offset - section.FileOffset + section.VirtualAddress

For the .rdata section:
  section.FileOffset    = 0x00e65600
  section.VirtualAddress = 0xe68000 (relative to module base)

  Simplified rule: add 0x2a00 to any .rdata file offset to get its RVA.
  (0x2a00 = section.VirtualAddress - section.FileOffset
         = 0xe68000 - 0x00e65600)

  Example: "lua_panic" string at file offset 0x00f1f698
  RVA = 0x00f1f698 + 0x2a00
      = 0x00f22098 (relative to module base)
```

At runtime, the actual memory address is:
```
actual_address = module_base + RVA
```

Where `module_base` is obtained via `GetModuleHandleW(L"Darktide.exe")`
(or `NULL` for the main executable).

### .rdata → .text cross-referencing

When searching `.text` for references to a `.rdata` string, the
reference is typically a RIP-relative LEA:

```
48 8D 05 xx xx xx xx    lea rax, [rip + disp32]
```

Where `rip + disp32` equals the string's RVA (relative to module base).
At runtime:

```
target_rva = current_instruction_rva + instruction_length + disp32
```

To find references to a specific string RVA, scan `.text` for the
pattern `48 8D ?5` and check whether `rip + 7 + disp32 == target_rva`.

---

## 9. Phase 0 Confirmed Addresses (Cross-Check Table)

> The following addresses were discovered and verified by Phase 0
> offline analysis. Phase 2's runtime discovery should reproduce these
> values — a match confirms both implementations are correct.
>
> **Binary SHA-256:**
> `132eed5fe58515774a41199269dd240ef6092f84b1efc8ad4a28e23ea6791661`
>
> **Full structured data:** `poc/phase0-offline-discovery/addresses.json`

### Confirmed function addresses (all verified)

| Function | RVA | Hook target | How confirmed |
|----------|-----|-------------|---------------|
| `lua_newstate` (thunk) | `0xc7c000` | Hook here (what callers invoke) — HOOKED in Phase 3 | CFG thunk follow |
| `lua_newstate` (body) | `0xc7eea0` | Or hook here (real body) | Allocator + ud arg signature |
| `lua_atpanic` | `0xc77f40` | — | `[g+0x118]` panic-fn-slot write |
| `lua_gettop` | `0xc74050` | — | `(top-base)>>3` return shape; called to verify captured L |
| `luaL_loadbuffer` | `0xc7ad80` | — | Traced from `lua_resource::bytecode`, 4-arg (L,buf,size,name) |
| `lua_pcall` | `0xc744c0` | — | **Phase 4 source-pattern match** against LuaJIT 2.1 `lj_api.c:1120`; calls `lj_vm_pcall` at `0x6845` |
| `lua_panic` (body) | `0x328220` | — | String anchor self-reference |
| `LuaEnvironment` init | `0x32a660`–`0x32aa2f` | — | `&lua_panic` LEA at `0x32a86b`; `lua_environment` marker present |
| `lj_vm_pcall` | `0x6845` | — | Callee of `lua_pcall`; matches dynasm `->vm_pcall:` entry |
| `luaL_openlibs` | `0xc7f380` | — | Phase 5 source-pattern match. **RULED OUT for use** — calling on the engine's state is destructive (overwrites engine wrappers → crash) |
| `lua_pushcclosure` | `0xc74580` | — | Phase 5 source-pattern match vs `lj_api.c:678`. Used for C-function bootstrap |
| `lua_setfield` | `0xc74cb0` | — | Phase 5 source-pattern match vs `lj_api.c:970`. Used to set globals (`lua_setglobal` is a macro: `lua_setfield(L, LUA_GLOBALSINDEX, name)`) |
| `lua_pushstring` | `0xc747d0` | — | Phase 5 source-pattern match vs `lj_api.c:647` |
| `lua_tolstring` | `0xc75190` | — | Phase 5 source-pattern match. Reads string args from C functions |
| `lua_createtable` | `0xc73ad0` | — | Phase 5 source-pattern match. Creates the `Mods` table + subtables |
| `lua_type` | `0xc753b0` | — | Phase 5 source-pattern match. Type-checks arguments |
| `lua_tonumber` | `0xc730c0` | — | Phase 5 source-pattern match. Reads numeric arguments |
| `lua_settop` | `0xc74f30` | — | Phase 5 bonus: formerly misidentified as `lua_pcall` (Phase 3 error). Actually `lua_settop` — confirmed by openlibs cleanup code |
| Engine script init | `0x32a2a0` | — | Called from LuaEnvironment::init (`0x32a8d0`). Calls `luaL_openlibs`, then replaces `_G.print`/`require`/`dofile`/`loadfile`/`load` with engine wrappers. Does NOT touch `io`/`table`/`string`/`math` |

All functions confirmed. `lua_pcall`/`luaL_openlibs`/C-API functions found via source-pattern matching against LuaJIT 2.1 source — the reliable identification method (dynamic "first-to-fire" heuristic was unreliable, as Phase 3 demonstrated).

**Note on macros:** `lua_pushcfunction` and `lua_setglobal` are macros in `lua.h`, not functions. The real calls are `lua_pushcclosure(L, f, 0)` and `lua_setfield(L, LUA_GLOBALSINDEX, name)`. `LUA_GLOBALSINDEX` = `-10002` (`0xFFFFD8EE`), confirmed from engine init code.

### lua_pcall resolution evidence (Phase 4 source-pattern match)

`lua_pcall` was the only function that could not be found offline (no
string anchor, no clean call site in the engine's bytecode path). Phase 3
attempted dynamic resolution by hooking candidates and observing which
fired — but this picked the WRONG function (`0xc74f30`, a 2-arg
stack-check helper).

Phase 4 corrected this via source-pattern matching against LuaJIT 2.1's
`lj_api.c:1120`. The function at `0xc744c0` matches the source
instruction-by-instruction: `G(L)` read, `hook_save`, errfunc handling
(test r9d; positive/negative paths), `api_call_base` computation, single
`call` to `lj_vm_pcall` at `0x6845`, `hook_restore` on error.

**Lesson:** dynamic resolution by "first candidate to fire with matching
L" is unreliable — multiple functions fire during engine init. Source-
pattern matching (comparing compiled code against known LuaJIT source)
is more reliable for identification.

### Key Phase 0 deliverables

| File | Location | Contents |
|------|----------|----------|
| `discover.py` | `poc/phase0-offline-discovery/` | Runnable offline discovery script (self-bootstrapping, idempotent) |
| `addresses.json` | same | Full structured cross-check table with all call targets and classifications |
| `report.md` | same | Human-readable findings with evidence for each identification |

---

## Quick Reference: Offset Summary

| What | File Offset | Section |
|------|-------------|---------|
| LuaJIT version string | `0x00e8b108` | `.rdata` |
| `lua_panic` anchor | `0x00f1f698` | `.rdata` |
| `default_error_callback` anchor | `0x00f1f910` | `.rdata` |
| `clear_temp_variables` anchor | `0x00f1fa48` | `.rdata` |
| `dump_state` anchor | `0x00f1fc98` | `.rdata` |
| `lua_resource::bytecode` anchor | `0x00f1d9d0` | `.rdata` |
| `copy_lua_variable_to_c` anchor | `0x00f266c8` | `.rdata` |
| `push_c_variable_to_lua` anchor | `0x00f267f8` | `.rdata` |
| `Bundle::open` anchor | `0x00f50bc0` | `.rdata` |
| `Lua->update` anchor | `0x00f520b8` | `.rdata` |
| `load_script_data` anchor | `0x00f51ef8` | `.rdata` |
| `bundle_database.data` string | `0x00f4e298` | `.rdata` |
| `lua_environment_api` string | `0x00f1f4f8` | `.rdata` |
| `.text` section (code) | `0x00000600` | — |
| `.rdata` section (strings) | `0x00e65600` | — |
| `.pdata` section (function map) | `0x010d3600` | — |

---

*All offsets verified against the live install as of the analysis date.
Game updates will shift these offsets; the discovery methodology (§7)
is designed to find them dynamically at runtime.*
