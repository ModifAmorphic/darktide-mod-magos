# Runtime — architecture

The **runtime** is everything up to the mod manager: the injected modding
runtime + its launcher. It's a **Hybrid** — a Rust discovery pure-library +
a C live-game shell, linked into one DLL, delivered by `CreateRemoteThread`.
See `docs/architecture/README.md` for the project-wide architecture and the
runtime↔mod-manager contract.

## Subcomponents

### `discovery/` — Rust discovery pure-library  *(built, stable)*

A pure function: a PE image (`&[u8]`) → the 16 LuaJIT/engine function
addresses. No I/O, no global state; 100% safe Rust in core logic;
offline-testable against a binary fixture. Compiled to a C-ABI staticlib.

- **Interface (the seam):** `magos_discover` / `magos_discover_detail`
  (C-ABI). Shared contract: `MagosAddressTable` (`#[repr(C)]`, mirrored in
  `shell/include/magos_discovery.h`) + return codes.
- **State:** production-quality (the Component A seed). The canonical 16
  addresses are stable; two bonus Phase-1 probe fields are also exposed —
  `lua_getfield` (the C-API table-get the shell uses to read globals;
  `lua_getglobal` is a macro over it) and `lua_resource_bytecode` (the engine
  bundle-script loader, resolved via the `stingray::lua_resource::bytecode`
  string anchor).

### `shell/` — C live-game shell (the injected DLL)  *(minimal slice built; expansion planned)*

The DLL injected into Darktide. `DllMain` spawns a worker that: runs discovery
(via the seam) → installs the `lua_newstate` hook → **[to build]** loads the
staged DML/DMF entry point in engine context → reports status.

- **Built (minimal validation slice):** discovery call, `lua_newstate` MinHook
  + `L` capture, `lua_gettop` call, hook-ready signal.
