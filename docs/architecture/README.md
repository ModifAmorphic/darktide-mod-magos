# Architecture

darktide-mod-magos launches Darktide modded via DLL injection — no
game-directory footprint, no bundle-database patching — and stays out of the
way for vanilla play (launch from Steam = the unmodified game).

## Component model

- **Enginseer (runtime) (`enginseer/`)** (built): the injected modding runtime +
  its launcher. A Rust discovery pure-library + a C live-game shell, linked into
  one DLL, delivered by `CreateRemoteThread`; the C shell stages the
  **mod loader** — the runtime-controlled Lua loader that loads DMF + user mods.
  See `docs/architecture/ENGINSEER.md` for the full subcomponent breakdown.
- **Magos Modificus — `magos-modificus/`** (Phases 0–2 built): the user-facing
  app — staging-directory management, load order, profiles, dependency
  resolution, the "Launch Modded" button. The backend libraries are implemented;
  the UI is still the bare Phase-0 window (Phase 3). See
  [`MAGOS-MODIFICUS.md`](MAGOS-MODIFICUS.md) for the target architecture.
- **DMF + user mods** (Lua, not our code): the Darktide-Mod-Framework Lua
  files, preserved as-is; only the harness is replaced. Loaded by the mod loader
  at runtime, from the user-controlled mod root (`--mod-path`) — distinct from
  the Enginseer-controlled loader root (the two-path split; see
  `docs/architecture/MOD_LOADER-DMF.md`).

## The Enginseer runtime — the Hybrid

The Enginseer runtime is a **Hybrid**: a Rust pure-library for discovery + a C
shell for everything that touches the live game, linked into one DLL. Each
language is placed where its benefit holds:

- **Rust discovery (`enginseer/discovery/`)** — a pure library (no I/O, no global
  state) that takes a PE image as `&[u8]` and returns the 16 LuaJIT function
  addresses. 100% safe Rust in core logic; offline-testable against a binary
  fixture. Compiled to a C-ABI staticlib.
- **C shell (`enginseer/shell/`)** — the injected DLL: `DllMain` worker, MinHook
  on `lua_newstate`, the `lua_gettop` call, the C-ABI seam call into Rust. The
  irreducibly-unsafe live-game bits, where C's native ergonomics + domain
  track record apply.

### The seam

The Rust↔C boundary is a tiny C-ABI surface: `magos_discover` /
`magos_discover_detail` (and the test-only `magos_test_panic_boundary`, gated
behind the `test-hooks` feature). It's held at the safety boundary — Rust owns
stateless computation; C owns everything touching the live game. The shared
contract is the `MagosAddressTable` struct (`#[repr(C)]`, mirrored in
`enginseer/shell/include/magos_discovery.h`) + return codes.

### Panic boundary

Every `extern "C"` entry point wraps its body in `catch_unwind` (primary
containment — a panic in the pure-lib is caught at the seam and returned as a
sentinel, no UB crossing into C). `panic = "unwind"` for the linked build; a
separate `panic-abort` profile is the fail-safe backstop. (`catch_unwind` and
`panic=abort` are mutually exclusive per-build, so the unwind build links and
the abort profile is a separate demonstration.)

### Launcher flow (`enginseer/launcher/`)

`CreateProcess(Darktide.exe, CREATE_SUSPENDED)` → `VirtualAllocEx` +
`WriteProcessMemory` (DLL path) → `CreateRemoteThread(LoadLibraryA, dllpath)`
→ **wait for the `magos_hook_ready` named event** → `ResumeThread`.

The hook-ready wait is essential: `DllMain` returns instantly (it only spawns
a worker), and the worker doesn't enable the `lua_newstate` hook until after
discovery — resuming before the hook is ready means the engine calls
`lua_newstate` before the hook is installed. The launcher also sets
`SteamAppId=1361210` + `SteamGameId=1361210` in the child env so the correct
appID reaches `steamclient`.

### Discovery

All 16 LuaJIT/engine functions are discovered at runtime (no hardcoded
addresses), via two methods + `.pdata` gap handling:
- **Method A — string-anchor**: `stingray::` namespaced strings in `.rdata` →
  LEA cross-references → `.pdata` function (engine functions).
- **Method B — source-pattern**: match compiled function bodies against LuaJIT
  2.1 source (the C API cluster).
- `.pdata` gaps: CFG thunks (`E9 rel32`), leaf functions, import thunks
  (`FF 25`).

The engine is **build-agnostic** — all 16 functions are found at
uniformly-shifted RVAs across binary versions (validated across builds; e.g. a
+0xf0680 cluster shift). The matchers are the ongoing maintenance surface
(re-tune on a LuaJIT version change — rare; LuaJIT is static Stingray code).

## Test strategy

- **Rust** (`cargo test --features test-hooks -p magos-discovery`): unit
  (seam null-arg rejection, panic containment), integration (oracle — all 16
  vs the real `Darktide.exe` via `DARKTIDE_GAME_DIR`; `magos_discover_detail`
  error path; synthetic-PE parsing).
- **C** (`make test`, via wine): SteamAppId env-setting; CreateRemoteThread
  injection + hook-ready handshake + resume against a benign stub process.
- **Mod loader Lua** (`make mod-loader-test`, offline LuaJIT harness — no
  game/wine): the loader + the deferred bootstrap, the IO re-rooting, the
  scan/load split, the `Managers.mod` surface.
- **Live** (validated end-to-end on Linux/Proton): game reaches the main menu,
  `lua_newstate` hook fires, `L` captured, `lua_gettop(L)=0` (confirms the
  struct offsets in-process), all 16 discovered in-process matching the
  offline oracle.

## Build

MinGW cross-compile from Linux (`make build`/`check`/`test`) + MSVC native on
Windows (CI). Both gate on `cargo clippy --all-targets --features test-hooks --
-D warnings` + tests. CI: `.github/workflows/mingw-build.yml` +
`msvc-build.yml`.

## Production launcher insights (from live testing — feed Magos Modificus)

1. **Proton launch model**: Steam non-Steam-game (the launcher) + forced
   Proton + `STEAM_COMPAT_DATA_PATH` → launcher creates+suspends+injects. Steam
   UX + zero game-dir footprint + correct hook timing in one design.
2. **Steam appID**: the launcher sets `SteamAppId`/`SteamGameId` to `1361210`
   (otherwise the non-Steam-shortcut's hashed appID denies `SteamAPI_Init`).
3. **Hook-ready handshake**: `CreateProcess(SUSPENDED) → inject → wait for
   hook-ready → ResumeThread` (resuming right after `LoadLibrary` returns is
   too early).

## References

- `docs/architecture/ENGINSEER.md` — the Enginseer runtime architecture: the
  subcomponents, the Rust↔C seam, the launcher flow, the env-var contract,
  logging.
- `docs/architecture/MAGOS-MODIFICUS.md` — the Magos Modificus target
  architecture: project layout, domain libraries, the Enginseer contract it
  consumes, profiles, the Windows/Linux launch paths, v1 scope.
- `docs/architecture/MOD_LOADER-DMF.md` — the mod_loader↔DMF integration: the
  loader, the IO re-rooting, the load timing, the two-path split.
- `docs/reference/darktide/darktide-binary.md` — the validated game-binary
  constraints (addresses, struct offsets, sandboxed `_G`, discovery methodology).
- `docs/reference/community-tools/darktide-framework-analysis.md` — the existing
  modding ecosystem being replaced.
- `docs/reference/magos-modificus/` — per-library API reference for the Magos
  Modificus backend libraries.
