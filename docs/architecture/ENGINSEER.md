# Enginseer (runtime) — architecture

**Enginseer (runtime)** is everything up to the mod manager: the injected
modding runtime + its launcher. It's a **Hybrid** — a Rust discovery pure-library
+ a C live-game shell, linked into one DLL, delivered by `CreateRemoteThread`.
See `docs/architecture/README.md` for the project-wide architecture and the
Enginseer↔mod-manager contract.

## Subcomponents

### `discovery/` — Rust discovery pure-library  *(built, stable)*

A pure function: a PE image (`&[u8]`) → the 16 LuaJIT/engine function
addresses. No I/O, no global state; 100% safe Rust in core logic;
offline-testable against a binary fixture. Compiled to a C-ABI staticlib.

- **Interface (the seam):** `magos_discover` / `magos_discover_detail`
  (C-ABI). Shared contract: `MagosAddressTable` (`#[repr(C)]`, mirrored in
  `shell/include/magos_discovery.h`) + return codes.
- **State:** production-quality (the Enginseer runtime seed). The canonical 16
  engine/LuaJIT function addresses are stable.

### `shell/` — C live-game shell (the injected DLL)  *(built; live-validated)*

This is **`magos_shell.dll`** — the C shell linked with the Rust **discovery**
staticlib (`libmagos_discovery.a`) + MinHook, into one PE DLL.

The DLL injected into Darktide. `DllMain` spawns a worker that: runs discovery
(via the seam) → installs the `lua_newstate` hook → loads the staged mod loader
in engine context.

- **Built (minimal validation slice):** discovery call, `lua_newstate` MinHook
  + `L` capture, `lua_gettop` call, hook-ready signal.
