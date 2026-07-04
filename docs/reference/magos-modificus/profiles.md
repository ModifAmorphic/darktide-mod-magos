# Profiles (`Magos.Modificus.Profiles`) — reference

> Profile + per-profile mod-list management: the profile data model, its on-disk
> persistence, and the projection of the mod list into a staged mod root
> (symlinks) + `mods.lst` for the Enginseer runtime. Status: implemented
> (Phase 1, restaged shared-first in Phase 2).

A profile owns its own mods, mod settings, and load order. The profile's staged
mod root is what Magos passes to the Enginseer launcher as `--mod-path`; Magos
writes `mods.lst` into it on each launch.

## Public surface

### `IProfileService`

Profile lifecycle + per-profile mod-list management. All storage details
(paths, shared-vs-diverged allocation) stay behind the interface.

```csharp
public interface IProfileService
{
    IReadOnlyList<ProfileSummary> ListProfiles();
    Profile GetProfile(Guid id);
    Profile CreateProfile(string name);
    void RenameProfile(Guid id, string newName);
    void DeleteProfile(Guid id);

    IReadOnlyList<ModListEntry> GetModList(Guid id);
    void SetModOrder(Guid id, IReadOnlyList<string> modNamesInOrder);
    void SetModEnabled(Guid id, string modName, bool enabled);
    void AddMod(Guid id, string modName);
    void AddMod(Guid id, string modName, ModVersionPolicy policy);
    void SetModPolicy(Guid id, string modName, ModVersionPolicy policy);
    void RemoveMod(Guid id, string modName);

    string PrepareModRoot(Guid id);
}
```

Method behavior:

- `ListProfiles()` — every profile under `ProfilesBaseFolder`, as lightweight
  summaries, sorted by `Name` (ordinal). Non-`Guid` directories and unreadable
  profiles are skipped with a debug/warning log; one bad profile never breaks
  listing.
- `GetProfile(id)` — loads the full profile (metadata + mod list). Throws
  `KeyNotFoundException` if the profile dir or `profile.json` is absent.
- `CreateProfile(name)` — generates the `Guid`, scaffolds the directory tree
  (`staged/` + `mods/`) **before** persisting an empty `profile.json`
  (so a crash between the two never leaves a `profile.json` without its tree),
  and returns the new profile. `name` must be non-whitespace.
- `RenameProfile(id, newName)` — display label only; the id and on-disk dir are
  unchanged.
- `DeleteProfile(id)` — removes the entry and its entire directory tree
  (recursive). Throws `KeyNotFoundException` if absent.
- `GetModList(id)` — the profile's mods in stored order, not load order.
- `SetModOrder(id, modNamesInOrder)` — reassigns each entry's `Order` so the
  listed names come first; unmentioned mods keep their relative order appended
  after; unknown names are ignored. No mods are added or removed.
- `SetModEnabled(id, modName, enabled)` — toggles a single mod. Throws
  `KeyNotFoundException` if the profile or the mod is unknown.
- `AddMod(id, modName)` / `AddMod(id, modName, policy)` — appends a mod entry
  (`Enabled = true`) at the end, default policy `Latest`. **List entry only —
  does NOT fetch or install mod files.** Idempotent: re-adding an existing name
  is a no-op (order/enabled/policy untouched). The no-policy overload exists
  because `ModVersionPolicy` is a reference type and cannot be a `const` default
  parameter.
