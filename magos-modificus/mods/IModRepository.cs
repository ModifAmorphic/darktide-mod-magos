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

    /// <summary>
    /// Rebuilds the in-memory index from the <b>live</b> mods root (the path
    /// <see cref="General.IConfigLoader.Load()"/>.<c>ModsFolder</c> currently
    /// returns). Clears the existing index first, then re-scans the directory
    /// tree exactly as the constructor did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Container ids are stable across a relocation (the move is a directory
    /// rename, not an id change), so a relocate leaves the index valid by
    /// construction. <see cref="Relocate"/> calls this <em>internally</em> as
    /// the final step of its atomic move + save + rescan, so the next
    /// operation reads the new location with an index that matches it. This
    /// public surface is also useful after any out-of-band change to the mods
    /// folder (a hand-edit, an external tool, a backup restore) that bypasses
    /// <see cref="Relocate"/>.</para>
    /// </remarks>
    void Rescan();

    /// <summary>
    /// Live-moves every container directory from the <b>current</b> mods root
    /// (read live from config, i.e. the OLD path) to <paramref name="newBasePath"/>
    /// AND, atomically, persists <c>ModsFolder = newBasePath</c> through
    /// <see cref="General.IConfigLoader"/> and rescans the index at the new
    /// path. Best-effort per container on the move: an individual move failure
    /// is logged + skipped so one locked directory does not abort the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Atomicity (single-call contract):</b> the move, the config save, and
    /// the rescan are owned by this method so a save failure can never strand
    /// the files at the new path with the config still pointing at the old one.
    /// The steps, in order:
    /// <list type="number">
    /// <item><description>Read <c>oldPath = Load().ModsFolder</c> (the OLD
    /// path).</description></item>
    /// <item><description>Validate <paramref name="newBasePath"/> (absolute,
    /// parent creatable; reject a conflicting tracked-UUID
    /// dir).</description></item>
    /// <item><description>Move every indexed container dir
    /// <c>oldPath -&gt; newPath</c> (best-effort per container, tracking which
    /// moved).</description></item>
    /// <item><description>Save the config with <c>ModsFolder = newPath</c>. On
    /// save failure (a thrown exception, OR a silent failure: the production
    /// <see cref="General.ConfigLoader"/> swallows write errors by design), roll
    /// the moved container dirs back to <c>oldPath</c> so files + config agree
    /// at <c>oldPath</c> again, then throw to surface the failure to the
    /// caller.</description></item>
    /// <item><description>Rescan: rebuild the index at <c>newPath</c> (now the
    /// live config path).</description></item>
    /// </list>
    /// The caller (the Settings VM) makes a single Relocate call; it does not
    /// save the config or rescan separately.</para>
    /// <para>
    /// The index is untouched by the move itself (container ids are unchanged
    /// by a directory rename); the final <see cref="Rescan"/> is the defensive
    /// resync that guarantees the index reflects whatever is actually at the
    /// new path.</para>
    /// <para>
    /// <b>Scope of the move:</b> only directories whose name is a container UUID
    /// <em>in the index</em> are moved (the containers the repository tracks).
    /// Stray non-UUID directories and corrupt-manifest UUID directories (skipped
    /// at construction) are left in place; they are not the repository's
    /// concern.</para>
    /// </remarks>
    /// <param name="newBasePath">The absolute path to move container directories
    /// into. Must be absolute and its parent must be creatable.</param>
    /// <exception cref="ArgumentException"><paramref name="newBasePath"/> is
    /// null/whitespace, not absolute, or its parent directory cannot be
    /// created.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="newBasePath"/>
    /// already contains a directory whose name is a container UUID the
    /// repository tracks (a conflicting pre-existing container).</exception>
    /// <exception cref="IOException">The move succeeded but the config save
    /// failed (thrown or silent); the moves are rolled back to the old path
    /// before this throws.</exception>
    void Relocate(string newBasePath);
}
