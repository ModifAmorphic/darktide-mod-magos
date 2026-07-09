# 0001 — Junction-based mod staging on Windows (privilege-free fallback)

> Status: **documented alternative, not yet chosen.** Carried alongside the
> USVFS investigation (`0002-usvfs-spike.md`) as the ready fallback if USVFS
> proves unworkable, and as the minimal "unblock the Windows release now"
> option regardless. This is a design note for a future coder, not a decision
> record.

## The problem

`ProfileService.PrepareModRoot` stages each enabled mod by creating a
**directory symbolic link**: `staged/<baseName>` ->
`<versionFolder>/<baseName>/`, via the BCL primitive
`Directory.CreateSymbolicLink` (registered as the `SymlinkCreator` delegate).

On Windows, creating a symbolic link requires `SeCreateSymbolicLinkPrivilege`,
which means **Developer Mode or administrator**. For a public Windows release
this is a showstopper. The current code bakes the workaround into the
user-facing error string (`SymlinkStagingException` -> *"On Windows, enable
Developer Mode or run the manager as administrator..."*), which hides the
design fault instead of fixing it.

The symlink choice was originally made on the (false) premise that symlinks
work on Windows without privilege. They do not.

## The fix: NTFS junctions on Windows, symlinks on Linux

Windows has a second reparse-point type, the **junction** (`mklink /J`), that
does not require any privilege: no Developer Mode, no admin. Junctions are
directory-only and NTFS-only, and were designed for exactly this kind of
local-volume directory indirection.

This is a platform-selective swap of the staging primitive, not a new model:

- **Windows:** create a *junction* at `staged/<baseName>` pointing at the
  repo's `<versionFolder>/<baseName>/`. No privilege.
- **Linux:** keep using `Directory.CreateSymbolicLink` (symlinks need no
  privilege on Linux).

### Why it fits this architecture specifically

1. **The mod model is directory-level.** Each mod is one self-contained base
   folder; `GetBaseNameCollision` blocks two mods from sharing a folder name;
   the loader enumerates folders. A junction resolves to a real directory at
   the Win32 layer, so relay and DMF see a normal mod folder. No relay-contract
   change.
2. **The data-safety cleanup logic is already correct for junctions.**
   `DeleteStagedEntry` (`ProfileService.cs`) keys off
   `FileAttributes.ReparsePoint | Directory` and calls `Directory.Delete(entry)`,
   which removes a junction without traversing into the repository target.
   Junctions set exactly those attributes, so cleanup keeps its "never follow a
   link into the repo" guarantee **unmodified**.
3. **Cross-local-volume works.** If a user keeps the mod repo on `D:` and
   profiles on `C:`, the junction still resolves. Only network paths break,
   which are out of scope (the repo and profiles are local).

### Caveats

- **NTFS-only.** A user running Curator off a FAT32/exFAT volume would fail.
  Darktide installs and local-app-data are NTFS in practice, so this is a
  non-issue in realistic deployments, but it is the one real limitation versus
  true symlinks.
- **Whole-directory only.** Junctions cannot express file-level overlay. This
  architecture does not need file-level overlay (mods are discrete folders), so
  it is not a loss, but it forecloses that future capability (USVFS would keep
   it open).
- **Windows-only primitive.** Linux keeps symlinks; the seam selects per-OS.

## Change sites (exact)

| File | Change |
| --- | --- |
| `src/profiles/SymlinkCreator.cs` | Rename the delegate to `StagingLinkCreator` (it is no longer always a symlink). Keep the same signature `(linkPath, targetPath)`. Update the doc comment to describe the platform-selective impl. |
| `src/profiles/ServiceCollectionExtensions.cs` (line ~35, ~45-46) | Replace the single `CreateSymbolicLink` registration with a platform-aware one: `OperatingSystem.IsWindows()` -> junction impl; else -> symlink impl. The junction impl creates a junction (see "Creating a junction" below). |
| `src/profiles/ProfileService.cs` (`CreateSymlinkOrThrow`, line ~485-499) | Rename `_symlink` -> `_createLink`. Rewrite the error message: it is no longer "enable Developer Mode"; it is "failed to create the staging link" with the OS-appropriate hint. |
| `src/profiles/SymlinkStagingException.cs` | Rename to `StagingLinkException`. Update the doc comment (no longer symlink-specific; no longer mentions Developer Mode). |
| `src/profiles/ProfileService.cs` (`ClearStagedDir`, `DeleteStagedEntry`) | **No change.** Already reparse-point-aware; junctions are `ReparsePoint \| Directory` and are removed correctly. |
| `src/profiles/IProfileService.cs` (doc comments) | Update "symlink" prose to "staging link" where it describes staging. No signature change. |
| Tests under `src/tests/Modificus.Curator.Profiles.Tests/` | The fixture injects a `SymlinkCreator` override for the failure path; rename to `StagingLinkCreator`. Existing symlink-path tests stay valid on Linux; add a Windows-junction test that asserts the staged entry is a reparse point (junction) and that the target is not copied. |

## Creating a junction

The .NET 10 BCL has **no first-class junction API** (the coder must confirm this
against the installed SDK; `Directory.CreateSymbolicLink` exists in
`System.IO` but there is no `Directory.CreateJunction`). Two implementation
options:

1. **P/Invoke `DeviceIoControl` with `FSCTL_SET_REPARSE_POINT`** (the raw,
   dependency-free way). Write a `REPARSE_DATA_BUFFER` with
   `IO_REPARSE_TAG_MOUNT_POINT` and the absolute target path (wide, prefixed
   with `\??\`). Standard, well-documented pattern. ~80 lines including the
   structs.
2. **Shell out to `cmd /c mklink /J <link> <target>`**. Simpler, but spawns a
   process per link and depends on `cmd.exe` being present. Acceptable but
   less clean than the P/Invoke.

Recommendation: the P/Invoke. It is deterministic, testable, and matches the
project's preference for dependency-free primitives.

> Version-grounding note for the coder: confirm `OperatingSystem.IsWindows()`
> (System.OperatingSystem, available since .NET 5) is the right gate, and that
> the junction reparse-tag constant (`0xA0000003`,
> `IO_REPARSE_TAG_MOUNT_POINT`) and `FSCTL_SET_REPARSE_POINT`
> (`0x000900A4`) values are sourced from the Windows SDK headers
> (`winnt.h` / `ntifs.h`), not memory.

## Naming generalization (plumbing decision, flagged not asked)

The link is no longer always a symlink, so the type names should stop claiming
it is. `SymlinkCreator` -> `StagingLinkCreator`; `SymlinkStagingException` ->
`StagingLinkException`. This is internal plumbing (identical behavior, no UX
consequence), so per the dev-lead convention it is decided here and flagged,
not surfaced as a question.

## Acceptance criteria (qa-able)

1. On Windows, **without** Developer Mode and **without** admin, staging a
   profile with at least one enabled mod succeeds and the staged entry is a
   reparse point (junction), not a copy (the target's files are not duplicated
   under `staged/`).
2. Staging is idempotent: a second `PrepareModRoot` clears the prior projection
   and rebuilds without error and without deleting repository files.
3. `GetBaseNameCollision` still behaves identically (it derives the base name;
   the link kind is irrelevant to it).
4. On Linux, staging still produces a symlink (unchanged behavior).
5. A re-add/remove cycle on a profile leaves no stale links and the repository
   target intact (the existing data-safety tests, plus a Windows-junction
   variant).
6. The `SymlinkStagingException`/Developer-Mode error string is gone.

## Effort

Small. The seam already exists (`SymlinkCreator` is a delegate, DI-swappable,
and the tests already substitute it). The work is: the junction P/Invoke impl,
the platform-aware DI registration, the rename, the error-message rewrite, and
a Windows-junction test. Hours, not days. It does not touch relay and does not
change the relay contract.
