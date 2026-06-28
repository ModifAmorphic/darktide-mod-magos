# Enginseer (runtime)

**Enginseer (runtime)** is the injected modding runtime + its launcher for
Darktide. It launches the game modded via DLL injection — no files in the game
directory, no bundle-database patching — and stays out of the way for vanilla
play (launch the game from Steam and it runs unmodified). Enginseer comprises
the **mod loader** (the runtime-staged Lua that loads DMF + user mods) plus the
launcher that delivers it.

- **Architecture:** [`docs/architecture/ENGINSEER.md`](../docs/architecture/ENGINSEER.md)
  — the subcomponents, the Rust↔C seam, the launcher flow, the env-var
  contract, logging.
- **DMF integration:** [`docs/architecture/MOD_LOADER-DMF.md`](../docs/architecture/MOD_LOADER-DMF.md)
  — how the mod loader loads DMF (the Darktide Mod Framework) + user mods, the
  IO re-rooting, and the load timing.
- **Project overview:** [`../README.md`](../README.md) (end-user), and
  [`../AGENTS.md`](../AGENTS.md) (agent orientation + ops).

> **Audience:** developers / power users. This is the build + component detail;
> the root [`README.md`](../README.md) is the end-user entry point.

## Sub-components

| Dir | What it is |
| --- | --- |
| **`discovery/`** | Rust crate — the LuaJIT discovery engine. A pure library (no I/O, no global state): a PE image (`&[u8]`) → the 16 LuaJIT/engine function addresses. 100% safe Rust in core logic; offline-testable. Compiled to a C-ABI staticlib (`libmagos_discovery.a`). |
| **`shell/`** | The injected C DLL — **`magos_shell.dll`**. Hooks the Lua VM (`lua_newstate` → the production trampoline), discovers the LuaJIT function addresses in-process, and loads the staged mod loader in engine context. Linked with the Rust discovery staticlib + MinHook. |
| **`launcher/`** | The C injector — **`magos_launcher.exe`**. `CreateProcess(Darktide.exe, SUSPENDED)` → inject `magos_shell.dll` → wait for the hook-ready signal → `ResumeThread`. Resolves the flag/env config and publishes it into the child env. |
| **`mod_loader/`** | The Lua mod loader. Runs in engine context, bridges pcall#1 to the engine's late boot (deferred bootstrap), and loads DMF + user mods. Entry `init.lua` + modules (`file`, `hook`, `class_patch`, `require_wrap`, `lifecycle`, `mod_manager`). Enginseer-controlled (ships with the build). |
| **`tests/`** | C unit tests (run via wine). |
| **`bin/`** | Build outputs (gitignored). Where `make build` lands everything. |

The workspace root (`Cargo.toml` / `Cargo.lock` / `Makefile`) lives here in
`enginseer/`, not the repo root — **all build/test commands run from `enginseer/`**.

## Build artifacts (`make build` → `bin/`)

- **`bin/magos_launcher.exe`** — the C injector (from `launcher/`). The host
  process that creates the game suspended, injects the shell DLL, waits for the
  hook-ready handshake, and resumes. Sets the Steam app id and publishes the
  runtime's env vars.
- **`bin/magos_shell.dll`** — the injected DLL (from `shell/`): the C shell
  linked with the Rust discovery staticlib (`libmagos_discovery.a`) + MinHook,
  into one PE DLL. Hooks the Lua VM, runs discovery in-process, and stages the
  mod loader.
- **`bin/mod_loader/`** — the mod loader Lua (from `mod_loader/`), staged next to
  the launcher/DLL. This is the **Enginseer-controlled** loader root the shell
  self-locates from its own DLL path (`<dll-dir>/mod_loader/`, set as the internal
  `MOD_LOADER_DIR` global — not an env var/flag); the trampoline loads `init.lua`
  from here. User mods live in a separate, user-controlled mod root (see
  [Two roots](#two-roots)).

## Build + test

Run from `enginseer/`:

```sh
export PATH="$HOME/.cargo/bin:$PATH"   # system rust lacks the windows-gnu target
source ../_local/DARKTIDE.env          # sets DARKTIDE_GAME_DIR (for oracle tests)

make build          # cross-compile DLL + launcher (x86_64-pc-windows-gnu)
make check          # verify valid PE DLL with DllMain
make test           # C tests (via wine) + Rust tests + mod loader Lua tests
make mod-loader-test # mod loader Lua tests (offline LuaJIT harness; no game/wine)
```

- **MinGW cross-compile** from Linux produces the Windows DLL + launcher. MSVC
  native on Windows is also supported (CI runs both).
- **Oracle tests** run discovery against the real `Darktide.exe` (resolved via
  `DARKTIDE_GAME_DIR`). The engine is build-agnostic (Tier-2 self-validation
  passes on any build; Tier-1 exact-match skips if the SHA differs from the
  pinned one). In CI (no game install) they skip cleanly.
- **Mod loader Lua tests** are an offline LuaJIT harness — no game, no wine.
- **`test-hooks` feature** gates the debug panic-boundary symbol out of release
  builds: `cargo test --features test-hooks -p magos-discovery` (and
  `cargo clippy --all-targets --features test-hooks -- -D warnings`). `make test`
  handles this.

## Launcher CLI

The launcher is **flag-based**, where every setting follows
**flag > env var > default**. `--game-binary` is the only required flag; the
shell DLL, log file, and mod loader root all default next to the launcher exe.

| Flag | Env var | Default |
| --- | --- | --- |
| `--game-binary <path>` | `MAGOS_ENGINSEER_GAME_BINARY` | — **(required)** |
| `--mod-path <path>` | `DARKTIDE_MOD_PATH` | unset (mods won't load) |
| `--log-file <path>` | `MAGOS_ENGINSEER_LOG_FILE` | `<launcher-dir>\magos_enginseer.log` |
| `--log-level <level>` | `MAGOS_ENGINSEER_LOG_LEVEL` | `info` (`error`/`warn`/`info`/`debug`/`trace`) |
| `--steam-app-id <id>` | `MAGOS_ENGINSEER_STEAM_APP_ID` | `1361210` |

The injected DLL (`magos_shell.dll`) is hardcoded to `<launcher-dir>\` and
self-locates the mod loader (`<dll-dir>\mod_loader\`); neither path is
configurable.

See [`docs/architecture/ENGINSEER.md`](../docs/architecture/ENGINSEER.md) →
`launcher/` for the full table, the env-var contract, and logging details.

## Two roots

The mod loader and the mods live in **separate** directories, resolved as two
values:

- **`MOD_LOADER_DIR`** (self-located by the shell from its own DLL path as
  `<dll-dir>\mod_loader\`, set as an **internal** global — not an env var/flag) —
  **Enginseer-controlled**. Holds `init.lua` + its modules. Ships with the build
  (`make build` stages it); a DMF/mod update never requires an Enginseer rebuild.
- **`MAGOS_MOD_PATH`** (from `--mod-path` / `DARKTIDE_MOD_PATH`) —
  **user/mod-manager-controlled**. Holds DMF + user mods +
  `mods.lst` (the load-order file, regenerated by Magos Modificus each launch).
  The mod loader roots its mod-facing IO here.

The split keeps the loader's own code Enginseer-owned while the mods it loads are
user-owned. Detail in
[`docs/architecture/MOD_LOADER-DMF.md`](../docs/architecture/MOD_LOADER-DMF.md).