- `SetModPolicy(id, modName, policy)` — records the new policy, then reconciles
  the diverged copy against the shared store (see [Divergence transitions](#divergence-transitions)).
- `RemoveMod(id, modName)` — drops the entry and the mod's profile-local
  (`mods/`) files, if any. A missing local copy is graceful. The shared-store
  copy is **not** touched (other profiles may still share it).
- `PrepareModRoot(id)` — regenerates the staged mod root (the `--mod-path`) from
  the current shared-first resolution and writes `mods.lst`. Idempotent (clears +
  rebuilds `staged/` each call). Returns the `--mod-path` to pass to the
  Enginseer launcher. Throws `SymlinkStagingException` if a symlink cannot be
  created (the manager never silently copies).

### Key types

- `ModListEntry` — a single mod within a profile's list (immutable record):
  `Name` (the mod folder name written to `mods.lst`), `Enabled` (disabled mods
  are omitted from `mods.lst` — enable-by-omission), `Order` (`int`, lower loads
  first), `Policy` (default `ModVersionPolicy.Latest`; drives shared-vs-diverged
  allocation). Mutations go through `IProfileService`, which rebuilds the changed
  entry via `with` expressions and persists.
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

`ModVersionPolicy` (PinnedPolicy/LatestPolicy), `ModSource`, and
`AllocationResolver` live in the [shared-mods](shared-mods.md) library; Profiles
consumes them.

### `IModOrderResolver` + `IdentityModOrderResolver`

The auto-sort seam. The mod-list UI's auto-sort toggle resolves an order via
this interface, then applies it through `IProfileService.SetModOrder`.

```csharp
public interface IModOrderResolver
{
    IReadOnlyList<string> ResolveOrder(IReadOnlyList<ModListEntry> mods);
}

public sealed class IdentityModOrderResolver : IModOrderResolver;   // identity stub
```

The current implementation is the **identity stub** (`IdentityModOrderResolver`):
it returns names in their current `ModListEntry.Order` (a no-op). The real
dependency-driven auto-sort algorithm lands in a later phase; this interface is
the DI-swappable seam so the UI wires against the abstraction now and the real
resolver drops in later without a UI change. Pure + deterministic (stable on
ties).

## DI registration

```csharp
public static IServiceCollection AddProfiles(this IServiceCollection services);
```

`AddProfiles()` registers:

- `AddSharedMods()` — called defensively (idempotent) so a lone `AddProfiles()`
  yields a resolvable `IProfileService`; the composition root also calls it.
- `TryAddSingleton<SymlinkCreator>(_ => Directory.CreateSymbolicLink)` — the BCL
  default. `TryAdd` so a test may pre-register a throwing/fake delegate.
- `TryAddSingleton<IModOrderResolver, IdentityModOrderResolver>()`: the auto-
  sort identity stub. `TryAdd` so a test (or the real dependency-driven resolver,
  when it lands) may pre-register an override.
- `AddSingleton<IProfileService, ProfileService>()` — the filesystem-backed
  implementation (internal). Resolves `MagosConfig`, `ISharedModStore`,
  `SymlinkCreator`, and `ILogger<ProfileService>` from the container.

Registered as a singleton: it holds no per-request state — all state lives on
disk, and `MagosConfig` (its only config source) is itself a singleton.

## On-disk layout

```
<ProfilesBaseFolder>/              (auto-created on first run)
  <guid>/                          (profile dir; id-named)
    profile.json                   (metadata + mod list — the source of truth)
    mods/<mod>/                    (a profile's diverged copy of a mod, if any)
    staged/                        (the staged mod root = the --mod-path;
                                     REGENERATED each launch — a projection)
      <mod>                        (symlink → shared <mod> OR mods/<mod>)
      mods.lst                     (successfully-staged enabled mods, in order)
```

`profile.json` and `mods.lst` are UTF-8 without BOM (a BOM would surface as a
stray prefix on the first mod name when the Lua loader reads line-by-line).

### Shared-first staging (`PrepareModRoot`)

Each enabled mod resolves Share/Diverge against the shared store via
`AllocationResolver`:

- **Share** → symlink `staged/<mod>` → the shared store entry's `Path`.
- **Diverge** (and a `mods/<mod>/` copy is present) → symlink
  `staged/<mod>` → `mods/<mod>/`.
- **Diverge** but `mods/<mod>/` absent (Phase 4 acquisition hasn't placed it)
  → skipped with a warning (no `staged/` entry, no `mods.lst` entry).

**Symlinks, never copies** — the shared store + `mods/` hold the files;
`staged/` is a symlink projection. `mods.lst` lists exactly what got staged, in
`Order` — no DMF-first enforcement, no auto-sort (those are higher-layer
concerns).

### Divergence transitions (`SetModPolicy`)

A policy change re-resolves the mod against the shared store:

- **Share → Diverge:** records the policy (the profile now needs a local copy).
  Acquiring the `mods/<mod>/` files is Phase 4 — staging looks for it and
  skips + warns until then.
- **Diverge → Share:** the local copy is no longer needed — drops
  `mods/<mod>/` to reclaim space (best-effort; a missing dir is a no-op;
  re-divergence re-acquires).

If the mod has no shared-store entry, only the policy is recorded (no transition
applies until acquisition populates the store).

### Data safety — `ClearStagedDir`

`staged/` is cleared before each rebuild. The clear is **symlink-aware**: it
removes each top-level entry as a link (never following it into the shared store
or a diverged copy). A naive `Directory.Delete(staged, recursive: true)` could
follow a directory symlink and delete shared mod files. The delete API is chosen
to match the link's kind — directory symlinks use `Directory.Delete` (the link
only), file symlinks use `File.Delete` — so it stays data-safe on both OSes.

## Dependencies

- **Magos libraries:** `config` (`MagosConfig.ProfilesBaseFolder`), `shared-mods`
  (`ISharedModStore`, `AllocationResolver`, `ModVersionPolicy`). The dependency
  direction is clean: Profiles depends on SharedMods (the shared store knows
  nothing of profiles).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Magos.Modificus.Profiles.Tests` covers profile CRUD (`ProfileCrudTests`), mod
list ordering/enable/policy (`ModListTests`), `PrepareModRoot` + shared-first
staging + the data-safe `ClearStagedDir` (`PrepareModRootTests`, `StagingTests`),
and the `AddProfiles` DI wiring (including the `TryAdd` `SymlinkCreator` override).

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) — the
  [Profiles](../../architecture/MAGOS-MODIFICUS.md#profiles) +
  [Shared mod storage](../../architecture/MAGOS-MODIFICUS.md#shared-mod-storage)
  sections.
- [shared-mods](shared-mods.md) — the version-policy model + allocation resolver.
- [enginseer-client](enginseer-client.md) — the launch façade that consumes
  `PrepareModRoot`.
