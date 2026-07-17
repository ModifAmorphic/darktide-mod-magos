namespace Modificus.Curator.Mods;

/// <summary>
/// The unified mod repository: storage CRUD keyed by container, each holding
/// one or more versioned mod copies. One container per <c>(source, identity)</c>
/// pair; profiles reference a mod by container id + a version policy.
/// </summary>
/// <remarks>
/// <para>
/// Container identity is by source: Nexus by <see cref="NexusSource.ModId"/>,
/// Untracked by <see cref="ModContainer.Name"/> (the source record carries no
/// identity payload, so Untracked lookup goes through
/// <see cref="FindUntrackedByName"/>). Different source-types never collide and
/// never share.</para>
/// <para>
/// Safe for concurrent callers: the repository is read and mutated from both
/// the UI thread and background update-check work.</para>
/// </remarks>
public interface IModRepository
{
    /// <summary>All containers, in no guaranteed order.</summary>
    IReadOnlyList<ModContainer> List();

    /// <summary>Looks up a container by id. Null if absent.</summary>
    ModContainer? Get(Guid containerId);

    /// <summary>
    /// Looks up a container by its source identity: Nexus by
    /// <see cref="NexusSource.ModId"/>. Returns <c>null</c> for
    /// <see cref="UntrackedSource"/> (untracked identity is the container
    /// <see cref="ModContainer.Name"/>; use <see cref="FindUntrackedByName"/>).
    /// </summary>
    ModContainer? FindBySource(ModSource source);

    /// <summary>
    /// Looks up an untracked container by its <see cref="ModContainer.Name"/>
    /// (ordinal). Returns <c>null</c> if absent.
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
    /// with the absolute path of an EMPTY TEMP DIRECTORY (a sibling of the
    /// final version folder) so the caller extracts/copies the mod files into
    /// it; on success the repo atomically swaps the temp into the version folder
    /// (a same-volume <c>Directory.Move</c> rename), records the version entry
    /// on the manifest, and flips <see cref="ModVersion.IsLatest"/> to the
    /// newest (by <see cref="ModVersion.ImportedAt"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transactional overwrite (atomicity contract):</b> the temp directory
    /// is populated first; only on a successful return from
    /// <paramref name="populateFolder"/> does the repo delete the prior version
    /// folder (if any) and rename the temp into its place. On any exception from
    /// <paramref name="populateFolder"/> the temp is deleted (best-effort), the
    /// existing version folder is left UNTOUCHED (for a dedup re-import: the old
    /// version's files survive intact), and the manifest is unchanged. A failed
    /// re-import is therefore non-destructive: the caller sees the original
    /// exception and the mod on disk is exactly as it was before the call.</para>
    /// <para>
    /// <b>Upsert by <see cref="ModVersion.VersionString"/></b>: re-adding a
    /// version whose <paramref name="versionString"/> already exists on the
    /// container reuses its folder (the temp is swapped into the existing
    /// folder name); the existing version entry's
    /// <see cref="ModVersion.IsLatest"/> + <see cref="ModVersion.ImportedAt"/>
    /// are left unchanged (a re-import refreshes the files, not the manifest
    /// ordering), but <see cref="ModVersion.RemoteUploadedAt"/> IS overwritten
    /// from <paramref name="remoteUploadedAt"/> (matching how dedup refreshes
    /// files: a re-acquired version carries the current remote-publish
    /// timestamp, not the stale one from the first import). A new
    /// <paramref name="versionString"/> creates a new opaque folder + a new
    /// version entry stamped with the current time, and that new entry becomes
    /// <see cref="ModVersion.IsLatest"/> (it is the newest).</para>
    /// </remarks>
    /// <param name="containerId">The target container.</param>
    /// <param name="versionString">The raw release tag (e.g. <c>"1.2"</c>,
    /// <c>"v2.0.1"</c>). Dedup key within the container.</param>
    /// <param name="populateFolder">A callback that receives the absolute path
    /// of an empty temp directory (created by the repo, a sibling of the final
    /// version folder) and populates it: extract an archive, copy a folder, etc.
    /// On success the repo atomically swaps the temp into the version folder; on
    /// a thrown exception the temp is deleted and the existing version folder is
    /// left untouched.</param>
    /// <param name="remoteUploadedAt">Optional remote-publish timestamp (UTC)
    /// captured at acquisition for remote-source mods (Nexus). Recorded on the
    /// version entry in BOTH branches: a new version creates the entry with it,
    /// a dedup re-import overwrites the reused entry's value (matching how dedup
    /// refreshes files). <c>null</c> for manual imports (folder/archive) + non-
    /// remote sources, which aren't update-checked anyway. Source-agnostic:
    /// Integrations (the acquisition layer) owns Nexus metadata + passes it
    /// through; this seam does not know about Nexus.</param>
    /// <returns>The updated container (with the new/reused version entry
    /// recorded).</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="containerId"/> is
    /// unknown.</exception>
    ModContainer AddVersion(
        Guid containerId,
        string versionString,
        Action<string> populateFolder,
        DateTimeOffset? remoteUploadedAt = null);

