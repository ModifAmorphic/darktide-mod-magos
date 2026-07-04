# SharedMods (`Magos.Modificus.SharedMods`) reference

> The global shared mod store, the version-policy model that drives
> shared-vs-diverged allocation across profiles, the mod-source provenance model,
> and the local-import service. Status: implemented (Phase 2 + Phase 3 Track B
> backend).

Mods are stored **shared-first**: a profile uses the global shared copy when its
version policy is compatible with the shared entry's, and takes a profile-local
(diverged) copy only when the policies diverge. This library owns the manifest
of shared mods, the pure allocation rule, the source/version model, and the
import seam that places files into the store; the [Profiles](profiles.md)
library applies the allocation rule during staging.

## Public surface

### `ISharedModStore`

The global shared mod store: the manifest of mods that live shared-first across
profiles. Owns `<SharedModsFolder>/shared-manifest.json`. The manifest is the
index; the mod files live at each entry's `Path` (placed there by the import
service for local imports, or Phase 4 acquisition for remote).

```csharp
public interface ISharedModStore
{
    IReadOnlyList<SharedModEntry> List();
    SharedModEntry? Get(string name);   // ordinal; null if absent
    void Add(SharedModEntry entry);     // upsert
    void Remove(string name);           // idempotent
}
```

- `List()`: all entries in stored order.
- `Get(name)`: lookup by mod name (ordinal). Null if absent.
- `Add(entry)`: upsert, replaces an existing same-named entry, else appends.
  **Assumes the mod files are already at `entry.Path`**. Phase 2 manages the
  manifest, not downloads (acquisition is Phase 4; local import places its own
  files before calling this). `entry.Name` must be non-whitespace.
- `Remove(name)`: drops the manifest entry; idempotent (a missing name is a
  no-op that does not even write). The mod files are **not** touched (they're
  the acquisition's / importer's responsibility; other profiles may still share
  them).

The store is read-through: each operation reads the manifest fresh from disk
(small file; avoids stale-state across instances), and mutations write it back
in full. A corrupt/unreadable manifest is treated as empty and logged loudly
(staging degrades gracefully: no mods share, rather than crashing).

### `IModImportService`

Imports a local mod source (a folder OR a `.zip` archive) into the shared store.
The mod-list UI's add flow (picker + drag-and-drop) goes through this seam:
the UI never touches the filesystem directly.

```csharp
public interface IModImportService
{
    SharedModEntry Import(string sourcePath, string modName, ModSource source, string version);
}
```

- `.zip` detection is by extension (`.zip`, ordinal ignore-case); anything else
  is treated as a folder path. A `.zip` is extracted via
  `System.IO.Compression.ZipFile.ExtractToDirectory` (in-box for net10.0); a
  folder is recursively copied.
- **Upsert semantics:** the target `<SharedModsFolder>/<modName>/` is deleted
  first (if it exists), then re-populated. The manifest entry is upserted via
  `ISharedModStore.Add` (replaces a same-named entry, else appends). A re-import
  replaces the files + metadata wholesale (no merge).
- The returned entry carries the recorded `Source` + `ActualVersion` + `Path`.
- Does NOT touch profile mod lists: the caller adds the profile reference via
  `IProfileService.AddMod` after the import succeeds (order matters: import the
  shared copy, then reference it from the profile).
- Throws on I/O errors (copy/delete/extract), a malformed `.zip`
  (`InvalidDataException`), or a missing source path (`FileNotFoundException`).

### Key types

#### `SharedModEntry`

A single mod in the global shared store (immutable record):

| Field | Meaning |
| --- | --- |
| `Name` | The mod folder name: the value written to `mods.lst`. |
| `Policy` | The shared entry's version policy (drives allocation). Default `Latest`. |
| `Source` | Where this mod came from: Local / Nexus / GitHub (`ModSource`, default `NoneSource`). Makes a pinned version legible. |
| `ActualVersion` | The actual on-disk version of the shared copy: a raw release tag string (e.g. `"1.2"`, `"v2.0.1"`). Used for display; **not** the share decision (resolution is by the policies' pins, not this field). Default `string.Empty`. |
| `Path` | Where the shared mod files live: `<SharedModsFolder>/<Name>`. The symlink target when staging resolves this entry to Share. |

#### `ModSource` (abstract record)

A mod's source provenance: the type-safe one-of that records where a shared
copy came from. Three cases:

- `NoneSource`: local / untracked (the default; also the read-back value for a
  legacy entry lacking the field).
- `NexusSource(int ModId)`: Nexus Mods (the game is fixed: Darktide; the
  canonical identity is just the numeric mod id).
- `GitHubSource(string Owner, string Repo)`: GitHub (the canonical identity is
  the owner/repo pair).

Persisted polymorphically to `shared-manifest.json` via a `$kind` discriminator
with **stable identifiers** (`none` / `nexus` / `github`), mirroring the
established `ModVersionPolicy` serialization:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(NoneSource),   "none")]
[JsonDerivedType(typeof(NexusSource),  "nexus")]
[JsonDerivedType(typeof(GitHubSource), "github")]
public abstract record ModSource;
```

The UI collects URLs; the model stores canonical identity. URL → source parsing
lives in `ModSourceParser` (a pure helper, below).

#### `ModVersionPolicy` (abstract record)

A mod's version policy: the type-safe one-of that drives allocation. Two cases:

- `PinnedPolicy(string Version)`: frozen at a specific release tag. Two pins
  share only when their version strings match exactly (string equality).
- `LatestPolicy`: tracks the newest release (auto-update). Two Latests share
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

`PinnedPolicy.Version` is a **raw release tag string** (e.g. `"1.2"`,
`"v2.0.1"`, `"1.0.0-beta"`), stored verbatim. GitHub tags + Nexus versions are
arbitrary strings, not SemVer, so `string` is the right type. There is no
parsing or normalization at this layer; the pin is exact. A null/absent policy
defaults to `Latest` (the Phase 1 baseline), handled by caller coercion (e.g.
`ProfileService` on read), not by this type.

#### `ModSourceParser` (static)

Pure URL → canonical `ModSource` parsers (UI-agnostic, unit-tested). Never
throws: malformed input returns `false` and the caller shows a validation
message.

```csharp
public static class ModSourceParser
{
    // "https://www.nexusmods.com/warhammer40kdarktide/mods/12345"  -> NexusSource(12345)
    // Accepts with/without trailing slash/query; accepts a plain "12345" too.
    public static bool TryParseNexus(string input, out NexusSource source);

