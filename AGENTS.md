# AGENTS.md — darktide-mod-magos

> Orientation for any agent working in this repo. Read this first. This file
> is for **agents**, not humans — the human-facing entry point is `README.md`.

## What this is

**darktide-mod-magos** is a mod manager for Warhammer 40,000: Darktide. It
launches the game modded via DLL injection (no game-directory footprint, no
bundle-database patching) and stays out of the way for vanilla play (launch
from Steam = unmodified game).

Architecture: a **Hybrid** — Enginseer (runtime): a Rust discovery pure-library
(C-ABI staticlib) + a C live-game shell, linked into one DLL, delivered by
`CreateRemoteThread`. Magos Modificus (the mod-manager app) is planned, not yet
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

- **`main`** — production. Enginseer (the injected modding runtime + launcher) is
  merged as the production seed; Magos Modificus is not yet built.
- **`poc`** — historical proof-of-concept, reference only. Not built upon.
- Development is branch + PR; no unreviewed merges to `main` (reviewed +
  covered + qa'd + CI green).

## Directory structure (current `main`)

```
enginseer/          Enginseer (runtime) — the injected modding runtime + injector
  Cargo.toml        workspace root (members = ["discovery"])
  Cargo.lock
  Makefile          builds Enginseer: make build / check / test / mod-loader-test / clean
                    (run from enginseer/ — all commands below assume CWD = enginseer/)
  bin/              ALL build outputs land here (gitignored): magos_launcher.exe,
                      magos_shell.dll + mod_loader/ (the staged mod loader Lua)
  target/           cargo build artifacts (gitignored)
  discovery/        Rust crate: LuaJIT discovery engine (pure library, C-ABI staticlib)
  shell/            C shell — the injected DLL (DllMain, MinHook, lua_newstate hook)
  launcher/         C launcher — CreateRemoteThread injector + hook-ready handshake
  mod_loader/       the mod loader — Enginseer's runtime-staged Lua loader (LuaJIT):
                      init.lua entry + modules (file/hook/class_patch/
                      require_wrap/lifecycle/mod_manager) + init.v1.lua +
                      tests/ (offline LuaJIT harness, run via `make mod-loader-test`).
                      `make build` stages the entry + modules into bin/mod_loader/
                      (the Enginseer-controlled loader root, self-located by the
                      shell from its own DLL path and set as MOD_LOADER_DIR).
                      Vendored DMF/test-mod/mods.lst live in a repo-root mods/
                      dir (gitignored — the mod root, pointed at by --mod-path).
  tests/            C unit tests (run via wine)
mod-manager/        Magos Modificus — the mod manager app (not yet built; placeholder)
docs/               architecture, poc (frozen), reference
.github/workflows/  CI: mingw-build (Linux cross-compile) + msvc-build (Windows native)
.gitignore          ignores enginseer/target, enginseer/bin, build artifacts, _local/
```
The workspace root (`Cargo.toml`/`Cargo.lock`/`Makefile`) lives under
`enginseer/`, not the repo root — all build/test commands run from there.

## Agent ops

Build + test (Linux dev box) — run from `enginseer/`:
```sh
export PATH="$HOME/.cargo/bin:$PATH"   # system rust lacks the windows-gnu target
source ../_local/DARKTIDE.env          # sets DARKTIDE_GAME_DIR (for oracle tests)
make build          # cross-compile DLL + launcher (x86_64-pc-windows-gnu)
make check          # verify valid PE DLL with DllMain
make test           # C tests (via wine) + Rust tests + mod loader Lua tests
make mod-loader-test # mod loader Lua tests (offline LuaJIT harness; no game/wine)
```
Build outputs land in `enginseer/bin/`; cargo's artifacts in `enginseer/target/`.
- **Oracle tests** run discovery against the real `Darktide.exe` (resolved via
  `DARKTIDE_GAME_DIR`). The engine is build-agnostic (Tier-2 self-validation
  passes on any build; Tier-1 exact-match skips if the SHA differs from the
  pinned one).
- **`test-hooks` feature** gates the debug panic-boundary symbol out of
  release builds. Tests use it: `cargo test --features test-hooks -p
  magos-discovery`. `make test` handles this; clippy too
  (`cargo clippy --all-targets --features test-hooks -- -D warnings`).
- **Launcher CLI** is flag-based (**flag > env var > default**; `--game-binary`
  is the only required flag; the shell DLL is hardcoded next to the launcher
  and self-locates the mod loader). `--mod-path` (env `DARKTIDE_MOD_PATH`) is
  the user-controlled mod root; the loader root is self-located by the shell
  from its own DLL path (`<dll-dir>/mod_loader/`, set as the internal
  `MOD_LOADER_DIR` — not an env var/flag). See
  `docs/architecture/ENGINSEER.md` → `launcher/` for the full flag/env/default
  table + the env-var contract.
- **Shell log** is `magos_enginseer.log`, structured + level-filtered via
  `MAGOS_ENGINSEER_LOG_LEVEL` (default `info`; crank to `debug`/`trace` for
  verbose output). The mod loader's Lua-side `print` lines go to the engine's
  console log, not the shell log — see ENGINSEER.md → Logging.
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

Keep the established thematic names — **Enginseer** (the runtime) and **Magos**
(the app) — don't rename them. Going forward, use plain, descriptive names for
new components/modules (Rust crates, C modules, Lua modules, functions); the
runtime's Lua loader is `mod_loader` (descriptive), not a themed name. Reserve
any Warhammer 40k / Adeptus Mechanicus flavor for the UI (Magos Modificus); docs
and code read as plain engineering documentation.

- **Folders/filenames:** lowercase (`enginseer/mod_loader/init.lua`).
- **Prose/docs:** "Enginseer" is the runtime's public name — first mention in a
  doc is "Enginseer (runtime)", thereafter "the Enginseer runtime" / "Enginseer".
  The Lua loader inside it is "the mod loader" (prose) / `mod_loader`
  (code/dir references).
- Don't obscure — names should be descriptive and accessible, not cryptic.

## README pattern

Docs follow a two-tier README pattern:

- **Root `README.md`** — audience is the **general / end user**: what Magos is,
  its components, and how to get it running. **No build internals.**
- **Component-dir `README.md`** (e.g. `enginseer/README.md`) — audience is
  **developers / power users**: build instructions, sub-component details,
  testing, links to the architecture specs.

The **root README links to** the component READMEs — it does **not** duplicate
their content. When a component gets (or changes) a README, ensure the root
links to it and that the split holds (user-facing up top, dev detail under the
component).

## Before opening a PR — keep docs current

Docs must reflect the code in the PR. Before opening a PR for any change that
affects repo structure, build, architecture, or ops, update:
- **`AGENTS.md`** (this file) — directory structure, ops, architecture
  pointers — to reflect the change.
- **`README.md`** (root) — if the **user-facing** structure/status changed.
  Keep it user-facing (see [README pattern](#readme-pattern)); dev/build detail
  goes in the relevant component README, and the root must link to it.
- **Component-dir `README.md`** (e.g. `enginseer/README.md`) — for build/dev
  detail under that component; ensure the root links to it.
- **`docs/architecture/`** for any architecture change; `docs/reference/` for
  game/ecosystem facts.

Then ensure `make build/check/test` + clippy pass. **Outdated docs in a PR are
a review blocker** — including this file.
