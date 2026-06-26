# AGENTS.md — darktide-mod-magos

> Orientation for any agent working in this repo. Read this first. This file
> is for **agents**, not humans — the human-facing entry point is `README.md`.

## What this is

**darktide-mod-magos** is a mod manager for Warhammer 40,000: Darktide. It
launches the game modded via DLL injection (no game-directory footprint, no
bundle-database patching) and stays out of the way for vanilla play (launch
from Steam = unmodified game).

Architecture: a **Hybrid** — the runtime: a Rust discovery pure-library
(C-ABI staticlib) + a C live-game shell, linked into one DLL, delivered by
`CreateRemoteThread`. Darktide Magos (the mod-manager app) is planned, not yet
built. See `docs/architecture/` for the full architecture.

## Baseline (read before planning)

The POC (on the `poc` branch) is a capability proof and reference — **not** a
pre-release of production code. Production is built ground-up with
testability, review, and production-readiness as first-class goals. The POC
carries forward (1) proof of feasibility and (2) validated technical
constraints that are properties of the Darktide binary (in
`docs/reference/darktide-binary.md`). It does not carry forward code.
Requirements, architecture, and technology choices are made fresh.

## Repository state

- **`main`** — production. The runtime (the injected modding runtime + launcher) is
  merged as the production seed; Darktide Magos is not yet built.
- **`poc`** — historical proof-of-concept, reference only. Not built upon.
- Development is branch + PR; no unreviewed merges to `main` (reviewed +
  covered + qa'd + CI green).

## Directory structure (current `main`)

```
runtime/            the injected modding runtime + injector
  Cargo.toml        workspace root (members = ["discovery"])
  Cargo.lock
  Makefile          builds the runtime: make build / check / test / enginseer-test / clean
                    (run from runtime/ — all commands below assume CWD = runtime/)
  bin/              ALL build outputs land here (gitignored): magos_launcher.exe,
                      magos_shell.dll + enginseer/ (the staged Enginseer Lua)
  target/           cargo build artifacts (gitignored)
  discovery/        Rust crate: LuaJIT discovery engine (pure library, C-ABI staticlib)
  shell/            C shell — the injected DLL (DllMain, MinHook, lua_newstate hook)
  launcher/         C launcher — CreateRemoteThread injector + hook-ready handshake
  enginseer/        Enginseer (aka the Mod Loader) — the runtime-staged loader (LuaJIT):
                      enginseer.lua entry + v2 modules (file/hook/class_patch/
                      require_wrap/lifecycle/mod_manager) + enginseer.v1.lua +
                      tests/ (offline LuaJIT harness, run via `make enginseer-test`).
                      `make build` stages the entry + v2 modules into bin/enginseer/
                      (the runtime-controlled root, published as MAGOS_ENGINSEER_PATH).
                      Vendored DMF/test-mod/mod_load_order live in a repo-root mods/
                      dir (gitignored — the mod root, pointed at by --mod-path).
  tests/            C unit tests (run via wine)
mod-manager/        Darktide Magos — the mod manager app (not yet built; placeholder)
docs/               architecture, poc (frozen), reference
.github/workflows/  CI: mingw-build (Linux cross-compile) + msvc-build (Windows native)
.gitignore          ignores runtime/target, runtime/bin, build artifacts, _local/
```
The workspace root (`Cargo.toml`/`Cargo.lock`/`Makefile`) lives under
`runtime/`, not the repo root — all build/test commands run from there.

## Agent ops

Build + test (Linux dev box) — run from `runtime/`:
```sh
export PATH="$HOME/.cargo/bin:$PATH"   # system rust lacks the windows-gnu target
source ../_local/DARKTIDE.env          # sets DARKTIDE_GAME_DIR (for oracle tests)
make build          # cross-compile DLL + launcher (x86_64-pc-windows-gnu)
make check          # verify valid PE DLL with DllMain
make test           # C tests (via wine) + Rust tests
make enginseer-test # Enginseer Lua tests (offline LuaJIT harness, 93 tests; no game/wine)
```
Build outputs land in `runtime/bin/`; cargo's artifacts in `runtime/target/`.
- **Oracle tests** run discovery against the real `Darktide.exe` (resolved via
  `DARKTIDE_GAME_DIR`). The engine is build-agnostic (Tier-2 self-validation
  passes on any build; Tier-1 exact-match skips if the SHA differs from the
  pinned one).
- **`test-hooks` feature** gates the debug panic-boundary symbol out of
  release builds. Tests use it: `cargo test --features test-hooks -p
  magos-discovery`. `make test` handles this; clippy too
  (`cargo clippy --all-targets --features test-hooks -- -D warnings`).
- **Launcher CLI** is flag-based (**flag > env var > default**; `--game-binary`
  is the only required flag; shell DLL + log default next to the launcher).
  `--enginseer-path` (env `MAGOS_ENGINSEER_PATH`, default
  `<launcher-dir>/enginseer/`) is the runtime-controlled Enginseer root —
  `make build` stages the Enginseer Lua there; `--mod-path` (env
  `DARKTIDE_MOD_PATH`) is the user-controlled mod root. See
  `docs/architecture/RUNTIME.md` → `launcher/` for the full flag/env/default
  table + the env-var contract.
- **Shell log** is `magos_enginseer.log`, structured + level-filtered via
  `MAGOS_ENGINSEER_LOG_LEVEL` (default `info`; recon at `debug`/`trace`). The
  Enginseer's Lua-side `print` lines go to the engine's console log, not the
  shell log — see RUNTIME.md → Logging.
- **`_local/`** is gitignored (local env, e.g. `DARKTIDE.env`). Never commit
  it or the game binary.
- **CI** runs on push/PR to `main`: mingw (Linux cross-compile + wine tests)
  + msvc (Windows native). Both gate on clippy + tests.

## Key docs

- `docs/architecture/` — the production architecture (component model, the
  Hybrid, the seam, test strategy, build, launcher flow).
- `docs/reference/darktide-binary.md` — validated game-binary constraints.
- `docs/reference/darktide-framework-analysis.md` — the existing modding
  ecosystem being replaced.
- `docs/poc/` — frozen POC handoff (historical reference).

## Conventions

- **Conventional Commits** (`type(scope): subject`); commit freely on feature
  branches. Branch + PR flow; no unreviewed merges to `main`.
- Don't commit secrets, the game binary, or anything under `_local/`.

## Naming convention

Keep the existing thematic names that are already established (Enginseer for the
mod loader, Magos for the app) — don't rename them. Going forward, use plain,
descriptive names for new components/modules (Rust crates, C modules, Lua
modules, functions). Reserve any Warhammer 40k / Adeptus Mechanicus flavor for
the UI (Darktide Magos); docs and code read as plain engineering documentation.

- **Folders/filenames:** lowercase (`runtime/enginseer/enginseer.lua`).
- **Prose/docs:** "Enginseer" is the established name for the loader; first
  mention in a doc is "Enginseer (aka the Mod Loader)", thereafter "Enginseer".
- Don't obscure — names should be descriptive and accessible, not cryptic.

## Before opening a PR — keep docs current

Docs must reflect the code in the PR. Before opening a PR for any change that
affects repo structure, build, architecture, or ops, update:
- **`AGENTS.md`** (this file) — directory structure, ops, architecture
  pointers — to reflect the change.
- **`README.md`** if the user-facing structure/status changed.
- **`docs/architecture/`** for any architecture change; `docs/reference/` for
  game/ecosystem facts.

Then ensure `make build/check/test` + clippy pass. **Outdated docs in a PR are
a review blocker** — including this file.