- **Engine-context mechanism (proven).** A chunk injected at the first
  `lua_pcall` (after `luaL_openlibs`, while `io`/`loadstring` are still in
  globals) sees the engine's real facilities and can `io.open` + `loadstring`
  staged Lua — validated end-to-end in the live game. The engine removes
  `io`/`loadstring` from globals by ~pcall#10, so the trampoline runs in the
  pcall#1 → pcall#10 window and captures them first. There is no `setfenv`
  sandbox (a chunk's env *is* the globals table). **`Managers`** is an engine
  global (appears late in boot); **`CLASS`** is never engine-set, so the
  mod loader sets it.
- **Built (production trampoline + the mod loader).** The production trampoline
  is wired in `dllmain.c`: on the first `lua_pcall` (one-shot, before the
  engine's pcall) it injects the proven chunk — set the two root globals
  (`MOD_LOADER_DIR` + `MAGOS_MOD_PATH`), `io.open` the staged entry
  (`<MOD_LOADER_DIR>/init.lua`) → read → `loadstring` → run. The mod loader
  Lua is **packaged with the Enginseer runtime** (staged into `bin/mod_loader/`
  by `make build`, deployed next to the launcher/DLL; the shell self-locates it
  from its own DLL path as `<dll-dir>\mod_loader\` and publishes that dir as the
  internal `MOD_LOADER_DIR` global — not an env var/flag). The mod root is read
  from `DARKTIDE_MOD_PATH` (the launcher sets it in the child env from
  `--mod-path`); if unset the chunk emits an empty `MAGOS_MOD_PATH` (the loader
  runs, finds no mod root, and degrades gracefully — mods won't load, no crash).
  If the loader dir can't be self-located (DLL path unreadable/too long), the
  trampoline is SKIPPED (logged) and the game runs **vanilla**. The chunk
  template and game-safety discipline (one-shot, stack-clean) are unchanged.
- **Built + live-validated (the full modding chain).** The mod loader
  (`enginseer/mod_loader/init.lua`, the runtime-staged entry) runs in
  engine-context at pcall#1, captures the engine's real
  `io`/`loadstring`/`require`/`print`/`os`/`ffi` into the `Mods` table **before
  the engine removes `io`/`loadstring` (~pcall#6)**, and bridges pcall#1 to the
  engine's late boot via a **deferred bootstrap**:
  - the wrapped global `require` (`require_wrap.lua`) caches require results
    (`Mods.require_store`), one-shot installs the `class()` monkey-patch once
    `_G.class` appears (building `CLASS` — the only handle on engine state
    classes), and flushes a deferred-hook queue after every `require`;
  - the bootstrap hook (`lifecycle.lua`) attaches (deferred) to
    `CLASS.BootStateRequireGameScripts._state_update`, loads the loader
    (`mod_manager.lua` — the mod loader's driver), and installs the
    per-frame (`CLASS.StateGame.update`) + state-change
    (`CLASS.GameStateMachine._change_state`) hooks that drive `Managers.mod`.
  The loader splits load into two phases: `init()` SCANs (reads
    `mod_load_order.txt`, prepends `dmf`, builds the `_mods` table, installs the
    DMF IO watch — no mod loaded), and the first `StateGame.update` tick LOADs
    (per-mod `run()` → object → `init()`, then `_state="done"`) — deferred so
    boot-complete globals like `Managers.input` exist. The IO watch re-roots
    DMF's mod-facing IO at the mod root mid-DMF-init. The loader exposes itself
    as `Managers.mod`. The whole bootstrap is pcall-wrapped so a DMF/mod failure
    degrades to vanilla + a log line, not a crash. **Live-validated to
    `StateMainMenu`** (DMF loads, a test mod's hook fires); the scan/load split
    + IO-watch re-root are offline-tested, live validation pending. See
    `docs/architecture/MOD_LOADER-DMF.md` for the DMF integration + the IO
    re-rooting + the load timing.
- **Bootstrap-only C helpers.** C functions are acceptable only at the
  bootstrap boundary (crossing from DLL injection into the Lua lifecycle) or
  for runtime-private plumbing (status/log) — never as loader/DMF/mod-visible
  replacements for engine Lua facilities (`require`, `io`, …). See the
  compatibility section below.

### `launcher/` — C injector  *(built)*

This is **`magos_launcher.exe`** — the C injector (`enginseer/launcher/`), the
host process Darktide Magos invokes. `CreateProcess(Darktide.exe, SUSPENDED)`
→ inject `magos_shell.dll` → wait for `magos_hook_ready` → `ResumeThread` →
exit. Sets `SteamAppId`/`SteamGameId`.

- **Built:** injection + hook-ready handshake + Steam appID.
- **Interface (to Darktide Magos):** a **flag-based CLI**, where every setting
  follows **flag > env var > default**. `--game-binary` is the only required
  flag; the shell DLL is hardcoded next to the launcher (the shell self-locates
  the mod loader from its own path), and the log file defaults next to the
  launcher exe.

  | Flag | Env var | Default |
  | --- | --- | --- |
  | `--game-binary <path>` | `MAGOS_ENGINSEER_GAME_BINARY` | — **(required)** |
  | `--mod-path <path>` | `DARKTIDE_MOD_PATH` | unset (mods won't load) |
  | `--log-file <path>` | `MAGOS_ENGINSEER_LOG_FILE` | `<launcher-dir>\magos_enginseer.log` |
  | `--log-level <level>` | `MAGOS_ENGINSEER_LOG_LEVEL` | `info` (`error`/`warn`/`info`/`debug`/`trace`) |
  | `--steam-app-id <id>` | `MAGOS_ENGINSEER_STEAM_APP_ID` | `1361210` |

  The injected DLL (`magos_shell.dll`) is hardcoded to `<launcher-dir>\` and
  self-locates the mod loader (`<dll-dir>\mod_loader\`); neither path is
  configurable. The launcher resolves the config, then publishes the
  shell-contract values (`SteamAppId`/`SteamGameId`, `DARKTIDE_MOD_PATH`,
  `MAGOS_ENGINSEER_LOG_FILE`, `MAGOS_ENGINSEER_LOG_LEVEL`) into the child env
  before `CreateProcess`, so the injected shell inherits them. `-h`/`--help`
  prints the full table.

## Contracts

### Enginseer ↔ Darktide Magos (the component boundary)

- **Invocation:** Darktide Magos calls the launcher (subprocess) with the
  flag-based CLI above (`--game-binary` required; the rest flag > env > default).
- **Staging dirs (two roots):** the mod loader Lua (`init.lua` + its modules)
  ships WITH the Enginseer runtime — `make build` stages it into `bin/mod_loader/`,
  deployed next to the launcher/DLL. The shell self-locates the loader root from
  its own DLL path (`<dll-dir>\mod_loader\`, set as the internal `MOD_LOADER_DIR`
  global — not an env var/flag). The **mod** root (`--mod-path` /
  `DARKTIDE_MOD_PATH`) is Darktide-Magos-controlled: it writes DMF, user mods,
  and `mod_load_order.txt` there; the trampoline sets `MAGOS_MOD_PATH` from it
  and the mod loader bootstraps DMF + mods from there. `mod_load_order.txt` is a
  Darktide Magos artifact, but the **mod loader reads it**; DMF does not. The
  Enginseer runtime is the conduit; it does not compute the load order or
  resolve dependencies (that's Darktide Magos's job).
- **Platform:** Windows — Darktide Magos runs directly, Steam in the
  background. Linux — Steam → Darktide Magos (Proton, Darktide's compatdata)
  → launcher → Darktide, one prefix/context.

### Internal

- **discovery ↔ shell:** the C-ABI seam (`magos_discover` /
  `magos_discover_detail`); the panic boundary (`catch_unwind` at every
  `extern "C"` entry, `panic = "abort"` fail-safe).
- **launcher ↔ shell:** the `magos_hook_ready` named-event handshake (hook
  armed before `main`).

### Env-var contract (shell ↔ launcher ↔ mod-manager)

The source of truth for the values the launcher publishes into the child env
and the injected shell reads. (The launcher's own config-override env vars are
in the CLI table above; these are the *contract* the shell depends on.) The
loader root is **not** here — the shell self-locates it from its own DLL path
(`<dll-dir>\mod_loader\`) and publishes it to Lua as the internal `MOD_LOADER_DIR`
global, so no loader-path env var exists.

| Env var | Set by | Read by | Meaning |
| --- | --- | --- | --- |
| `DARKTIDE_MOD_PATH` | launcher (only when `--mod-path`/env configured) | shell trampoline + mod loader | mod dir — where DMF + user mods + `mod_load_order.txt` live. The trampoline sets `MAGOS_MOD_PATH` from it; the loader/DMF/mods root here (`Mods.file.*`). Unset ⇒ empty `MAGOS_MOD_PATH` (mods won't load; graceful). |
| `MAGOS_ENGINSEER_LOG_FILE` | launcher | shell | shell log file path |
| `MAGOS_ENGINSEER_LOG_LEVEL` | launcher | shell | shell log level (`error`/`warn`/`info`/`debug`/`trace`) |
| `SteamAppId` / `SteamGameId` | launcher | Steam | the real Darktide app id (`1361210`); without it `SteamAPI_Init` is denied under a non-Steam shortcut |

### Logging

The shell's C-side log is **`magos_enginseer.log`** (supersedes the old
`magos_spike.log`). Each line is structured
`<UTC ts> <LEVEL> <component>: <msg>` (e.g.
`2026-06-25T13:01:07Z INFO  trampoline: @ pcall#1: OK`) and goes to both
`OutputDebugString` and the log file. Level-filtered via
`MAGOS_ENGINSEER_LOG_LEVEL` (default `info`; crank to `debug`/`trace` for
verbose detail). Default location is next to the launcher exe (resolved by the
launcher from `--log-file`/`MAGOS_ENGINSEER_LOG_FILE`); the shell itself opens
`MAGOS_ENGINSEER_LOG_FILE` if set, otherwise falls back to beside the game exe.

**Log split to be aware of:** the mod loader's Lua-side `print`/`__print` output
(the `[mod_loader] …` lines) goes to the **engine's** print destination —
Fatshark's console log (under Proton, the Proton/steam log) — **not** to
`magos_enginseer.log`. `magos_enginseer.log` carries the C-side shell +
trampoline lines (including the trampoline's one-line `OK`/`FAIL` status, which
is the reliable bootstrap validation).

## Runtime patch compatibility

The entire point of Magos is to eliminate the fragility of the bundle-db /
`patch_999` toolchain — not to relocate that fragility upstream into a
reimplementation of Darktide's Lua runtime.

Magos replaces the current toolchain's bundle-database entry point:

```text
current: dtkit-patch → patch_999 → mod_loader → DMF → mods
Magos:   DLL injection → runtime patch → staged mod_loader/DMF entry point → mods
```

The runtime patch is an entry-point replacement, not a replacement Lua runtime.
It may use native code and narrow C helpers to cross from DLL injection into the
game's Lua lifecycle, but once staged loader/DMF/mod Lua is running it must see the
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
- handing off to staged loader/DMF Lua in an engine-equivalent environment.

After that handoff, loader/DMF-visible surfaces such as `require`, `io`,
`loadstring`, globals, and loader hooks come from the engine path or a
behaviorally equivalent wrapper around it — not from independent C
reimplementations.

### Engine-equivalent loader path

In the current community toolchain, `patch_999` is file-based only for the
initial bootstrap: its trampoline reads `./mod_loader` from disk and executes it
with `loadstring`. After that, `mod_loader` runs inside the game's normal Lua
startup path and captures the engine-visible Lua facilities that loader/DMF expect.

**This mechanism is proven.** A chunk injected at `lua_pcall` #1 (the first
script execution after `luaL_openlibs`, while `io`/`loadstring` are still in
the globals) sees the engine's real facilities and can `io.open` + `loadstring`
staged Lua — validated end-to-end in the live game (trampoline prototype loaded
+ ran staged Lua successfully). The engine removes `io`/`loadstring` by
~pcall#10, so the trampoline runs in the pcall#1 → pcall#10 window and captures
them first. There is no `setfenv` sandbox (chunk env = globals table). The mod
loader then runs in engine-context, captures `io`/`loadstring`/`require` into the
`Mods` table, and defers `CLASS`/`Managers` work (same model as the existing
community `mod_loader`).

### `Mods.original_require`

The runtime must provide DMF with a `Mods.original_require` function compatible
with the loader DMF expects. In the current toolchain, `mod_loader` preserves the
engine's real `require` as `Mods.original_require`; DMF's require module builds
tracking (`Mods.require_store`, `hook_require`) on top of that original function.

Magos preserves this behavior. `Mods.original_require` is the engine's original
`require` or a behaviorally equivalent wrapper around it. It is not a
staging-only file loader. Runtime-owned file loading may exist for bootstrap or
private helper paths, but not as the production implementation of
`Mods.original_require` observed by loader/DMF/mod code.

### `Mods.lua.io`

The runtime must provide the `Mods.lua.io` surface that DMF uses for file access.
In the current toolchain, the existing DML (Darktide-Mod-Loader) assigns `Mods.lua.io` from the Lua `io` library
available in the engine-loaded environment. DMF's `core/io.lua` copies that table
and uses the familiar Lua file API (`io.open`, file `:read("*all")`, `:lines()`,
`:close()`, `io.close`).

Magos preserves this behavior. `Mods.lua.io` is the engine-visible Lua `io` table
or a behaviorally equivalent engine wrapper. A C/Win32 file API may exist for the
bootstrap or runtime-private helpers, but not as the loader/DMF-visible production
replacement for Lua `io`.

## Out of scope for the Enginseer runtime

- **Dependency resolution / load-order computation** — Darktide Magos's job
  (it writes `mod_load_order.txt`); the Enginseer runtime bootstraps the staged
  mod loader entry point, and the mod loader reads the load order (DMF does not).
- **The mod manager UI / staging-dir management** — Darktide Magos.

## Build + test

See `AGENTS.md` (agent ops) and `docs/architecture/README.md` (test strategy):
MinGW + MSVC; the `test-hooks` feature; `make build/check/test` + the clippy
gate.

## References

- `docs/architecture/MOD_LOADER-DMF.md` — the mod_loader↔DMF integration + IO
  re-rooting (the loader, the loader surfaces, the `Managers.mod` shape contract,
  the load timing).
- `docs/architecture/README.md` — project architecture + the Enginseer↔mod-manager contract.
- `docs/reference/darktide-binary.md` — the validated game-binary constraints.
- `docs/poc/` — frozen POC handoff (the discovery methodology + DMF bootstrap approach are validated here).
