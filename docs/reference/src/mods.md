# Mods (`Modificus.Curator.Mods`) reference

> The unified mod repository: one UUID container per `(source, identity)`,
> holding opaque-ID version subfolders indexed by per-container manifests.
> Plus the version-policy model, the mod-source provenance model, and the
> local-import service. Archive import is format-agnostic (zip, 7z, rar, and
> the other formats SharpCompress supports, detected by content not extension).
> Status: implemented (the unified mod repository replaced the earlier
> shared-store + per-profile allocation model in #30; multi-format archive
> import + traversal-safe extraction landed in the archive-import fix).

A mod is stored exactly once, in the repository, keyed by a UUID container per
`(source-type, identity)`. Profiles reference a mod by `(containerId, policy)`
and never store mod files of their own. At stage time the profile resolves the
policy to a version folder and symlinks into the repository.

## Public surface

### `IModRepository`

The unified mod repository. Storage CRUD over per-container `container.json`
manifests. Owns `<ModsFolder>/<containerUUID>/container.json` (one manifest
per container) + the opaque-ID version subfolders.

```csharp
public interface IModRepository
{
    IReadOnlyList<ModContainer> List();
    ModContainer? Get(Guid containerId);
    ModContainer? FindBySource(ModSource source);     // Nexus by ModId, GitHub by Owner/Repo; null for Untracked
    ModContainer? FindUntrackedByName(string name);   // Untracked identity is the container Name
    ModContainer CreateContainer(ModSource source, string name);
    ModContainer AddVersion(Guid containerId, string versionString, Action<string> populateFolder, DateTimeOffset? remoteUploadedAt = null);
    void RemoveVersion(Guid containerId, string versionFolder);
    void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced);
    string GetVersionFolderPath(Guid containerId, string versionFolder);  // derived, never stored
    void Rescan();                 // rebuild the index from the live ModsFolder
    void Relocate(string newBasePath);   // atomic: move + save config + rescan (rollback on save failure)
}
```

- `List()`: every container, in scan order.
- `Get(containerId)`: lookup by id. Null if absent.
- `FindBySource(source)`: Nexus by `ModId`, GitHub by `Owner`/`Repo`. Returns
  `null` for `UntrackedSource` (untracked identity is the container `Name`;
  use `FindUntrackedByName`).
- `FindUntrackedByName(name)`: lookup an untracked container by its `Name`
  (ordinal). The untracked dedup path: a re-import of the same name resolves to
  the existing container.
- `CreateContainer(source, name)`: new UUID container + empty `container.json`.
  Does not check for an existing same-identity container (the caller does that
  via `FindBySource` / `FindUntrackedByName` first).
- `AddVersion(containerId, versionString, populateFolder, remoteUploadedAt = null)`: upsert by
  `versionString`. Re-adding the same tag reuses + refreshes its opaque folder
  (no re-order, `IsLatest` + `ImportedAt` unchanged; `RemoteUploadedAt` IS
  overwritten from `remoteUploadedAt`, matching how dedup refreshes files); a
  new tag creates a new opaque folder + a new entry that becomes `IsLatest`
  (the newest by `ImportedAt`). The optional `remoteUploadedAt` (UTC) is the
  underlying remote file's publish date, captured at acquisition for
  remote-source mods (Nexus) and stamped on the entry as `RemoteUploadedAt`
  (the update-check comparison basis). `null` for manual imports + non-remote
  sources.
  **Transactional:** the repo stages `populateFolder`'s output into a sibling
  temp dir, then atomically swaps it into the version folder on success
  (same-volume `Directory.Move`); on any failure the temp is cleaned + the
  existing version folder + manifest are left untouched, so a failed re-import
  is non-destructive (the old version survives a mid-extraction CRC/I/O
  failure). Orphan temps from a process crash are swept at each `AddVersion` +
  at index build (`RebuildIndex`).
- `RemoveVersion(containerId, versionFolder)`: idempotent. Promotes the newest
  remaining version to `IsLatest` if the removed one carried it.
- `PruneUnreferenced(referenced)`: GC. Drops every `(containerId, versionFolder)`
  not in the referenced set + removes containers left with zero versions.
  Intended for startup (`ModCleanup.PruneUnreferenced`).
- `GetVersionFolderPath(containerId, versionFolder)`: the absolute path
  `<ModsFolder>/<containerUUID>/<versionFolder>`. Derived (the repository
  owns `<ModsFolder>`); never stored. Used for staging symlink targets.
