# SharedMods (`Magos.Modificus.SharedMods`) — reference

> The global shared mod store + the version-policy model that drives
> shared-vs-diverged allocation across profiles. Status: implemented (Phase 2).

Mods are stored **shared-first**: a profile uses the global shared copy when its
version policy is compatible with the shared entry's, and takes a profile-local
(diverged) copy only when the policies diverge. This library owns the manifest
of shared mods and the pure allocation rule; the [Profiles](profiles.md) library
applies it during staging.

## Public surface

### `ISharedModStore`

The global shared mod store — the manifest of mods that live shared-first across
profiles. Owns `<SharedModsFolder>/shared-manifest.json`. The manifest is the
index; the mod files live at each entry's `Path` (placed there by Phase 4
acquisition).

```csharp
public interface ISharedModStore
{
    IReadOnlyList<SharedModEntry> List();
    SharedModEntry? Get(string name);   // ordinal; null if absent
    void Add(SharedModEntry entry);     // upsert
    void Remove(string name);           // idempotent
}
```

- `List()` — all entries in stored order.
- `Get(name)` — lookup by mod name (ordinal). Null if absent.
- `Add(entry)` — upsert: replaces an existing same-named entry, else appends.
  **Assumes the mod files are already at `entry.Path`** — Phase 2 manages the
  manifest, not downloads (acquisition is Phase 4). `entry.Name` must be
  non-whitespace.
- `Remove(name)` — drops the manifest entry; idempotent (a missing name is a
  no-op that does not even write). The mod files are **not** touched (they're
  the acquisition's responsibility; other profiles may still share them).

The store is read-through: each operation reads the manifest fresh from disk
(small file; avoids stale-state across instances), and mutations write it back
in full. A corrupt/unreadable manifest is treated as empty and logged loudly
(staging degrades gracefully — no mods share — rather than crashing).

### Key types

#### `SharedModEntry`

A single mod in the global shared store (immutable record):

| Field | Meaning |
| --- | --- |
| `Name` | The mod folder name — the value written to `mods.lst`. |
| `Policy` | The shared entry's version policy (drives allocation). Default `Latest`. |
| `ActualVersion` | The actual on-disk version of the shared copy (`System.Version`; default `0.0`). Used for display; **not** the share decision (resolution is by intent, not version). |
| `Path` | Where the shared mod files live: `<SharedModsFolder>/<Name>`. The symlink target when staging resolves this entry to Share. |

#### `ModVersionPolicy` (abstract record)

A mod's version policy — the type-safe one-of that drives allocation. Two cases:

- `PinnedPolicy(Version Version)` — frozen at a specific release. Two pins share
  only when their versions match.
- `LatestPolicy` — tracks the newest release (auto-update). Two Latests share
  (both move together).

Persisted polymorphically to `shared-manifest.json` and `profile.json` via a
`$kind` discriminator with **stable identifiers** (`pinned` / `latest`),
independent of assembly names:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(PinnedPolicy), "pinned")]
[JsonDerivedType(typeof(LatestPolicy),  "latest")]
public abstract record ModVersionPolicy
{
    public static ModVersionPolicy Latest { get; } = new LatestPolicy();
}
```

A null/absent policy defaults to `Latest` (the Phase 1 baseline) — handled by
caller coercion (e.g. `ProfileService` on read), not by this type.

#### `AllocationResolver` (static) + `AllocationResolution`

Pure allocation logic — resolves a profile mod's policy against the shared
entry's policy. No I/O, no logging, no DI.

```csharp
public static class AllocationResolver
{
    public static AllocationResolution Resolve(
        ModVersionPolicy sharedPolicy,
        Version sharedActualVersion,    // unused for the decision (kept for clarity/future)
        ModVersionPolicy profilePolicy);
}

public enum AllocationResolution { Share, Diverge }
```

The four cases, by policy **intent** (not current version):

| Shared | Profile | Resolution |
| --- | --- | --- |
| `Pinned(v1.0.1)` | `Pinned(v1.0.1)` | **Share** — same pin |
| `Pinned(v1.0.1)` | `Pinned(v2.0.1)` | **Diverge** — different pins |
| `Latest` | `Latest` | **Share** — both track latest |
| `Latest` | `Pinned(v2.0.1)` | **Diverge** — shared will move, profile won't |

The resolution is by intent because a shared `Latest` and a profile `Pinned` to
today's same version still **Diverge** — the shared one will move on the next
release while the profile won't. `sharedActualVersion` is intentionally unused:
a matching version alone is not enough; both sides must agree on intent.

> **Phase 4 caveat (dormant in Phase 2):** the `PinnedPolicy` share check
> compares `sp.Version == pp.Version`. `System.Version` equality is
> **component-count-sensitive** — `new Version(1,0) != new Version(1,0,0)`
> (missing components default to `-1`, not `0`). Phase 2 is internally
> consistent (its tests use matching component counts), so this is dormant.
> Phase 4 acquisition will parse user-typed pins and GitHub-release-tag versions
> against shared-store entries; cross-component-count comparisons would silently
> Diverge. Phase 4 must normalize version representation at the acquisition
> boundary (canonical `Major.Minor.Build`, or a component-normalized compare) so
> `1.0` and `1.0.0` compare equal. No Phase 2 logic change.

## DI registration

```csharp
public static IServiceCollection AddSharedMods(this IServiceCollection services)
    => services.TryAddSingleton<ISharedModStore, SharedModStore>();
```

Uses `TryAddSingleton` (mirroring the `SymlinkCreator` seam in Profiles):
production behavior is unchanged (TryAdd registers on first call when nothing's
pre-registered), but a caller may pre-register an `ISharedModStore` mock and
have it survive `AddProfiles()` (which calls `AddSharedMods()` unconditionally —
a plain `AddSingleton` would clobber a pre-registered mock, since MS DI resolves
the last descriptor). Resolves `MagosConfig` + `ILogger<SharedModStore>` from the
container. Registered as a singleton — no per-request state; all state lives on
disk.

## Dependencies

- **Magos libraries:** `config` (`MagosConfig.SharedModsFolder`).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Magos.Modificus.SharedMods.Tests` covers the four-case `AllocationResolver`
(including the intent-vs-version point) and the `SharedModStore` manifest
persistence (upsert, idempotent remove, first-run-safe + corrupt-manifest
degradation). The internal `SharedModStore` is visible to tests via
`InternalsVisibleTo`.

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) — the
  [Shared mod storage](../../architecture/MAGOS-MODIFICUS.md#shared-mod-storage)
  section.
- [profiles](profiles.md) — applies the allocation rule during staging.
