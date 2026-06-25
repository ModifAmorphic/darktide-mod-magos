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
- **State:** production-quality (the the runtime seed). The canonical 16
  addresses are stable; two bonus Phase-1 probe fields are also exposed —
  `lua_getfield` (the C-API table-get the shell uses to read globals;
  `lua_getglobal` is a macro over it) and `lua_resource_bytecode` (the engine
  bundle-script loader, resolved via the `stingray::lua_resource::bytecode`
  string anchor).

### `shell/` — C live-game shell (the injected DLL)  *(built; live-validated)*

The DLL injected into Darktide. `DllMain` spawns a worker that: runs discovery
(via the seam) → installs the `lua_newstate` hook → loads the staged Enginseer
(aka the Mod Loader) in engine context → reports status.

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
    never engine-set (mod-loader-provided — our Enginseer sets it, as today).
  - **Trampoline (PROVEN live):** a chunk injected at pcall#1 used the
    engine's real `io.open` + `loadstring` to read, compile, and run staged
    Lua → `OK`. **Engine-context is achievable via DLL injection.**
  The probe hooks (detours on `lua_newstate`/`luaL_openlibs`/
  `luaL_loadbuffer`/`lua_pcall`/`lua_setfenv`, read-only globals inspection)
  remain as recon tooling; they are not the production path.
