# 0002 — USVFS staging spike

> Status: **throwaway investigation on the `spike/usvfs-staging` branch.** The
> code under `spike/usvfs/` is disposable recon, not production code, and is not
> intended to merge. This doc records the questions, the chosen integration
> shape, the harness design, and the results so the decision survives the spike.

## Why this spike exists

Mod staging currently uses directory symlinks, which on Windows require
Developer Mode or admin (`0001-junction-staging.md` documents the bug and a
privilege-free junction fallback). Before committing to junctions, we owe
ourselves an honest evaluation of **USVFS** as the alternative: a true virtual
file system that would *eliminate* the staging directory entirely (no links,
no `ClearStagedDir`/`DeleteStagedEntry` data-safety logic, no NTFS-only /
cross-volume caveats) and keep file-level overlay open as a future capability.

USVFS was previously dismissed on premises that turned out to be wrong (an
asserted anti-cheat blocker; Darktide's Easy Anti-Cheat was **removed on
2024-06-25**, and relay's DLL injection already works today, which is the
deductive proof). This spike is the evaluation that should have happened the
first time.

## Grounded facts (verified, not memory)

- **USVFS** (`github.com/ModOrganizer2/usvfs`, v0.5.7.2, June 2025): C++
  library, `extern "C"` stdcall API (see `include/usvfs/usvfs.h`), P/Invokable
  from .NET. GPLv3, same as Curator. Privilege-free by design (API hooking, not
  reparse points). Ships binary release assets, so building from source with
  cmake/vcpkg is not required. Maintainer labels it alpha and reserves the
  right to relicense. Windows-only (Win32 detouring).
- **Darktide has no anti-cheat.** EAC removed 2024-06-25 (PCGamingWiki + SteamDB
  depot citation; AreWeAntiCheatYet status: Supported). No blocker for process
  hooking.
- **Relay's injection** (`darktide-modificus-relay/src/launcher/src/launcher.c`,
  `inject_and_resume`): `CreateProcessA(CREATE_SUSPENDED)` -> allocate + write
  DLL path -> `CreateEvent(hook_ready)` -> `CreateRemoteThread(LoadLibraryA,
  <dllpath>)` -> wait for `LoadLibraryA` -> wait for `hook_ready` event ->
  `ResumeThread`. relay_shell.dll uses **minhook** for its own `lua_newstate`
  hook. relay is a standalone runtime: the contract is "give it a prepared
  `--mod-path` directory + a `--game-binary`, it launches." Anyone can drive it.

## Chosen integration shape: Curator orchestrates USVFS, relay is unaware

Of the three possible shapes, this spike targets **Option 1**:

1. **Curator is the USVFS controller.** It calls `usvfsCreateVFS`, then
   `usvfsVirtualLinkDirectoryStatic` per enabled mod, mapping each repo
   `<versionFolder>/<baseName>/` onto a virtual destination under a (real,
   possibly empty) mod-path directory.
2. **Curator spawns relay via `usvfsCreateProcessHooked`.** relay sees the
   virtual mod-path as a populated directory and behaves exactly as today.
3. **relay spawns Darktide** with its existing `CreateProcessA(SUSPENDED)` +
   inject + resume sequence. USVFS hooks `CreateProcess` in relay, so the hook
   propagates into Darktide; relay_shell.dll is still injected. Both live in
   the Darktide process.

### Why Option 1 (and not "relay hosts USVFS")

The operator's stated value: **relay stays dead simple and composable** (give
it a prepared mod directory + a game binary, usable standalone by anyone). That
value is the optimization target, not "co-locate injection ownership."

- Option 1 leaves **relay's contract unchanged.** relay never learns USVFS
  exists; `--mod-path` is still a real directory it enumerates. The standalone
  relay story is untouched (no Curator -> no USVFS -> relay runs as today).
  USVFS is purely a Curator-side staging concern, which is correct, because
  staging is purely a Curator-side concern.
- "relay hosts USVFS" (Option 2) is cleaner from an injection-ownership
  standpoint, but it changes the contract (relay would receive a list of mod
  folders and own the VFS setup), and even a backward-compatible `--vfs-config`
  arg complicates relay's standalone story. Rejected on the operator's value.

Option 1 does introduce one genuine behavioral change versus materialized
staging, surfaced explicitly below: the staging becomes **session-scoped**
rather than on-disk, which affects whether Curator can exit while the game
runs. That is one of the spike's questions.

## The three questions this spike answers

All three are answerable with **dummy processes** on the operator's Windows
machine; Darktide is only needed for the final end-to-end confirmation.

1. **Enumeration visibility.** Does `usvfsVirtualLinkDirectoryStatic` make a
   virtual directory enumerate correctly to a process that reads it with
   standard Win32 directory APIs (the same family relay/DMF use to read
   `--mod-path`)? *If no, USVFS is out immediately.*

2. **Propagation through relay's suspend-create sequence.** Curator spawns the
   relay-stand-in via `usvfsCreateProcessHooked` (relay-stand-in is hooked).
   The relay-stand-in then spawns the "dummy game" via
   `CreateProcessA(CREATE_SUSPENDED)` + resume (mirroring `launcher.c`'s
   creation mode). Does the dummy game inherit the USVFS hooks, i.e. can it
   enumerate the virtual mod-path? *This is the core integration risk: does
   USVFS's child-process propagation survive the specific way relay creates the
   game?*

