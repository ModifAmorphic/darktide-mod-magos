namespace Magos.Modificus.Mods;

/// <summary>
/// The unified mod repository: storage CRUD over per-container
/// <c>container.json</c> manifests. Replaces the earlier per-profile
/// diverged-copies model. Owns <c>&lt;ModsFolder&gt;/&lt;containerUUID&gt;/container.json</c>
/// (one manifest per container) plus the opaque-ID version subfolders.
/// </summary>
/// <remarks>
/// <para>
/// The repository builds an in-memory index at construction by scanning every
/// <c>&lt;ModsFolder&gt;/&lt;*&gt;/container.json</c> (dozens of
/// containers, cheap). There is no global databank file: the per-container
/// manifests are self-describing, so the index rebuilds from a scan. This makes
/// the repository resilient (no single corruption/loss point) + relocatable
/// (paths derive from the config root + UUIDs, never stored absolute).</para>
/// <para>
/// Container identity is by source: Nexus by <see cref="NexusSource.ModId"/>,
/// GitHub by <see cref="GitHubSource.Owner"/>/<see cref="GitHubSource.Repo"/>,
/// Untracked by <see cref="ModContainer.Name"/> (the source record carries no
/// identity payload, so Untracked lookup goes through
/// <see cref="FindUntrackedByName"/>). Different source-types never collide and
/// never share.</para>
/// <para>
/// Registered via <c>AddMods()</c>; <c>ProfileService</c> depends on this
/// for staging (the version-folder resolution seam). Concurrency is not
/// coordinated (single-UI-thread assumption, unchanged from the prior store).</para>
/// </remarks>
public interface IModRepository
{
    /// <summary>All containers, in scan order (no guaranteed sort).</summary>
    IReadOnlyList<ModContainer> List();

    /// <summary>Looks up a container by id. Null if absent.</summary>
    ModContainer? Get(Guid containerId);

    /// <summary>
    /// Looks up a container by its source identity: Nexus by
    /// <see cref="NexusSource.ModId"/>, GitHub by
    /// <see cref="GitHubSource.Owner"/>/<see cref="GitHubSource.Repo"/>. Returns
    /// <c>null</c> for <see cref="UntrackedSource"/> (untracked identity is the
    /// container <see cref="ModContainer.Name"/>; use
    /// <see cref="FindUntrackedByName"/>).
    /// </summary>
    ModContainer? FindBySource(ModSource source);

    /// <summary>
    /// Looks up an untracked container by its <see cref="ModContainer.Name"/>
    /// (ordinal). Null if absent. The untracked dedup path: a re-import of the
    /// same name resolves to the existing container instead of creating a new
    /// one.
    /// </summary>
    ModContainer? FindUntrackedByName(string name);

    /// <summary>
    /// Creates a new container: generates the <see cref="Guid"/>, writes an
    /// empty <c>container.json</c>, and returns the new container. Does not
    /// check for an existing same-identity container (the caller does that via
    /// <see cref="FindBySource"/> / <see cref="FindUntrackedByName"/> before
    /// deciding to create).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or
    /// whitespace.</exception>
    ModContainer CreateContainer(ModSource source, string name);

    /// <summary>
    /// Adds (or dedup-reuses) a version on the container. The repository creates
    /// the opaque version-folder ID and invokes <paramref name="populateFolder"/>
    /// with its absolute path so the caller extracts/copies the mod files into
    /// it; the repo then records the version entry on the manifest and flips
    /// <see cref="ModVersion.IsLatest"/> to the newest (by
    /// <see cref="ModVersion.ImportedAt"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Upsert by <see cref="ModVersion.VersionString"/></b>: re-adding a
    /// version whose <paramref name="versionString"/> already exists on the
    /// container reuses its folder (cleaned + repopulated via
    /// <paramref name="populateFolder"/>); the existing version entry's
    /// <see cref="ModVersion.IsLatest"/> + <see cref="ModVersion.ImportedAt"/>
    /// are left unchanged (a re-import refreshes the files, not the manifest
    /// ordering). A new <paramref name="versionString"/> creates a new opaque
    /// folder + a new version entry stamped with the current time, and that new
    /// entry becomes <see cref="ModVersion.IsLatest"/> (it is the newest).</para>
    /// </remarks>
    /// <param name="containerId">The target container.</param>
    /// <param name="versionString">The raw release tag (e.g. <c>"1.2"</c>,
    /// <c>"v2.0.1"</c>). Dedup key within the container.</param>
    /// <param name="populateFolder">A callback that receives the absolute path
    /// of the version folder (created by the repo) and populates it: extract a
    /// <c>.zip</c>, copy a folder, etc. The repo ensures the folder exists +
    /// is empty before invoking.</param>
    /// <returns>The updated container (with the new/reused version entry
    /// recorded).</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="containerId"/> is
    /// unknown.</exception>
    ModContainer AddVersion(Guid containerId, string versionString, Action<string> populateFolder);

    /// <summary>
    /// Removes a version from the container's manifest + deletes its folder
    /// (idempotent: a missing container or folder is a no-op). If the removed
    /// version carried <see cref="ModVersion.IsLatest"/>, the newest remaining
    /// version (by <see cref="ModVersion.ImportedAt"/>) is promoted.
    /// </summary>
    void RemoveVersion(Guid containerId, string versionFolder);

    /// <summary>
    /// Resolves the absolute on-disk path of a container's version folder:
    /// <c>&lt;ModsFolder&gt;/&lt;containerUUID&gt;/&lt;versionFolder&gt;</c>.
    /// The repository is the path authority (it owns <c>&lt;ModsFolder&gt;</c>);
    /// paths are derived, never stored. Callers use this for symlink targets at
    /// stage time. Does not check existence (the caller decides what to do when
    /// the folder is absent).
    /// </summary>
    string GetVersionFolderPath(Guid containerId, string versionFolder);

    /// <summary>
    /// Garbage-collects unreferenced versions + empty containers. Every
    /// <c>(containerId, versionFolder)</c> not in <paramref name="referenced"/>
    /// is dropped (manifest entry + on-disk folder); containers left with zero
    /// versions are removed entirely (manifest + directory). Idempotent;
    /// intended to run at startup so a clean state is enforced.
    /// </summary>
    /// <param name="referenced">The set of <c>(containerId, versionFolder)</c>
    /// pairs still referenced by some profile (the caller collects these by
    /// resolving each profile entry's policy against its container).</param>
    void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced);
}
