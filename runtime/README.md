# Runtime (Enginseer)

The **runtime** is the injected modding runtime + its launcher for Darktide.
It launches the game modded via DLL injection — no files in the game
directory, no bundle-database patching — and stays out of the way for vanilla
play (launch the game from Steam and it runs unmodified). The runtime is
**Enginseer (aka the Mod Loader)** plus the launcher that delivers it.

- **Architecture:** [`docs/architecture/RUNTIME.md`](../docs/architecture/RUNTIME.md)
  — the subcomponents, the Rust↔C seam, the launcher flow, the env-var
  contract, logging.
- **DMF integration:** [`docs/architecture/ENGINSEER-DMF.md`](../docs/architecture/ENGINSEER-DMF.md)
  — how the Enginseer loads DMF (the Darktide Mod Framework) + user mods, the
  IO re-rooting, and the load timing.
- **Project overview:** [`../README.md`](../README.md) (end-user), and
  [`../AGENTS.md`](../AGENTS.md) (agent orientation + ops).

> **Audience:** developers / power users. This is the build + component detail;
> the root [`README.md`](../README.md) is the end-user entry point.

## Sub-components

| Dir | What it is |
| --- | --- |
| **`discovery/`** | Rust crate — the LuaJIT discovery engine. A pure library (no I/O, no global state): a PE image (`&[u8]`) → the 16 LuaJIT/engine function addresses. 100% safe Rust in core logic; offline-testable. Compiled to a C-ABI staticlib (`libmagos_discovery.a`). |
| **`shell/`** | The injected C DLL — **`magos_shell.dll`**. Hooks the Lua VM (`lua_newstate` → the production trampoline), discovers the LuaJIT function addresses in-process, and loads the staged Enginseer in engine context. Linked with the Rust discovery staticlib + MinHook. |
| **`launcher/`** | The C injector — **`magos_launcher.exe`**. `CreateProcess(Darktide.exe, SUSPENDED)` → inject `magos_shell.dll` → wait for the hook-ready signal → `ResumeThread`. Resolves the flag/env config and publishes it into the child env. |
| **`enginseer/`** | The Lua mod loader — Enginseer. Runs in engine context, bridges pcall#1 to the engine's late boot (deferred bootstrap), and loads DMF + user mods. Entry `enginseer.lua` + v2 modules (`file`, `hook`, `class_patch`, `require_wrap`, `lifecycle`, `mod_manager`). Runtime-controlled (ships with the build). |
| **`tests/`** | C unit tests (run via wine). |
| **`bin/`** | Build outputs (gitignored). Where `make build` lands everything. |

The workspace root (`Cargo.toml` / `Cargo.lock` / `Makefile`) lives here in
`runtime/`, not the repo root — **all build/test commands run from `runtime/`**.

## Build artifacts (`make build` → `bin/`)

- **`bin/magos_launcher.exe`** — the C injector (from `launcher/`). The host
  process that creates the game suspended, injects the shell DLL, waits for the
  hook-ready handshake, and resumes. Sets the Steam app id and publishes the
  runtime's env vars.
- **`bin/magos_shell.dll`** — the injected DLL (from `shell/`): the C shell
  linked with the Rust discovery staticlib (`libmagos_discovery.a`) + MinHook,
  into one PE DLL. Hooks the Lua VM, runs discovery in-process, and stages the
  Enginseer.
- **`bin/enginseer/`** — the Enginseer Lua (from `enginseer/`), staged next to
  the launcher/DLL. This is the **runtime-controlled** root the launcher
  publishes as `MAGOS_ENGINSEER_PATH` (default `<launcher-dir>/enginseer/`); the
  trampoline loads `enginseer.lua` from here. User mods live in a separate,
  user-controlled mod root (see [Two roots](#two-roots)).

## Build + test

Run from `runtime/`:

```sh
export PATH="$HOME/.cargo/bin:$PATH"   # system rust lacks the windows-gnu target
source ../_local/DARKTIDE.env          # sets DARKTIDE_GAME_DIR (for oracle tests)

make build          # cross-compile DLL + launcher (x86_64-pc-windows-gnu)
make check          # verify valid PE DLL with DllMain
make test           # C tests (via wine) + Rust tests
make enginseer-test # Enginseer Lua tests (offline LuaJIT harness; no game/wine)
```

- **MinGW cross-compile** from Linux produces the Windows DLL + launcher. MSVC
  native on Windows is also supported (CI runs both).
- **Oracle tests** run discovery against the real `Darktide.exe` (resolved via
  `DARKTIDE_GAME_DIR`). The engine is build-agnostic (Tier-2 self-validation
  passes on any build; Tier-1 exact-match skips if the SHA differs from the
  pinned one). In CI (no game install) they skip cleanly.
- **Enginseer Lua tests** are an offline LuaJIT harness — no game, no wine.
- **`test-hooks` feature** gates the debug panic-boundary symbol out of release
  builds: `cargo test --features test-hooks -p magos-discovery` (and
  `cargo clippy --all-targets --features test-hooks -- -D warnings`). `make test`
  handles this.

## Launcher CLI

The launcher is **flag-based**, where every setting follows
**flag > env var > default**. `--game-binary` is the only required flag; the
shell DLL, log, and Enginseer root default next to the launcher exe.

| Flag | Env var | Default |
| --- | --- | --- |
| `--game-binary <path>` | `MAGOS_ENGINSEER_GAME_BINARY` | — **(required)** |
| `--magos-shell <path>` | `MAGOS_ENGINSEER_SHELL` | `<launcher-dir>\magos_shell.dll` |
| `--enginseer-path <dir>` | `MAGOS_ENGINSEER_PATH` | `<launcher-dir>\enginseer` |
| `--mod-path <path>` | `DARKTIDE_MOD_PATH` | unset (mods won't load) |
| `--log-file <path>` | `MAGOS_ENGINSEER_LOG_FILE` | `<launcher-dir>\magos_enginseer.log` |
| `--log-level <level>` | `MAGOS_ENGINSEER_LOG_LEVEL` | `info` (`error`/`warn`/`info`/`debug`/`trace`) |
| `--steam-app-id <id>` | `MAGOS_ENGINSEER_STEAM_APP_ID` | `1361210` |

See [`docs/architecture/RUNTIME.md`](../docs/architecture/RUNTIME.md) →
`launcher/` for the full table, the env-var contract, and logging details.

## Two roots

The Enginseer (the loader) and the mods live in **separate** directories,
resolved as two values:

- **`MAGOS_ENGINSEER_PATH`** (`--enginseer-path`; default
  `<launcher-dir>\enginseer`) — **runtime-controlled**. Holds `enginseer.lua`
  + its modules. Ships with the build (`make build` stages it); a DMF/mod
  update never requires a runtime rebuild.
- **`MAGOS_MOD_PATH`** (from `--mod-path` / `DARKTIDE_MOD_PATH`) —
  **user/mod-manager-controlled**. Holds DMF + user mods +
  `mod_load_order.txt`. The Enginseer roots its mod-facing IO here.

The split keeps the loader's own code runtime-owned while the mods it loads are
user-owned. Detail in
[`docs/architecture/ENGINSEER-DMF.md`](../docs/architecture/ENGINSEER-DMF.md).
