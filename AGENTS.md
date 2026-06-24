# AGENTS.md — darktide-mod-magos

> Orientation for any agent working in this repo. Read this first. This file
> is for **agents**, not humans — the human-facing entry point is `README.md`.

## What this is

**darktide-mod-magos** is a mod manager for Warhammer 40,000: Darktide. It
launches the game modded via DLL injection (no game-directory footprint, no
bundle-database patching) and stays out of the way for vanilla play (launch
from Steam = unmodified game).

Architecture: a **Hybrid** Component A — a Rust discovery pure-library
(C-ABI staticlib) + a C live-game shell, linked into one DLL, delivered by
`CreateRemoteThread`. Component B (the mod-manager app) is planned, not yet
built. See `docs/decisions/0001-component-a-language-and-structure.md` (ADR:
Hybrid adopted) for the decision + outcome.

## Baseline (read before planning)

The POC (on the `poc` branch) is a capability proof and reference — **not** a
pre-release of production code. Production is built ground-up with
testability, review, and production-readiness as first-class goals. The POC
carries forward (1) proof of feasibility and (2) validated technical
constraints that are properties of the Darktide binary (below). It does not
carry forward code. Requirements, architecture, and technology choices are
made fresh.

## Repository state

- **`main`** — production. Component A (the injected runtime + launcher) is
  merged as the production seed; Component B is not yet built.
- **`poc`** — historical proof-of-concept, reference only. Not built upon.
- Development is branch + PR; no unreviewed merges to `main` (reviewed +
  covered + qa'd + CI green).

## Directory structure (current `main`)

```
runtime/            Component A — the injected modding runtime + injector
  discovery/        Rust crate: LuaJIT discovery engine (pure library, C-ABI staticlib)
  shell/            C shell — the injected DLL (DllMain, MinHook, lua_newstate hook)
  launcher/         C launcher — CreateRemoteThread injector + hook-ready handshake
  tests/            C unit tests (run via wine)
mod-manager/        Component B — the mod manager app (not yet built; placeholder)
docs/               architecture, decisions (ADRs), planning, poc (frozen), reference
.github/workflows/  CI: mingw-build (Linux cross-compile) + msvc-build (Windows native)
Cargo.toml          workspace root (members = ["runtime/discovery"])
Cargo.lock
Makefile            builds Component A: make build / check / test / clean
.gitignore          ignores /target, build artifacts, _local/
```

## Agent ops

Build + test (Linux dev box):
```sh
export PATH="$HOME/.cargo/bin:$PATH"   # system rust lacks the windows-gnu target
source _local/DARKTIDE.env             # sets DARKTIDE_GAME_DIR (for oracle tests)
make build    # cross-compile DLL + launcher (x86_64-pc-windows-gnu)
make check    # verify valid PE DLL with DllMain
make test     # C tests (via wine) + Rust tests
```
- **Oracle tests** run discovery against the real `Darktide.exe` (resolved via
  `DARKTIDE_GAME_DIR`). The engine is build-agnostic (Tier-2 self-validation
  passes on any build; Tier-1 exact-match skips if the SHA differs from the
  pinned one).
- **`test-hooks` feature** gates the debug panic-boundary symbol out of
  release builds. Tests use it: `cargo test --features test-hooks -p
  magos-discovery`. `make test` handles this; clippy too
  (`cargo clippy --all-targets --features test-hooks -- -D warnings`).
- **`_local/`** is gitignored (local env, e.g. `DARKTIDE.env`). Never commit
  it or the game binary.
- **CI** runs on push/PR to `main`: mingw (Linux cross-compile + wine tests)
  + msvc (Windows native). Both gate on clippy + tests.

## Validated technical constraints (properties of the Darktide binary)

- LuaJIT 2.1, statically linked, non-GC64 (LJ_64, 32-bit MRefs). 16 function
  addresses confirmed at runtime in the live game.
- **Sandboxed `_G`**: the engine replaces `print`/`require`/`dofile`/
  `loadfile`/`load`; stdlib not exposed to injected chunks. Solved via
  C-function bootstrap (`lua_pushcclosure` + `lua_setfield`), **not**
  `luaL_openlibs` (which is destructive — overwrites engine wrappers).
- `lua_State` field offsets (LJ_64 non-GC64): `glref`@0x08, `base`@0x10,
  `top`@0x18, `stack`@0x24, `stacksize`@0x38.
- Retry-on-error timing: the injected chunk self-checks for readiness and
  retries on the engine's `lua_pcall` calls.
- Pinned binary SHA-256 `132eed5f…` (the `docs/poc` addresses are for this
  build; the engine found all 16 at uniformly-shifted RVAs on a newer build —
  game-update resilience validated).
- Full detail: `docs/poc/production-spec.md` + `docs/poc/lua-vm-injection-anchors.md`.

## Key docs

- `docs/decisions/0001-component-a-language-and-structure.md` — ADR: Component
  A language/structure (Hybrid, accepted) + spike outcome + production launcher
  insights.
- `docs/planning/spike-001-component-a-language.md` — the spike spec that
  became Component A's seed.
- `docs/architecture/` — production architecture (component boundaries,
  contracts, test strategy); grows with the project.
- `docs/decisions/` — ADRs. `docs/planning/` — work breakdown, sequencing, spikes.
- `docs/reference/` — living reference: game-binary facts + the existing
  modding ecosystem being replaced.
- `docs/poc/` — frozen POC handoff (historical; the constraints above are
  distilled from here).

## Conventions

- **Conventional Commits** (`type(scope): subject`); commit freely on feature
  branches. Branch + PR flow; no unreviewed merges to `main`.
- Don't commit secrets, the game binary, or anything under `_local/`.

## Before opening a PR — keep docs current

Docs must reflect the code in the PR. Before opening a PR for any change that
affects repo structure, build, architecture, or ops, update:
- **`AGENTS.md`** (this file) — directory structure, ops, architecture
  pointers — to reflect the change.
- **`README.md`** if the user-facing structure/status changed.
- **`docs/decisions/`** (an ADR) for any architecture decision;
  `docs/architecture/` + `docs/planning/` as needed.

Then ensure `make build/check/test` + clippy pass. **Outdated docs in a PR are
a review blocker** — including this file.