    // "https://github.com/owner/repo"  -> GitHubSource(owner, repo)
    // Accepts with/without trailing slash/".git"; rejects <2 path segments.
    public static bool TryParseGitHub(string input, out GitHubSource source);
}
```

Host matching is ordinal ignore-case (so `GITHUB.COM` still parses); owner/repo
are kept verbatim (no lowercasing). The Nexus parser validates the Darktide game
slug (`warhammer40kdarktide`) so a pasted URL for the wrong game is rejected.

#### `AllocationResolver` (static) + `AllocationResolution`

Pure allocation logic: resolves a profile mod's policy against the shared
entry's policy. No I/O, no logging, no DI.

```csharp
public static class AllocationResolver
{
    public static AllocationResolution Resolve(
        ModVersionPolicy sharedPolicy,
        string sharedActualVersion,    // unused for the decision (kept for clarity/future)
        ModVersionPolicy profilePolicy);
}

public enum AllocationResolution { Share, Diverge }
```

The four cases, by policy **intent** (not current version):

| Shared | Profile | Resolution |
| --- | --- | --- |
| `Pinned("1.0.1")` | `Pinned("1.0.1")` | **Share**, same pin (string equality) |
| `Pinned("1.0.1")` | `Pinned("2.0.1")` | **Diverge**, different pins |
| `Latest` | `Latest` | **Share**, both track latest |
| `Latest` | `Pinned("2.0.1")` | **Diverge**, shared will move, profile won't |

The resolution is by intent because a shared `Latest` and a profile `Pinned` to
today's same version still **Diverge**: the shared one will move on the next
release while the profile won't. `sharedActualVersion` is intentionally unused:
a matching version alone is not enough; both sides must agree on intent. The
"both Pinned" share check is **raw string equality** on the pin tags
(`sp.Version == pp.Version`, ordinal). Release tags are arbitrary strings, so
exact match is the correct comparison (no normalization, no ordering, no
component-count sensitivity: `"1.0"` and `"1.0.0"` are genuinely different pins).

## DI registration

```csharp
public static IServiceCollection AddSharedMods(this IServiceCollection services)
{
    services.TryAddSingleton<ISharedModStore, SharedModStore>();
    services.TryAddSingleton<IModImportService, ModImportService>();
    return services;
}
```

Uses `TryAddSingleton` (mirroring the `SymlinkCreator` seam in Profiles):
production behavior is unchanged (TryAdd registers on first call when nothing's
pre-registered), but a caller may pre-register an `ISharedModStore` or
`IModImportService` mock and have it survive `AddProfiles()` (which calls
`AddSharedMods()` unconditionally; a plain `AddSingleton` would clobber a
pre-registered mock, since MS DI resolves the last descriptor). Resolves
`MagosConfig` + `ILogger<>` from the container. Registered as singletons: no
per-request state; all state lives on disk (`MagosConfig` is itself a singleton).

## Dependencies

- **Magos libraries:** `config` (`MagosConfig.SharedModsFolder`).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.
- **BCL:** `System.IO.Compression` (the import service uses
  `ZipFile.ExtractToDirectory`; in-box for net10.0, no package reference).

## Testing

`Magos.Modificus.SharedMods.Tests` covers:

- `AllocationResolver` (the four-case logic + the intent-vs-version point;
  versions as raw strings, including the exact-match `"1.0"` vs `"1.0.0"`
  divergence case).
- `SharedModStore` manifest persistence (upsert, idempotent remove, first-run-
  safe + corrupt-manifest degradation; `Source` round-trip for all three kinds).
- `ModSource` JSON `$kind` round-trip (none/nexus/github) + defaults + record
  equality.
- `ModSourceParser` URL/id parsing (valid variants, trailing slash, query,
  `.git`, plain id; malformed rejections: wrong host, wrong game slug, too few
  segments, non-numeric/zero/negative id).
- `ModImportService` folder + `.zip` import, upsert semantics, recorded
  metadata, + error paths (missing source, malformed zip, bad mod name).

The internal `SharedModStore` + `ModImportService` are visible to tests via
`InternalsVisibleTo` (tests resolve them through the interface via DI).

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## Persistence note (Phase 3 Track B format change)

The Phase 3 Track B backend changed two persisted shapes:

- `PinnedPolicy.Version` and `SharedModEntry.ActualVersion` moved from
  `System.Version` (serialized as `{"Major":..,"Minor":..}`) to a raw **string**
  (serialized as a JSON string). Old `profile.json` / `shared-manifest.json`
  files will not deserialize cleanly into the new shapes.
- `SharedModEntry` gained a `Source` field (a `$kind`-discriminated
  `ModSource`); a legacy entry lacking it reads back as `NoneSource`.

No migration shim is provided (the operator's real profiles are dev-only at this
stage); a clean re-import is the path.

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md): the
  [Shared mod storage](../../architecture/MAGOS-MODIFICUS.md#shared-mod-storage)
  section.
- [profiles](profiles.md): applies the allocation rule during staging.