- **Built (engine-context validation — PROVEN).** Probes (Phase 1-3) + a
  trampoline prototype (Phase 4) established the engine-context mechanism:
  - The engine's globals table has the full stdlib (`io`, `loadstring`,
    `require`, `print`, …) from `luaL_openlibs` through `lua_pcall` #1.
  - The engine **removes `io` + `loadstring` from the globals between pcall#1
    and pcall#10** (after the initial script-compilation phase). Gone
    thereafter.
  - **No `setfenv` sandbox** — a chunk's env *is* the globals table (confirmed
    at every measured point). The only "sandboxing" is that removal.
  - **`Managers`** is an engine global (appears ~pcall#16). **`CLASS`** is
    never engine-set (mod-loader-provided — our DML sets it, as today).
  - **Trampoline (PROVEN live):** a chunk injected at pcall#1 used the
    engine's real `io.open` + `loadstring` to read, compile, and run staged
    Lua → `OK`. **Engine-context is achievable via DLL injection.**
  The probe hooks (detours on `lua_newstate`/`luaL_openlibs`/
  `luaL_loadbuffer`/`lua_pcall`/`lua_setfenv`, read-only globals inspection)
  remain as recon tooling; they are not the production path.
- **To build (the production shell):**
  - **Production trampoline (inject at pcall#1).** On the first `lua_pcall`,
    inject a trampoline chunk that replicates `patch_999`'s mechanism:
    `io.open` the staged DML (our `mod_loader` equivalent) → `loadstring` →
    run. The DML runs in engine-context (sees the real `io`/`loadstring`/
    `require`), captures them into the `Mods` table **before the engine
    removes them (~pcall#10)**, then defers `CLASS`/`Managers`-dependent work
    (hooks `Main.init`/`StateRequireScripts`, runs after the game state
    machine is up — same model as the existing `mod_loader`).
  - **Status reporting** — report discovery results, DML/DMF/mod load, errors
    to the launcher (via the file-backed status channel).
- **Bootstrap-only C helpers.** C functions are acceptable only at the
  bootstrap boundary (crossing from DLL injection into the Lua lifecycle) or
  for runtime-private plumbing (status/log) — never as DML/DMF/mod-visible
  replacements for engine Lua facilities (`require`, `io`, …). See the
  compatibility section below.

### `launcher/` — C injector + session host  *(injection built; session-host mode planned)*

The host process Darktide Magos invokes. `CreateProcess(Darktide.exe,
SUSPENDED)` → inject `magos_shell.dll` → wait for `magos_hook_ready` →
`ResumeThread`. Sets `SteamAppId`/`SteamGameId`.

- **Built:** injection + hook-ready handshake + Steam appID.
- **To build (session-host mode):** stay alive after resume, relay the
  shell's status to Darktide Magos via structured stdout, wait for Darktide
  to exit, then exit.
- **Interface (to Darktide Magos):** the CLI —
  `--game <Darktide.exe> --dll <magos_shell.dll> --staging <staging_dir>` —
  + structured stdout (status). Darktide Magos reads stdout; the
  shell→launcher channel is internal to the runtime.

## Contracts

### Runtime ↔ Darktide Magos (the component boundary)

- **Invocation:** Darktide Magos calls the launcher (subprocess) with
  `--game` / `--dll` / `--staging`.
- **Staging dir:** Darktide Magos writes (DMF, mods, `mod_load_order.txt`);
  the runtime bootstraps the staged DML/DMF entry point with engine-equivalent
  Lua semantics. `mod_load_order.txt` is a Darktide Magos → DMF artifact (the
  runtime is the conduit; it does not parse the load order).
- **Status:** the launcher relays the shell's status via stdout (launch
  progress, mod-load outcome, errors, game exit). Game-update detection
  (discovery mismatch) rides this channel.
- **Lifecycle:** the launcher manages Darktide; exits on game exit; Darktide
  Magos returns to the UI. Cancel = terminate the launcher.
- **Platform:** Windows — Darktide Magos runs directly, Steam in the
  background. Linux — Steam → Darktide Magos (Proton, Darktide's compatdata)
  → launcher → Darktide, one prefix/context.

### Internal

- **discovery ↔ shell:** the C-ABI seam (`magos_discover` /
  `magos_discover_detail`); the panic boundary (`catch_unwind` at every
  `extern "C"` entry, `panic = "abort"` fail-safe).
- **launcher ↔ shell:** the `magos_hook_ready` named-event handshake (hook
  armed before `main`); the file-backed status channel (shell→launcher).

## Runtime patch compatibility

The entire point of Magos is to eliminate the fragility of the bundle-db /
`patch_999` toolchain — not to relocate that fragility upstream into a
reimplementation of Darktide's Lua runtime.

Magos replaces the current toolchain's bundle-database entry point:

```text
current: dtkit-patch → patch_999 → mod_loader → DMF → mods
Magos:   DLL injection → runtime patch → staged DML/DMF entry point → mods
```

The runtime patch is an entry-point replacement, not a replacement Lua runtime.
It may use native code and narrow C helpers to cross from DLL injection into the
game's Lua lifecycle, but once staged DML/DMF/mod Lua is running it must see the
same relevant globals, loader behavior, and file/runtime semantics it receives
when loaded by the engine today.

Runtime-owned replacement behavior is acceptable only at the bootstrap boundary
or for runtime-private plumbing such as status/logging. Magos should not become
an ongoing compatibility shim that reimplements Darktide's Lua runtime or
patch-fixes missing behavior for each mod.

### Bootstrap boundary

The shell's C bootstrap is responsible for:

- finding/capturing the live Lua state at the correct lifecycle point;
- getting the staged runtime patch into that state without the bundle database;
- reporting bootstrap/discovery/load status to the launcher;
- handing off to staged DML/DMF Lua in an engine-equivalent environment.

After that handoff, DML/DMF-visible surfaces such as `require`, `io`,
`loadstring`, globals, and loader hooks come from the engine path or a
behaviorally equivalent wrapper around it — not from independent C
reimplementations.

### Engine-equivalent loader path

In the current community toolchain, `patch_999` is file-based only for the
initial bootstrap: its trampoline reads `./mod_loader` from disk and executes it
with `loadstring`. After that, `mod_loader` runs inside the game's normal Lua
startup path and captures the engine-visible Lua facilities that DML/DMF expect.

**This mechanism is proven.** A chunk injected at `lua_pcall` #1 (the first
script execution after `luaL_openlibs`, while `io`/`loadstring` are still in
the globals) sees the engine's real facilities and can `io.open` + `loadstring`
staged Lua — validated end-to-end in the live game (trampoline prototype loaded
+ ran staged Lua successfully). The engine removes `io`/`loadstring` by
~pcall#10, so the trampoline runs in the pcall#1 → pcall#10 window and captures
them first. There is no `setfenv` sandbox (chunk env = globals table). The DML
then runs in engine-context, captures `io`/`loadstring`/`require` into the
`Mods` table, and defers `CLASS`/`Managers` work (same model as the existing
`mod_loader`).

### `Mods.original_require`

The runtime must provide DMF with a `Mods.original_require` function compatible
with the loader DMF expects. In the current toolchain, `mod_loader` preserves the
engine's real `require` as `Mods.original_require`; DMF's require module builds
tracking (`Mods.require_store`, `hook_require`) on top of that original function.

Magos preserves this behavior. `Mods.original_require` is the engine's original
`require` or a behaviorally equivalent wrapper around it. It is not a
staging-only file loader. Runtime-owned file loading may exist for bootstrap or
private helper paths, but not as the production implementation of
`Mods.original_require` observed by DML/DMF/mod code.

### `Mods.lua.io`

The runtime must provide the `Mods.lua.io` surface that DMF uses for file access.
In the current toolchain, DML assigns `Mods.lua.io` from the Lua `io` library
available in the engine-loaded environment. DMF's `core/io.lua` copies that table
and uses the familiar Lua file API (`io.open`, file `:read("*all")`, `:lines()`,
`:close()`, `io.close`).

Magos preserves this behavior. `Mods.lua.io` is the engine-visible Lua `io` table
or a behaviorally equivalent engine wrapper. A C/Win32 file API may exist for the
bootstrap or runtime-private helpers, but not as the DML/DMF-visible production
replacement for Lua `io`.

### Status channel (shell→launcher)

The runtime must move status from the injected shell back to the launcher so the
launcher can relay structured progress, mod-load outcomes, and errors to
Darktide Magos over stdout. This internal shell→launcher mechanism is separate
from the already-settled launcher→Darktide Magos stdout contract.

For v1, the internal status channel is file-backed. The shell writes structured
status records to a session-owned file; the launcher tails/reads that file and
relays normalized events to Darktide Magos over stdout. The file-backed channel
keeps the injected shell simple and leaves real-time control channels for future
runtime-command work.

## Out of scope for the runtime

- **Dependency resolution / load-order computation** — Darktide Magos's job
  (it writes `mod_load_order.txt`); the runtime bootstraps the staged DML/DMF
  entry point, and DMF reads the load order.
- **Multi-shot injection** — not needed for v1. The runtime's injection is
  one-shot (bootstrap); DMF's own hook system handles ongoing mod execution.
  Multi-shot (hot-reload, runtime commands) is a future capability.
- **The mod manager UI / staging-dir management** — Darktide Magos.

## Build + test

See `AGENTS.md` (agent ops) and `docs/architecture/README.md` (test strategy):
MinGW + MSVC; the `test-hooks` feature; `make build/check/test` + the clippy
gate.

## References

- `docs/architecture/README.md` — project architecture + the runtime↔mod-manager contract.
- `docs/reference/darktide-binary.md` — the validated game-binary constraints.
- `docs/poc/` — frozen POC handoff (the discovery methodology + DMF bootstrap approach are validated here).