- `Rescan()`: rebuild the in-memory index from the **live** `ModsFolder` (the
  path `IConfigLoader.Load().ModsFolder` currently returns), clearing first.
  Container ids are stable across a relocation (the move never changes ids,
  whether a same-volume rename or a cross-volume copy + delete), so `Relocate`
  leaves the index valid by construction; `Rescan` is the defensive guarantee
  the index reflects whatever is actually on disk. Also useful after an
  out-of-band change (hand-edit, external tool, backup restore).
- `Relocate(newBasePath)`: the **atomic** repository relocation, owned by the
  repository so a save failure can never strand files at the new path with config
  still pointing at the old one. Steps: (1) read `oldPath = Load().ModsFolder`;
  (2) validate `newBasePath` (absolute, parent creatable; reject a conflicting
  tracked-UUID dir); (3) move every indexed container dir `oldPath → newPath`
  via the volume-appropriate strategy (same-volume = a fast, atomic
  `Directory.Move` rename; cross-volume = copy the tree + delete the source,
  because `Directory.Move` throws `IOException` across volumes, e.g. Windows
  `C:\` → `D:\`, rather than falling back to a copy), best-effort per container,
  tracking which moved; (4) save `ModsFolder =
  newPath` via `IConfigLoader`, and on save failure (a thrown exception, OR a
  silent failure since the production `ConfigLoader.Save` swallows write errors)
  roll the moved container dirs back to `oldPath` so files + config agree there
  again, then throw; (5) `Rescan` at `newPath`. The caller (the Settings VM)
  makes a single call; it does not save config or rescan separately. Throws
  `ArgumentException` (bad path), `InvalidOperationException` (UUID conflict), or
  `IOException` (rolled-back save failure).

The repository builds an in-memory index at construction by scanning every
`<ModsFolder>/*/container.json` (dozens of containers, cheap). There is no
global databank file: the per-container manifests are self-describing, so the
index rebuilds from a scan (resilient + relocatable). A corrupt/unreadable
manifest is skipped with a warning; one bad container never breaks the rest.

### `IModImportService`

Imports a local mod source (a folder OR an archive) into the repository.
The mod-list UI's add flow (picker + drag-and-drop) goes through this seam: the
UI never touches the filesystem directly.

```csharp
public interface IModImportService
{
    (Guid ContainerId, string VersionId) Import(string sourcePath, string modName, ModSource source, string version, DateTimeOffset? remoteUploadedAt = null);

    // Read-only peeks used by the add flow's base-name collision hard-block:
    string GetBaseName(string sourcePath);                       // validates structure, returns the base folder name (no container/version created)
    ModContainer? FindExistingContainer(ModSource source, string modName);  // the container an import would dedup to (no create)
}
```

- Container resolution: `FindUntrackedByName` for untracked (dedup by `modName`),
  `FindBySource` for Nexus/GitHub (dedup by source identity); `CreateContainer`
  if absent.
- Version resolution: dedup by `versionString` (`AddVersion` reuses the existing
  folder + refreshes its files); a new `versionString` creates a new version +
  flips `IsLatest`.
- **`remoteUploadedAt`** (optional, UTC): the underlying remote file's publish
  date, forwarded by the acquisition layer (`ModAcquisitionService`) for
  remote-source mods (Nexus) and recorded on the version entry as
  `RemoteUploadedAt`. The Nexus update check compares publish dates (the
  imported file vs the latest file), not import times; this parameter is how
  the publish date reaches the entry. `null` for manual imports (folder/archive
  via the picker or drag-and-drop) and non-Nexus sources, which aren't
  update-checked anyway.
- **Return:** the imported version's opaque folder id (`ModVersion.Folder`,
  not the display tag), so the caller can construct a `PinnedPolicy(versionId)`
  pinning the profile entry to exactly the version just imported. The display
  tag (`VersionString`) is recorded in the container manifest; it is not
  returned.
- Archive detection is **content-based** (`ArchiveFactory.IsArchive`, reading
  magic bytes, not the extension): a file is an archive iff SharpCompress
  recognizes it, so zip, 7z, rar, and SharpCompress's other formats all flow
  through one path. A non-archive file fails fast with an actionable
  `InvalidOperationException` (the user is told to extract it themselves +
  import the folder); a folder source is recursively copied (`DirectoryCopy`).
  Archives are extracted via traversal-safe **per-entry** `WriteToDirectory`
  (directory entries skipped, a defense-in-depth `AssertSafePath` containment
  check per file entry, no `SymbolicLinkHandler`); see
  [Path-traversal safety](#path-traversal-safety) below. A corrupt/CRC failure
  mid-extraction is caught and rethrown as `InvalidDataException` with a plain
  "try downloading again" message.
- Returns `(containerId, versionString)` so the caller does
  `IProfileService.AddMod(profileId, containerId, policy)`.
- Does NOT touch profile mod lists: the caller adds the profile reference after
  the import succeeds (order matters: import the repository copy, then reference
  it from the profile).
- The `modName` path-traversal confinement (rejects path separators, `..`,
  absolute paths) is retained from Track B as defense-in-depth, even though the
  import target is now an opaque UUID folder (not `modName`-derived).

**Source structure (both kinds):** a mod source (archive or folder) must
contain exactly one base directory with a `<basefoldername>.mod` descriptor
inside it (the descriptor filename matches the base folder name). An archive is
inspected **before** extraction (single top-level folder, no loose top-level
files, matching `<base>.mod`); a folder is checked directly (non-empty +
matching descriptor) and is copied **as the folder itself** (not its contents).
Both produce `<versionId>/<base>/<files>`. An invalid structure throws
immediately, placing no files and creating no container/version.

**Path-traversal safety:** archive extraction is untrusted input (mods are
downloaded from the internet). SharpCompress had a directory-traversal CVE
(CVE-2026-44788, fixed in 0.48.0; we pin 0.49.1). Three mitigations, none
dependent on the library version alone: (1) per-entry `WriteToDirectory` (the
CVE-advisory-blessed file-entry path), never the convenience
`archive.WriteToDirectory()`; (2) directory entries skipped explicitly (the
vulnerable branch); (3) our own `AssertSafePath` containment check per file
entry (normalizes `\` → `/`, then `Path.GetFullPath` + prefix check, so a
`..\escape` or absolute-path entry cannot bypass it on Linux). No
`SymbolicLinkHandler` is supplied (the TAR-only symlink escalation requires one;
Darktide mods don't use symlinks).

**Base-name collision hard-block:** two mods with the same base folder name can't
coexist in one profile (the mod loader can't tell them apart). The add flow
peeks the base name (`GetBaseName`) + the would-be container
(`FindExistingContainer`) **before** importing, then asks
`IProfileService.GetBaseNameCollision` whether any existing profile mod (a
different container) resolves to the same base name. On a hit, the import is
**refused**: nothing is created (no version, no profile entry). The would-be
container is excluded from the check, so a re-add of a mod already in the
profile (same container, `AddMod` idempotent) is **not** a collision. The block
lives at the add flow (not in `Import` itself), so direct programmatic imports
remain unconditional.

### Key types

#### `ModContainer` (record)

A single mod in the repository (immutable record):

| Field | Meaning |
| --- | --- |
| `Id` | Stable identity (Guid); the on-disk container directory name. |
| `Source` | Where this mod came from: Untracked / Nexus / GitHub (`ModSource`, default `UntrackedSource`). |
| `Name` | The display name + the untracked dedup key. Set at import. |
| `Versions` | The container's imported versions (`IReadOnlyList<ModVersion>`). One may carry `IsLatest`. |

The container's on-disk path is **derived**:
`<ModsFolder>/<Id>/`. It is never stored absolute, so relocating the
repository is a physical move of the tree plus a config update (no manifest
rewriting, no drift detection).

`ResolveVersion(policy)` is a pure helper on the container that resolves a
profile's policy to the version it should stage: `LatestPolicy` → the
`IsLatest` version; `PinnedPolicy(vId)` → the version whose `Folder == vId`
(raw string equality on the opaque version id). Returns `null` when there is no
match. Centralized so staging and the startup prune cannot drift.

#### `ModVersion` (record)

One version of a mod (immutable record):

| Field | Meaning |
| --- | --- |
| `Folder` | The opaque version-folder ID (UUID-derived). The version's files live at `<ModsFolder>/<containerId>/<Folder>/`. Never the raw version tag. |
| `VersionString` | The raw release tag (e.g. `"1.2"`, `"v2.0.1"`). Used for display only. Arbitrary source tags, not SemVer; never parsed. |
| `IsLatest` | Whether this is the container's current latest version. Exactly one per container (the newest by `ImportedAt`). Moving latest is a one-field manifest edit. |
| `ImportedAt` | When first imported (UTC). Orders the versions; the newest carries `IsLatest`. |
| `RemoteUploadedAt` | When the underlying remote file was published (UTC), captured at acquisition for remote-source mods (Nexus). `null` for manual imports + non-remote sources. The update check uses it (with an `ImportedAt` fallback) as the comparison basis against the latest file's publish date. Backward-compatible on disk: a manifest from before this field existed deserializes it to `null`. |

#### `ModSource` (abstract record)

A mod's source provenance. Three cases:

- `UntrackedSource`: local / untracked (the default). No identity payload; the
  dedup key is the container `Name`.
- `NexusSource(int ModId)`: Nexus Mods (the game is fixed: Darktide; the
  canonical identity is just the numeric mod id).
- `GitHubSource(string Owner, string Repo)`: GitHub (the canonical identity is
  the owner/repo pair).

Persisted polymorphically to `container.json` via a `$kind` discriminator with
**stable identifiers** (`untracked` / `nexus` / `github`):

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(UntrackedSource), "untracked")]
[JsonDerivedType(typeof(NexusSource),  "nexus")]
[JsonDerivedType(typeof(GitHubSource), "github")]
public abstract record ModSource;
```

Different source-types are separate namespaces: an untracked "WeaponTweaks" and a
Nexus "WeaponTweaks" are distinct containers (never collide, never share).

#### `ModVersionPolicy` (abstract record)

A mod's version policy: drives version resolution at stage time. Two cases:

- `PinnedPolicy(string VersionId)`: frozen at a specific release, referenced by
  id (a foreign key to a `ModVersion.Folder`). Resolves to the version whose
  `Folder` matches exactly (string equality).
- `LatestPolicy`: tracks the newest release. Resolves to the container's
  `IsLatest` version.

Persisted polymorphically to `container.json` and `profile.json` via a `$kind`
discriminator (`pinned` / `latest`):

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(PinnedPolicy), "pinned")]
[JsonDerivedType(typeof(LatestPolicy),  "latest")]
public abstract record ModVersionPolicy
{
    public static ModVersionPolicy Latest { get; } = new LatestPolicy();
}
```

The version reference is **by id, not by tag**. `PinnedPolicy.VersionId` is a
foreign key to a `ModVersion.Folder` (the opaque on-disk version folder name,
a `Guid.NewGuid().ToString("N")` minted by `IModRepository.AddVersion`). The
repository stays the single source of truth for version details
(`VersionString`, `IsLatest`); the profile holds only the id, so a phantom pin
(an id with no matching version in the container) cannot be silently created:
`IProfileService.SetModPolicy` rejects an orphan id, and the UI's pin dropdown
can only produce ids the container's version list already holds.

#### `ModSourceParser` (static)

Pure URL → canonical `ModSource` parsers (UI-agnostic, unit-tested). Never
throws: malformed input returns `false`.

```csharp
public static class ModSourceParser
{
    // "https://www.nexusmods.com/warhammer40kdarktide/mods/12345"  -> NexusSource(12345)
    public static bool TryParseNexus(string input, out NexusSource source);

    // "https://github.com/owner/repo"  -> GitHubSource(owner, repo)
    public static bool TryParseGitHub(string input, out GitHubSource source);
}
```

## DI registration

```csharp
public static IServiceCollection AddMods(this IServiceCollection services)
{
    services.TryAddSingleton<IModRepository, ModRepository>();
    services.TryAddSingleton<IModImportService, ModImportService>();
    return services;
}
```

Uses `TryAddSingleton` (mirroring the `SymlinkCreator` seam in Profiles):
production behavior is unchanged, but a caller may pre-register an
`IModRepository` or `IModImportService` fake and have it survive `AddProfiles()`
(which calls `AddMods()` unconditionally). Resolves `IConfigLoader` +
`ILogger<>` from the container. Registered as singletons: the repository holds
the in-memory index (cheap to rebuild), and `ModsFolder` is read live from
`IConfigLoader` on each operation (one snapshot per op) so a runtime folder
change via the Settings window takes effect immediately.

## On-disk layout

```
<ModsFolder>/                 (auto-created on first run)
  <containerUUID>/                  (container dir; id-named, opaque)
    container.json                  (id + source + name + versions[] - the manifest)
    <versionFolder>/                (opaque-ID version subfolder)
      <baseFolder>/                 (the mod's base folder; name matches <base>.mod)
        <baseFolder>.mod            (the descriptor the loader resolves)
        <files...>                  (scripts/, etc.)
    <versionFolder>/
      ...
```

The version folder always contains exactly one subdirectory: the mod's **base
folder**, whose name matches its `<base>.mod` descriptor. Both import kinds
produce this shape (an archive is validated to have a single top-level folder
with a matching descriptor before extraction; a folder is copied as itself).
The base folder name is load-bearing at staging time: mods bake their folder
name into their code, so the staged symlink must carry the base name (not the
container's display name).

`container.json` is UTF-8 without BOM. Paths are derived (`ModsFolder` +
UUIDs), never stored absolute.

## Dependencies

- **Curator libraries:** `config` (`CuratorConfig.ModsFolder`).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`, `SharpCompress` 0.49.1 (MIT;
  archive detection + extraction for zip / 7z / rar / tar / gzip / .... Pinned
  `>= 0.48.0` for the CVE-2026-44788 traversal fix; mitigations in
  [Path-traversal safety](#path-traversal-safety) make us safe regardless of
  the library version alone. No dependencies on net10.0.).

## Testing

`Modificus.Curator.Mods.Tests` covers:

- `ModRepository`: container/version CRUD, `FindBySource` per source-type +
  `FindUntrackedByName`, manifest round-trip + in-memory index rebuild from a
  scan (skips non-container dirs + corrupt manifests), `PruneUnreferenced`
  (drops unreferenced version folders + empty containers, keeps referenced),
  opaque version-folder naming, derived paths, and the relocation surface:
  `Relocate` (atomic move + config save + rescan; rolls the move back when the
  save fails, whether the loader throws or silently fails; the cross-volume path
  is covered by forcing the volume detector, proving a copy + delete relocate
  succeeds where `Directory.Move` would throw) + `Rescan`
  (drops/Adds index entries to match the live disk state).
- `DirectoryCopy`: faithful recursive copy (files + nested subdirs reproduced,
  target created as it goes).
- `ModSource` JSON `$kind` round-trip (untracked/nexus/github) + defaults +
  record equality.
- `ModSourceParser` URL/id parsing (valid variants, trailing slash, query,
  `.git`, plain id; malformed rejections: wrong host, wrong game slug, too few
  segments, non-numeric/zero/negative id).
- `ModImportService`: container find/create + version dedup + `isLatest` flip +
  folder/archive import (zip, 7z via on-the-fly SharpCompress writers, rar via
  a committed RAR5 fixture under `Fixtures/`) + the source-structure validation
  (both kinds require exactly one base directory with a matching `<base>.mod`
  descriptor; the base folder is preserved under `<versionFolder>/<base>/`) +
  error paths (missing source, unsupported-format plain error, corrupt/CRC
  archive, multiple top-level folders, loose files, missing / mismatched
  descriptor, bad mod name) + the retained `modName` path-traversal confinement
  + the **per-entry extraction traversal guard** (a crafted zip with `../` +
  absolute-path entries is refused; nothing is written outside the extraction
  root) + the two import-time peeks (`GetBaseName` validates + returns the base
  name without creating anything; `FindExistingContainer` resolves the would-be
  dedup container without creating it).

The internal `ModRepository` + `ModImportService` are visible to tests via
`InternalsVisibleTo` (tests resolve them through the interface via DI).

```sh
dotnet test src/modificus-curator.sln -c Release
```

## Persistence note (format change)

This refactor supersedes the earlier per-profile allocation model. The persisted
shapes changed incompatibly:

- The old `<ModsFolder>/shared-manifest.json` (the pre-refactor manifest, a JSON
  array of the legacy `ModEntry`) is orphaned: the new code does not read it. The
  operator clears it once (or points `ModsFolder` at a fresh location).
- Each profile's old `mods/` directory is no longer created or read.
- `profile.json` mod entries changed shape: `Name` → `ContainerId` (Guid). Old
  entries deserialize with `ContainerId == Guid.Empty` and are dropped on read
  (logged); the profile is otherwise intact.

No migration shim is provided (the spec is fresh-start; the operator's real
profiles are dev-only at this point).

## See also

- [Modificus Curator architecture](../../architecture/MODIFICUS-CURATOR.md): the
  [Mod repository](../../architecture/MODIFICUS-CURATOR.md#mod-repository) section.
- [profiles](profiles.md): consumes `IModRepository` for staging (resolves each
  profile mod's policy to a version folder) + the startup prune.