    /// <summary>
    /// Renames a container's display label (the on-disk
    /// <c>container.json</c> <c>Name</c> field) and persists the manifest.
    /// Identity (<see cref="ModContainer.Id"/>) is unchanged: the on-disk
    /// container directory is keyed by <see cref="ModContainer.Id"/>, so it does
    /// not move. No-op + returns <c>null</c> when the container is unknown, and a
    /// no-op returning the unchanged container when the stored name already
    /// equals <paramref name="newName"/> (ordinal). For an
    /// <see cref="UntrackedSource"/> container the untracked-name index is kept
    /// consistent (the old name key is dropped, the new one recorded); for other
    /// sources the index is untouched (Nexus identity is on the source record,
    /// not the name).
    /// </summary>
    /// <param name="containerId">The target container.</param>
    /// <param name="newName">The new display name.</param>
    /// <returns>The updated container, or <c>null</c> when the container id is
    /// unknown.</returns>
    ModContainer? RenameContainer(Guid containerId, string newName);

    /// <summary>
    /// Removes a version from the container's manifest + deletes its folder
    /// (idempotent: a missing container or folder is a no-op). If the removed
    /// version carried <see cref="ModVersion.IsLatest"/>, the newest remaining
    /// version (by <see cref="ModVersion.ImportedAt"/>) is promoted.
    /// </summary>
    void RemoveVersion(Guid containerId, string versionFolder);

    /// <summary>
    /// Resolves the absolute on-disk path of a container's version folder. The
    /// repository is the path authority; paths are derived, never stored. Does
    /// not check existence (the caller decides what to do when the folder is
    /// absent).
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
    /// Container ids are stable across a relocation (the move never changes
    /// ids, whether a same-volume rename or a cross-volume copy + delete), so
    /// a relocate leaves the index valid by construction. <see cref="Relocate"/>
    /// calls this <em>internally</em> as the final step of its atomic move +
    /// save + rescan, so the next operation reads the new location with an
    /// index that matches it. This public surface is also useful after any
    /// out-of-band change to the mods folder (a hand-edit, an external tool, a
    /// backup restore) that bypasses <see cref="Relocate"/>.</para>
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
    /// <c>oldPath -&gt; newPath</c> via the move strategy the volume
    /// relationship dictates: same-volume is a fast, atomic directory rename
    /// (<c>Directory.Move</c>); cross-volume (e.g. Windows <c>C:\</c> -&gt;
    /// <c>D:\</c>) is a copy of the tree + source delete, because
    /// <c>Directory.Move</c> throws <c>IOException</c> across volumes rather
    /// than falling back to a copy. Best-effort per container, tracking which
    /// moved.</description></item>
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
