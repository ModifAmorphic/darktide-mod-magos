# Profiles (`Magos.Modificus.Profiles`) — reference

> Profile + per-profile mod-list management: the profile data model, its on-disk
> persistence, and the projection of the mod list into a staged mod root
> (symlinks to the repository's resolved version folders) + `mods.lst` for the
> Enginseer runtime. Status: implemented (Phase 1 of the shared-mod-storage
> refactor).

A profile owns its own mod list, mod settings, and load order. The profile's
staged mod root is what Magos passes to the Enginseer launcher as `--mod-path`;
Magos writes `mods.lst` into it on each launch. A profile references mods by
their repository container id and stores no mod files of its own.

## Public surface

### `IProfileService`

Profile lifecycle + per-profile mod-list management. All storage details (paths,
version-folder resolution) stay behind the interface.

```csharp
public interface IProfileService
{
    IReadOnlyList<ProfileSummary> ListProfiles();
    Profile GetProfile(Guid id);
    Profile CreateProfile(string name);
    void RenameProfile(Guid id, string newName);
    void DeleteProfile(Guid id);

    IReadOnlyList<ModListEntry> GetModList(Guid id);
    void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder);
    void SetModEnabled(Guid id, Guid containerId, bool enabled);
    void AddMod(Guid id, Guid containerId, ModVersionPolicy policy);
    void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy);
    void RemoveMod(Guid id, Guid containerId);

    string PrepareModRoot(Guid id);
}
```

Method behavior:

- `ListProfiles()` — every profile under `ProfilesBaseFolder`, as lightweight
  summaries, sorted by `Name` (ordinal). Non-`Guid` directories and unreadable
  profiles are skipped with a debug/warning log; one bad profile never breaks
  listing.
- `GetProfile(id)` — loads the full profile (metadata + mod list). Throws
  `KeyNotFoundException` if the profile dir or `profile.json` is absent. Legacy
  mod entries lacking `ContainerId` (the Phase 2 shape) are dropped on read +
  logged (fresh-start; the operator re-adds mods).
- `CreateProfile(name)` — generates the `Guid`, scaffolds the directory tree
  (`staged/`) **before** persisting an empty `profile.json` (so a crash between
  the two never leaves a `profile.json` without its tree), and returns the new
  profile. `name` must be non-whitespace. There is no per-profile `mods/`
  directory (mods live in the repository).
- `RenameProfile(id, newName)` — display label only; the id and on-disk dir are
  unchanged.
- `DeleteProfile(id)` — removes the entry and its entire directory tree
  (recursive). Throws `KeyNotFoundException` if absent.
- `GetModList(id)` — the profile's mods in stored order, not load order.
- `SetModOrder(id, containerIdsInOrder)` — reassigns each entry's `Order` so the
  listed containers come first; unmentioned mods keep their relative order
  appended after; unknown ids are ignored. No mods are added or removed.
- `SetModEnabled(id, containerId, enabled)` — toggles a single mod. Throws
  `KeyNotFoundException` if the profile or the container is not in its list.
- `AddMod(id, containerId, policy)` — appends a mod entry (`Enabled = true`) at
  the end. **List entry only: does NOT fetch or install mod files** (the
  repository holds the files; staging symlinks to them). Idempotent: re-adding a
  `containerId` already in the list is a no-op (order/enabled/policy untouched).
- `SetModPolicy(id, containerId, policy)` — records the new policy. Resolution
  happens at stage time, so there is no on-disk transition (no diverged copy to
  reconcile; the old model's share/diverge branch is gone). A `PinnedPolicy` is
  validated: its `VersionId` must reference a version present on the container,
  else `ArgumentException` (the UI dropdown can't produce a bad id; this guards
  programmatic / stale-id calls). `LatestPolicy` needs no check.
- `RemoveMod(id, containerId)` — drops the entry. The repository copy is **not**
  touched (other profiles may still reference it; the startup prune reclaims it
  when no profile does).
- `PrepareModRoot(id)` — regenerates the staged mod root (the `--mod-path`) from
  the current per-mod version resolution and writes `mods.lst`. Idempotent
  (clears + rebuilds `staged/` each call). Returns the `--mod-path` to pass to
  the Enginseer launcher. Throws `SymlinkStagingException` if a symlink cannot be
  created (the manager never silently copies).

### Key types

- `ModListEntry` — a single mod within a profile's list (immutable record):
  `ContainerId` (Guid; the join key against `IModRepository`), `Enabled`
  (disabled mods are omitted from `mods.lst`: enable-by-omission), `Order`
  (`int`, lower loads first), `Policy` (default `ModVersionPolicy.Latest`;
  drives version resolution). Mutations go through `IProfileService`, which
  rebuilds the changed entry via `with` expressions and persists.
- `Profile` — the aggregate root persisted to
  `<ProfilesBaseFolder>/<Id>/profile.json`. Identity is `Id` (a `Guid`, stable
  across renames and the on-disk directory name); `Name` is a display label, not
  unique, not a path. `CreatedAt` is UTC. `Mods` is exposed as an immutable
  `IReadOnlyList<ModListEntry>`.
- `ProfileSummary(Guid Id, string Name)` — a lightweight projection for profile
  pickers (no mod list loaded).
- `SymlinkCreator` — a `delegate` that creates a directory symlink
  (`Directory.CreateSymbolicLink` by default). Injectable so tests exercise the
  failure path without platform permission hacks.
- `SymlinkStagingException` — an `InvalidOperationException` thrown by
  `PrepareModRoot` when a staged symlink cannot be created (typically Windows
  without symlink permissions / Developer Mode). The staging layer never
  silently copies.

`ModVersionPolicy` (PinnedPolicy/LatestPolicy), `ModSource`, `ModContainer`, and
`ModVersion` live in the [mods](mods.md) library; Profiles consumes
them.

### `IModOrderResolver` + `IdentityModOrderResolver`

The auto-sort seam. The mod-list UI's auto-sort toggle resolves an order via
this interface, then applies it through `IProfileService.SetModOrder`.

```csharp
public interface IModOrderResolver
{
    IReadOnlyList<Guid> ResolveOrder(IReadOnlyList<ModListEntry> mods);
}

public sealed class IdentityModOrderResolver : IModOrderResolver;   // identity stub
```

The current implementation is the **identity stub** (`IdentityModOrderResolver`):
it returns container ids in their current `ModListEntry.Order` (a no-op). The
real dependency-driven auto-sort algorithm lands in a later phase; this
interface is the DI-swappable seam so the UI wires against the abstraction now.
Pure + deterministic (stable on ties).

### `ModCleanup` (static)

Startup prune orchestration. Collects every `(containerId, versionFolder)`
referenced by any profile (resolving each entry's policy against its container
via `ModContainer.ResolveVersion`), then calls
`IModRepository.PruneUnreferenced`. The composition root invokes it once after
building the service provider; a failure is logged + swallowed so cleanup never
blocks startup.

```csharp
public static class ModCleanup
{
    public static void PruneUnreferenced(IProfileService profiles, IModRepository repo);
}
```

## DI registration

```csharp
public static IServiceCollection AddProfiles(this IServiceCollection services);
```

`AddProfiles()` registers:

- `AddMods()` — called defensively (idempotent) so a lone `AddProfiles()`
  yields a resolvable `IProfileService`; the composition root also calls it.
- `TryAddSingleton<SymlinkCreator>(_ => Directory.CreateSymbolicLink)` — the BCL
  default. `TryAdd` so a test may pre-register a throwing/fake delegate.
- `TryAddSingleton<IModOrderResolver, IdentityModOrderResolver>()`: the auto-
  sort identity stub. `TryAdd` so a test (or the real dependency-driven resolver,
  when it lands) may pre-register an override.
- `AddSingleton<IProfileService, ProfileService>()` — the filesystem-backed
  implementation (internal). Resolves `MagosConfig`, `IModRepository`,
  `SymlinkCreator`, and `ILogger<ProfileService>` from the container.

Registered as a singleton: it holds no per-request state, and `MagosConfig` (its
only config source) is itself a singleton.

## On-disk layout

```
<ProfilesBaseFolder>/              (auto-created on first run)
  <guid>/                          (profile dir; id-named)
    profile.json                   (metadata + mod list - the source of truth)
    staged/                        (the staged mod root = the --mod-path;
                                     REGENERATED each launch - a projection)
      <displayName>                (symlink -> repository version folder)
      mods.lst                     (successfully-staged enabled mods, in order)
```

`profile.json` and `mods.lst` are UTF-8 without BOM. There is no per-profile
`mods/` directory (mods live in the repository).

### Staging (`PrepareModRoot`)

Each enabled mod resolves its `ModVersionPolicy` against its container:

- **LatestPolicy** → symlink `staged/<displayName>` → the container's `IsLatest`
  version folder.
- **PinnedPolicy(vId)** → symlink `staged/<displayName>` → the version whose
  `Folder == vId` (resolution by opaque version id, not by tag).
- **No match** (container missing, no versions, no `IsLatest`, or the pinned
  version id is absent) → skipped with a warning (no `staged/` entry, no
  `mods.lst` entry).

**Symlinks, never copies** — the repository holds the files; `staged/` is a
symlink projection. `mods.lst` lists exactly what got staged, in `Order`: no
DMF-first enforcement, no auto-sort (those are higher-layer concerns).

The symlink `<displayName>` is the sanitized container `Name`
(filesystem-illegal chars replaced with `_`, fall back to the container id if
empty). Intra-profile name collisions (two containers with the same display
name) are disambiguated by appending a short id-derived suffix; the loader is
agnostic to the symlink name.

### Moving `IsLatest` requires zero profile-entry changes

Because a profile references `(containerId, policy)` and resolves at stage time,
flipping which version is `IsLatest` in the repository is a one-field manifest
edit. Every profile with `LatestPolicy` on that container picks up the new
version on the next `PrepareModRoot`; no profile entry changes.

### Data safety — `ClearStagedDir`

`staged/` is cleared before each rebuild. The clear is **symlink-aware**: it
removes each top-level entry as a link (never following it into the repository).
A naive `Directory.Delete(staged, recursive: true)` could follow a directory
symlink and delete repository mod files. The delete API is chosen to match the
link's kind — directory symlinks use `Directory.Delete` (the link only), file
symlinks use `File.Delete` — so it stays data-safe on both OSes.

## Dependencies

- **Magos libraries:** `config` (`MagosConfig.ProfilesBaseFolder`), `mods`
  (`IModRepository`, `ModContainer` / `ModVersion`, `ModVersionPolicy`). The
  dependency direction is clean: Profiles depends on Mods (the repository
  knows nothing of profiles).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Magos.Modificus.Profiles.Tests` covers profile CRUD (`ProfileCrudTests`), mod
list ordering/enable/policy (`ModListTests`, including the legacy-Name-entry
drop + null-Policy coercion), `PrepareModRoot` + symlink staging + the data-safe
`ClearStagedDir` (`PrepareModRootTests`, `StagingTests`), and the `AddProfiles`
DI wiring (including the `TryAdd` `SymlinkCreator` override).

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) — the
  [Profiles](../../architecture/MAGOS-MODIFICUS.md#profiles) +
  [Mod repository](../../architecture/MAGOS-MODIFICUS.md#mod-repository) sections.
- [mods](mods.md) — the unified mod repository + version-policy model.
- [enginseer-client](enginseer-client.md) — the launch façade that consumes
  `PrepareModRoot`.
