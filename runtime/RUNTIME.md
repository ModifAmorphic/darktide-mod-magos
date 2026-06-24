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
- **State:** production-quality (the Component A seed). Stable; no expansion
  planned.

### `shell/` — C live-game shell (the injected DLL)  *(minimal slice built; expansion planned)*

The DLL injected into Darktide. `DllMain` spawns a worker that: runs discovery
(via the seam) → installs the `lua_newstate` hook → **[to build]** bootstraps
DMF → loads mods → reports status.

- **Built (minimal validation slice):** discovery call, `lua_newstate` MinHook
  + `L` capture, `lua_gettop` call, hook-ready signal.
- **To build (the shell expansion):**
  - **DMF bootstrap** — load `dmf_loader.lua` from the staging dir; register
    DMF's 6 dependency C functions (`Mods.original_require`, `Mods.lua.io`,
    `__print`, `Mods.file.dofile`, …) via `lua_pushcclosure` +
    `lua_setfield(L, LUA_GLOBALSINDEX, name)`.
  - **`lua_pcall` hook + retry-on-error** — the injection mechanism: hook
    `lua_pcall`, inject the DMF-loader chunk, self-check for readiness and
    retry on the engine's `lua_pcall` calls (POC-validated timing).
  - **Status reporting** — report discovery results, DMF load, per-mod load,
    errors to the launcher (via the internal status channel).
- **Open decisions** (resolve as the expansion starts — see below):
  - `Mods.original_require`: (a) wrap the engine's real `require`, or
    (b) file-based loader from staging. *Lean: (b) — mods require their own
    files, not engine internals.*
  - `Mods.lua.io`: (a) C Win32 (`CreateFileW`/`ReadFile`/`WriteFile`), or
    (b) expose the engine's `io` library. *Lean: (a) — full control, no
    sandbox dependency.*
  - Status channel mechanism (shell→launcher): file (simple, v1) vs pipe.
    *Lean: file for v1.*

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
  the runtime finds `dmf_loader.lua`; DMF reads mods + load order via the
  runtime's C functions. `mod_load_order.txt` is a Darktide Magos → DMF
  artifact (the runtime is the conduit; it does not parse the load order).
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
  armed before `main`); the status channel (shell→launcher, mechanism TBD).

## Open decisions (resolve as the shell expansion starts)

1. **`Mods.original_require`** — (a) wrap the engine's real `require`, or
   (b) file-based loader from staging. *Lean (b): DMF mods `require` their
   own files, not engine internals; simpler + sufficient.*
2. **`Mods.lua.io`** — (a) C Win32 file API, or (b) expose the engine's `io`
   library. *Lean (a): full control, no dependency on locating the engine's
   sandboxed `io`.*
3. **Status channel (shell→launcher)** — file (simple, v1) vs named pipe
   (richer, real-time). *Lean file for v1; the launcher→Darktide Magos relay
   via stdout is settled regardless.*

These are implementation choices, not architecture — the contracts above hold
either way.

## Out of scope for the runtime

- **Dependency resolution / load-order computation** — Darktide Magos's job
  (it writes `mod_load_order.txt`); the runtime bootstraps DMF, which reads it.
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