- **Built (production trampoline + Enginseer v2).** The production trampoline
  is wired in `dllmain.c`: on the first `lua_pcall` (one-shot, before the
  engine's pcall) it injects the proven Phase-4 chunk — set the `MAGOS_MOD_PATH`
  global, `io.open` the staged entry (`<DARKTIDE_MOD_PATH>/enginseer.lua`) →
  read → `loadstring` → run. The staging dir is read from `DARKTIDE_MOD_PATH`
  (the launcher sets it in the child env); if unset the trampoline is SKIPPED
  (logged) and the build degrades to the recon probes. The chunk template and
  safety discipline (one-shot, stack-clean, `g_in_probe` guard) carry over
  unchanged from the Phase-4 prototype that validated the mechanism.
- **Built + live-validated (Enginseer v2 — the full modding chain).** The
  Enginseer (`runtime/enginseer/enginseer.lua`, the user-staged entry) runs in
  engine-context at pcall#1, captures the engine's real
  `io`/`loadstring`/`require`/`print`/`os`/`ffi` into the `Mods` table **before
  the engine removes `io`/`loadstring` (~pcall#6)**, and bridges pcall#1 to the
  engine's late boot via a **deferred bootstrap**:
  - the wrapped global `require` (`require_wrap.lua`) caches require results
    (`Mods.require_store`), one-shot installs the `class()` monkey-patch once
    `_G.class` appears (building `CLASS` — the only handle on engine state
    classes), and flushes a deferred-hook queue after every `require`;
  - the bootstrap hook (`lifecycle.lua`) attaches (deferred) to
    `CLASS.BootStateRequireGameScripts._state_update`, loads the **rite**
    (`mod_manager.lua` — the Enginseer IS the mod loader), and installs the
    per-frame (`CLASS.StateGame.update`) + state-change
    (`CLASS.GameStateMachine._change_state`) hooks that drive `Managers.mod`.
  The rite reads `mod_load_order.txt`, prepends `dmf`, and loads each mod's
  `.mod` (`run()` → object → `init()`), exposing itself as `Managers.mod`. The
  whole bootstrap is pcall-wrapped so a DMF/mod failure degrades to vanilla +
  a log line, not a crash. **Live-validated end-to-end** (DMF loads, runs to
  `StateMainMenu`, a test mod's hook fires). See
  `docs/architecture/ENGINSEER-DMF.md` for the DMF integration + the IO
  re-rooting.
- **To build (the production shell):**
  - **Status reporting** — report discovery results, Enginseer/DMF/mod load,
    errors to the launcher (via the file-backed status channel).
- **Bootstrap-only C helpers.** C functions are acceptable only at the
  bootstrap boundary (crossing from DLL injection into the Lua lifecycle) or
  for runtime-private plumbing (status/log) — never as Enginseer/DMF/mod-visible
  replacements for engine Lua facilities (`require`, `io`, …). See the
  compatibility section below.

### `launcher/` — C injector + session host  *(injection built; session-host mode planned)*

The host process Darktide Magos invokes. `CreateProcess(Darktide.exe,
SUSPENDED)` → inject `magos_shell.dll` → wait for `magos_hook_ready` →
`ResumeThread`. Sets `SteamAppId`/`SteamGameId`.

- **Built:** injection + hook-ready handshake + Steam appID.
- **To build (session-host mode):** stay alive after resume, relay the
  shell's status to Darktide Magos via structured stdout, wait for Darktide
  to exit, then exit. (Today the launcher exits after `ResumeThread`.)
- **Interface (to Darktide Magos):** a **flag-based CLI**, where every setting
  follows **flag > env var > default**. `--game-binary` is the only required
  flag; the shell DLL + log file default next to the launcher exe.

  | Flag | Env var | Default |
  | --- | --- | --- |
  | `--game-binary <path>` | `MAGOS_ENGINSEER_GAME_BINARY` | — **(required)** |
  | `--magos-shell <path>` | `MAGOS_ENGINSEER_SHELL` | `<launcher-dir>\magos_shell.dll` |
  | `--mod-path <path>` | `DARKTIDE_MOD_PATH` | unset (trampoline skips) |
  | `--log-file <path>` | `MAGOS_ENGINSEER_LOG_FILE` | `<launcher-dir>\magos_enginseer.log` |
  | `--log-level <level>` | `MAGOS_ENGINSEER_LOG_LEVEL` | `info` (`error`/`warn`/`info`/`debug`/`trace`) |
  | `--steam-app-id <id>` | `MAGOS_ENGINSEER_STEAM_APP_ID` | `1361210` |

  The launcher resolves the config, then publishes the shell-contract values
  (`SteamAppId`/`SteamGameId`, `DARKTIDE_MOD_PATH`, `MAGOS_ENGINSEER_LOG_FILE`,
  `MAGOS_ENGINSEER_LOG_LEVEL`) into the child env before `CreateProcess`, so the
  injected shell inherits them. `-h`/`--help` prints the full table. Darktide
  Magos reads the launcher's stdout; the shell→launcher channel is internal to
  the runtime.

## Contracts

### Runtime ↔ Darktide Magos (the component boundary)

- **Invocation:** Darktide Magos calls the launcher (subprocess) with the
  flag-based CLI above (`--game-binary` required; the rest flag > env > default).
- **Staging dir:** Darktide Magos writes it (DMF, mods, `mod_load_order.txt`);
  the runtime bootstraps the staged Enginseer entry point with engine-equivalent
  Lua semantics. `mod_load_order.txt` is a Darktide Magos artifact, but the
  **Enginseer reads it** (the rite — it is the mod loader); DMF does not. The
  runtime is the conduit; it does not compute the load order or resolve
  dependencies (that's Darktide Magos's job).
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

### Env-var contract (shell ↔ launcher ↔ mod-manager)

The source of truth for the values the launcher publishes into the child env
and the injected shell reads. (The launcher's own config-override env vars are
in the CLI table above; these are the *contract* the shell depends on.)

| Env var | Set by | Read by | Meaning |
| --- | --- | --- | --- |
| `DARKTIDE_MOD_PATH` | launcher (only when `--mod-path`/env configured) | shell trampoline | staging dir for mods + `enginseer.lua`. Unset ⇒ trampoline SKIPPED (degrades to recon probes / vanilla) |
| `MAGOS_ENGINSEER_LOG_FILE` | launcher | shell | shell log file path |
| `MAGOS_ENGINSEER_LOG_LEVEL` | launcher | shell | shell log level (`error`/`warn`/`info`/`debug`/`trace`) |
| `SteamAppId` / `SteamGameId` | launcher | Steam | the real Darktide app id (`1361210`); without it `SteamAPI_Init` is denied under a non-Steam shortcut |

### Logging

The shell's C-side log is **`magos_enginseer.log`** (supersedes the old
`magos_spike.log`). Each line is structured
`<UTC ts> <LEVEL> <component>: <msg>` (e.g.
`2026-06-25T13:01:07Z INFO  trampoline: @ pcall#1: OK`) and goes to both
`OutputDebugString` and the log file. Level-filtered via
`MAGOS_ENGINSEER_LOG_LEVEL` (default `info`; the probe recon runs at
`debug`/`trace`). Default location is next to the launcher exe (resolved by the
launcher from `--log-file`/`MAGOS_ENGINSEER_LOG_FILE`); the shell itself opens
`MAGOS_ENGINSEER_LOG_FILE` if set, otherwise falls back to beside the game exe.

**Log split to be aware of:** the Enginseer's Lua-side `print`/`__print` output
(the `[Enginseer] …` lines) goes to the **engine's** print destination —
Fatshark's console log (under Proton, the Proton/steam log) — **not** to
`magos_enginseer.log`. `magos_enginseer.log` carries the C-side shell + probe +
trampoline lines (including the trampoline's one-line `OK`/`FAIL` status, which
is the reliable bootstrap validation).

## Runtime patch compatibility

The entire point of Magos is to eliminate the fragility of the bundle-db /
`patch_999` toolchain — not to relocate that fragility upstream into a
reimplementation of Darktide's Lua runtime.

Magos replaces the current toolchain's bundle-database entry point:

```text
current: dtkit-patch → patch_999 → mod_loader → DMF → mods
Magos:   DLL injection → runtime patch → staged Enginseer/DMF entry point → mods
```

The runtime patch is an entry-point replacement, not a replacement Lua runtime.
It may use native code and narrow C helpers to cross from DLL injection into the
game's Lua lifecycle, but once staged Enginseer/DMF/mod Lua is running it must see the
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
- handing off to staged Enginseer/DMF Lua in an engine-equivalent environment.

After that handoff, Enginseer/DMF-visible surfaces such as `require`, `io`,
`loadstring`, globals, and loader hooks come from the engine path or a
behaviorally equivalent wrapper around it — not from independent C
reimplementations.

### Engine-equivalent loader path

In the current community toolchain, `patch_999` is file-based only for the
initial bootstrap: its trampoline reads `./mod_loader` from disk and executes it
with `loadstring`. After that, `mod_loader` runs inside the game's normal Lua
startup path and captures the engine-visible Lua facilities that Enginseer/DMF expect.

**This mechanism is proven.** A chunk injected at `lua_pcall` #1 (the first
script execution after `luaL_openlibs`, while `io`/`loadstring` are still in
the globals) sees the engine's real facilities and can `io.open` + `loadstring`
staged Lua — validated end-to-end in the live game (trampoline prototype loaded
+ ran staged Lua successfully). The engine removes `io`/`loadstring` by
~pcall#10, so the trampoline runs in the pcall#1 → pcall#10 window and captures
them first. There is no `setfenv` sandbox (chunk env = globals table). The Enginseer
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
`Mods.original_require` observed by Enginseer/DMF/mod code.

### `Mods.lua.io`

The runtime must provide the `Mods.lua.io` surface that DMF uses for file access.
In the current toolchain, the existing DML (Darktide-Mod-Loader) assigns `Mods.lua.io` from the Lua `io` library
available in the engine-loaded environment. DMF's `core/io.lua` copies that table
and uses the familiar Lua file API (`io.open`, file `:read("*all")`, `:lines()`,
`:close()`, `io.close`).

Magos preserves this behavior. `Mods.lua.io` is the engine-visible Lua `io` table
or a behaviorally equivalent engine wrapper. A C/Win32 file API may exist for the
bootstrap or runtime-private helpers, but not as the Enginseer/DMF-visible production
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
  (it writes `mod_load_order.txt`); the runtime bootstraps the staged Enginseer
  entry point, and the Enginseer's rite reads the load order (DMF does not).
- **Multi-shot injection** — not needed for v1/v2. The runtime's injection is
  one-shot (bootstrap); DMF's own hook system handles ongoing mod execution.
  Multi-shot (hot-reload, runtime commands) is a future capability.
- **The mod manager UI / staging-dir management** — Darktide Magos.

## Build + test

See `AGENTS.md` (agent ops) and `docs/architecture/README.md` (test strategy):
MinGW + MSVC; the `test-hooks` feature; `make build/check/test` + the clippy
gate.

## References

- `docs/architecture/ENGINSEER-DMF.md` — the Enginseer↔DMF integration + IO
  re-rooting (the rite, the loader surfaces, the `Managers.mod` shape contract).
- `docs/architecture/README.md` — project architecture + the runtime↔mod-manager contract.
- `docs/reference/darktide-binary.md` — the validated game-binary constraints.
- `docs/poc/` — frozen POC handoff (the discovery methodology + DMF bootstrap approach are validated here).