3. **Session lifetime without the controller.** After propagation, if the
   controller (Curator-stand-in) disconnects from / exits the VFS, do the
   child/grandchild processes keep the virtual view for the remainder of their
   lifetime? *Determines whether "close Curator after launch" (the current
   fire-and-forget behavior) still works under USVFS, or whether Curator must
   stay alive to hold the session open.*

## Harness design

Location: `spike/usvfs/` on this branch (kept out of the solution; not built by
`dotnet build src/modificus-curator.sln`). Components:

- `UsvfsHooks.cs` — P/Invoke wrappers for the USVFS C API (`usvfsCreateVFS`,
  `usvfsConnectVFS`, `usvfsDisconnectVFS`, `usvfsClearVirtualMappings`,
  `usvfsVirtualLinkDirectoryStatic`, `usvfsCreateProcessHooked`,
  `usvfsGetLogMessages`, `usvfsVersionString`). The `usvfsParameters` struct
  must match `include/usvfs/usvfsparameters.h` exactly (field order, string
  marshaling) — confirmed against the header before running.
- `TargetApp` — a tiny console "dummy game" that enumerates a path it is given
  and prints the entries, then either exits or stays alive (for the lifetime
  scenario).
- `FakeRelayInjector` — a faithful C# port of `launcher.c`'s process-*creation*
  sequence (`CreateProcessA(CREATE_SUSPENDED)` -> ... -> `ResumeThread`).
- `Program.cs` — a `--scenario` runner: `enum` (Q1), `propagate` (Q2),
  `lifetime` (Q3).
- `fetch-usvfs.ps1` — downloads the USVFS release binaries into `bin/`
  (gitignored; large native binaries are not committed). The harness resolves
  `usvfs*.dll` from there.

### A documented simplification

The relay-stand-in omits `CreateRemoteThread(LoadLibraryA, <relay_shell>)`.
That step is **orthogonal to USVFS propagation**: USVFS propagates by hooking
`CreateProcess` in the child, not by reacting to `CreateRemoteThread`.
Including it would require a no-op native DLL and would test nothing about
USVFS. The propagation question is purely "does USVFS reach a grandchild
created with `CREATE_SUSPENDED` by a hooked child?" The simplification is valid
and is called out here so it is not mistaken for an unfaithful test.

## Per-scenario acceptance gates

| Scenario | Pass | Fail implication |
| --- | --- | --- |
| `enum` | Dummy process spawned via `usvfsCreateProcessHooked` enumerates the virtually-linked directory and prints the source's contents, with no physical copy at the destination. | USVFS does not surface virtual dirs to Win32 enumeration -> USVFS is out. |
| `propagate` | Dummy game created by the relay-stand-in (hooked) via `CreateProcessA(SUSPENDED)`+resume enumerates the virtual dir. | Propagation does not survive relay's creation mode -> Option 1 needs a different spawn arrangement or USVFS is out for this architecture. |
| `lifetime` | After the controller disconnects, the dummy game still enumerates the virtual dir. | Curator must stay alive for the game session -> a real behavior change from fire-and-forget; weigh against the materialized-staging alternatives. |

## Operator's end-to-end test (the real confirmation)

The dummy-process scenarios answer the mechanism questions. The final
confirmation requires the operator's Darktide install and is **not** part of
this autonomous spike:

1. Build the harness (`dotnet build` on the spike project).
2. Run `fetch-usvfs.ps1` to vendor the USVFS binaries.
3. Replace the dummy game with a real Curator + relay launch: Curator creates
   the VFS, virtually links one real mod (e.g. DMF) onto an empty mod-path,
   spawns `modificus_relay.exe` via `usvfsCreateProcessHooked`, relay launches
   Darktide.
4. Observe: does Darktide reach the main menu with DMF loaded (relay.log shows
   the hook fire; the mod is active)? Does it stay stable?

This end-to-end test is the gate that turns "mechanism works on dummies" into
"USVFS ships."

## Out of scope for this spike

- The Linux/Proton path (USVFS is Windows-only; under Proton the game is a
  Windows process tree so USVFS *plausibly* works, but it is unverified and
  deferred).
- The AV false-positive surface (USVFS's API detouring is a known AV
  false-positive magnet; it interacts with the existing
  `curator-post-release-av` workflow). Noted as a future consideration, not
  tested here.
- Any Curator or relay production-code changes. This branch touches only
  `docs/design/` and `spike/usvfs/`.

## Version grounding

- USVFS: v0.5.7.2 (latest release, June 2025). API surface from
  `include/usvfs/usvfs.h`; `usvfsParameters` struct from
  `include/usvfs/usvfsparameters.h` (read before P/Invoking).
- .NET 10 P/Invoke to an `extern "C"` stdcall (`WINAPI`) DLL: standard
  `[DllImport(..., CallingConvention = CallingConvention.StdCall,
  CharSet = CharSet.Unicode)]`. Wide strings for `LPCWSTR` params.
