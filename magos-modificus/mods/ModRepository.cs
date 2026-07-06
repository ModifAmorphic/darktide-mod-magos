using System.Text;
using System.Text.Json;
using Magos.Modificus.Config;
using Magos.Modificus.General;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Mods;

/// <summary>
/// Filesystem-backed <see cref="IModRepository"/>. Each container lives under
/// <c>&lt;ModsFolder&gt;/&lt;containerUUID&gt;/</c> with this layout:
/// </summary>
/// <remarks>
/// <code>
/// &lt;ModsFolder&gt;/              (auto-created on first run)
///   &lt;containerUUID&gt;/                 (container dir; id-named, opaque)
///     container.json                   (id + source + name + versions[] - the manifest)
///     &lt;versionFolder&gt;/                (opaque-ID version subfolder; the mod files)
///     &lt;versionFolder&gt;/
///       ...
/// </code>
/// <para>
/// On construction the repository scans every
/// <c>&lt;ModsFolder&gt;/&lt;*&gt;/container.json</c> into an in-memory
/// index (<c>containerId -> container</c> + an untracked-name lookup). All
/// mutations write through to the per-container manifest; the index stays in
/// sync. A corrupt/unreadable manifest is skipped with a warning (one bad
/// container never breaks the rest), mirroring <c>ProfileService</c>'s
/// "skip unreadable, keep going" posture.</para>
/// <para>
/// Registered as a singleton: it holds the in-memory index (cheap to rebuild).
/// The mods root folder is read live from <see cref="IConfigLoader"/> on each
/// operation (one snapshot per op), so a runtime folder change via the upcoming
/// Settings window takes effect immediately; <see cref="Directory.CreateDirectory"/>
/// runs per-op (idempotent) on the live path. Concurrent writes are not
/// coordinated (single-UI-thread assumption).</para>
/// </remarks>
internal sealed class ModRepository : IModRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    // Consistent with profile.json / mods.lst: UTF-8 without BOM (hand-edits +
    // diffs stay clean; no consumer expects a BOM here).
    private static readonly Encoding ManifestEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly string VersionManifestFileName = "container.json";

    private readonly IConfigLoader _configLoader;
    private readonly ILogger<ModRepository> _logger;

    // Same-volume detector used by Relocate to pick the move strategy (rename
    // vs copy + delete). Injectable so a test can force the cross-volume path
    // without a real second volume (cross-volume cannot be simulated under one
    // temp root). The DI constructor wires the real path-root comparison.
    private readonly Func<string, string, bool> _sameVolume;

    // Primary index: containerId -> container. Source identity lookups are
    // served by scanning this (cheap for dozens of containers); the untracked-
    // name index below is the only dedicated secondary index (untracked dedup
    // happens on every import).
    private readonly Dictionary<Guid, ModContainer> _byId = new();

    // Untracked-name -> containerId (only untracked containers are entered).
    // Nexus/GitHub lookups scan _byId (identity is fully on the source record).
    private readonly Dictionary<string, Guid> _untrackedByName = new(StringComparer.Ordinal);

    /// <summary>
    /// DI constructor. Wires the real same-volume detector (path-root
    /// comparison, see <see cref="SameVolumeByRoot"/>).
    /// </summary>
    public ModRepository(IConfigLoader configLoader, ILogger<ModRepository> logger)
        : this(configLoader, logger, SameVolumeByRoot)
    {
    }

    /// <summary>
    /// Internal constructor that lets a test inject the same-volume detector
    /// (force the cross-volume copy + delete path). Production resolves the
    /// public constructor through DI.
    /// </summary>
    internal ModRepository(IConfigLoader configLoader, ILogger<ModRepository> logger, Func<string, string, bool> sameVolume)
    {
        _configLoader = configLoader;
        _logger = logger;
        _sameVolume = sameVolume;

        // Build the in-memory index from the current mods root. The index is
        // construction-time state (a scan of the disk); live-read changes the
        // per-op path computations, not the index contents. A runtime folder
        // relocation re-scans through the app's restart / relocation flow.
        var baseFolder = EnsureBaseFolder();
        RebuildIndex(baseFolder);
    }

    /// <summary>
    /// Reads the mods root folder from the live config snapshot and ensures it
    /// exists. Called at the top of each public operation so a runtime folder
    /// change takes effect immediately (the directory is created on the live
    /// path, and subsequent path helpers derive from it).
    /// </summary>
    private string EnsureBaseFolder()
    {
        // ModsFolder is non-null by MagosConfig contract (defaults to
        // <app-data>/mods). Directory.CreateDirectory is idempotent, so this
        // makes every subsequent op first-run safe without each re-checking.
        var baseFolder = _configLoader.Load().ModsFolder;
        Directory.CreateDirectory(baseFolder);
        return baseFolder;
    }

    /// <inheritdoc />
    public IReadOnlyList<ModContainer> List() => _byId.Values.ToArray();

    /// <inheritdoc />
    public ModContainer? Get(Guid containerId) =>
        _byId.TryGetValue(containerId, out var c) ? c : null;

    /// <inheritdoc />
    public ModContainer? FindBySource(ModSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Untracked identity is the container Name, not a field on the source.
        // Callers route untracked dedup through FindUntrackedByName; here we
        // return null so a generic FindBySource caller never gets a false match.
        if (source is UntrackedSource)
        {
            return null;
        }

        return source switch
        {
            NexusSource n => _byId.Values.FirstOrDefault(c =>
                c.Source is NexusSource ns && ns.ModId == n.ModId),
            GitHubSource g => _byId.Values.FirstOrDefault(c =>
                c.Source is GitHubSource gs
                && string.Equals(gs.Owner, g.Owner, StringComparison.Ordinal)
                && string.Equals(gs.Repo, g.Repo, StringComparison.Ordinal)),
            _ => null,
        };
    }

    /// <inheritdoc />
    public ModContainer? FindUntrackedByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _untrackedByName.TryGetValue(name, out var id) ? Get(id) : null;
    }

    /// <inheritdoc />
    public ModContainer CreateContainer(ModSource source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Container name must not be null or whitespace.", nameof(name));
        }

        var baseFolder = EnsureBaseFolder();
        var container = new ModContainer
        {
            Id = Guid.NewGuid(),
            Source = source,
            Name = name,
            Versions = Array.Empty<ModVersion>(),
        };

        Directory.CreateDirectory(ContainerDir(baseFolder, container.Id));
        WriteContainer(container, baseFolder);

        _byId[container.Id] = container;
        IndexUntrackedName(container);
        _logger.LogInformation("Created container {Id} ('{Name}', source={Source})", container.Id, name, source);
        return container;
    }

    /// <inheritdoc />
    public ModContainer AddVersion(Guid containerId, string versionString, Action<string> populateFolder)
    {
        ArgumentNullException.ThrowIfNull(versionString);
        ArgumentNullException.ThrowIfNull(populateFolder);

        if (!_byId.TryGetValue(containerId, out var container))
        {
            throw new KeyNotFoundException($"No mod container with id '{containerId}'.");
        }

        var baseFolder = EnsureBaseFolder();
        var containerDir = ContainerDir(baseFolder, containerId);
        Directory.CreateDirectory(containerDir);

        var existing = container.Versions.FirstOrDefault(v =>
            string.Equals(v.VersionString, versionString, StringComparison.Ordinal));

        List<ModVersion> versions;
        if (existing is not null)
        {
            // Dedup: reuse the existing folder + entry. PopulateAtomically swaps
            // the new content into the existing version folder atomically (the
            // old contents survive any populateFolder failure); the version
            // entry (Folder, VersionString, IsLatest, ImportedAt) is left
            // unchanged so the manifest ordering stays stable. (Re-importing a
            // version is a file refresh, not a re-order.)
            var versionDir = VersionDir(baseFolder, containerId, existing.Folder);
            PopulateAtomically(versionDir, populateFolder);
            versions = container.Versions.ToList();
            _logger.LogInformation(
                "Re-imported version '{Version}' on container {Id} (folder reused: {Folder})",
                versionString, containerId, existing.Folder);
        }
        else
        {
            // New version: new opaque folder + new entry stamped now; the new
            // entry is the newest by ImportedAt, so it becomes IsLatest and the
            // flag is cleared on every other version. PopulateAtomically stages
            // into a temp + swaps into the new version folder; on a
            // populateFolder failure nothing is created on disk and the manifest
            // is left untouched (no entry added below).
            var folder = Guid.NewGuid().ToString("N");
            var versionDir = VersionDir(baseFolder, containerId, folder);
            PopulateAtomically(versionDir, populateFolder);

            var entry = new ModVersion
            {
                Folder = folder,
                VersionString = versionString,
                IsLatest = true,
                ImportedAt = DateTimeOffset.UtcNow,
            };
            versions = container.Versions
                .Select(v => v with { IsLatest = false })
                .Append(entry)
                .ToList();
            _logger.LogInformation(
                "Added version '{Version}' on container {Id} (folder: {Folder}, isLatest=true)",
                versionString, containerId, folder);
        }

        var updated = container with { Versions = versions };
        _byId[containerId] = updated;
        WriteContainer(updated, baseFolder);
        return updated;
    }

    /// <summary>
    /// Stages <paramref name="populateFolder"/>'s output into a temp directory
    /// that is a SIBLING of <paramref name="versionDir"/> (so the final swap is
    /// a same-volume atomic <see cref="Directory.Move"/>), then swaps it into
    /// <paramref name="versionDir"/>. On any exception from
    /// <paramref name="populateFolder"/> the temp is deleted (best-effort) and
    /// the existing <paramref name="versionDir"/> is left untouched (for dedup:
    /// the old version survives on disk; for new-version: nothing was created),
    /// and the original exception is rethrown as-is (no swallowing, no wrapping).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the transactional core of <see cref="AddVersion"/>. The previous
    /// implementation cleaned <paramref name="versionDir"/> first and then
    /// invoked <paramref name="populateFolder"/> directly on it, so an
    /// extraction failure (CRC error, disk full, I/O error, anything) left the
    /// old version already deleted and the new one partial, while the manifest
    /// still referenced that folder: a manifest/disk inconsistency on a mod
    /// potentially referenced by a profile, with no recovery (the startup prune
    /// only reclaims containers no profile references).</para>
    /// <para>
    /// <b>tempDir location:</b> a sibling of <paramref name="versionDir"/> under
    /// the same container dir, named <c>&lt;versionFolder&gt;.tmp.&lt;guid&gt;</c>.
    /// It MUST be a sibling (not under the system temp): a cross-volume
    /// <see cref="Directory.Move"/> throws <see cref="IOException"/> rather than
    /// falling back to a copy, and the system temp is commonly on a different
    /// volume from the mods root.</para>
    /// <para>
    /// <b>Swap order:</b> populate succeeds first (into the temp), THEN
    /// <see cref="CleanTarget"/>(<paramref name="versionDir"/>) runs (a no-op
    /// on the new-version branch since it does not exist yet; deletes the old
    /// version on dedup), THEN <see cref="Directory.Move"/>(tempDir,
    /// versionDir). A failure of populate therefore never destroys the old
    /// version; the only window in which both old and new are absent is between
    /// CleanTarget and Move, both of which are near-instant BCL calls on a
    /// same-volume path.</para>
    /// <para>
    /// <b>Crash recovery:</b> if the process dies between CreateDirectory(temp)
    /// and Move, the temp is left as an orphan under the container dir. The
    /// repo's index is built from <c>container.json</c>, not by scanning version
    /// subfolders, so the orphan is invisible to the index but occupies disk.
    /// <see cref="SweepOrphanTemps"/> deletes any <c>*.tmp.*</c> directories
    /// under the container dir at the start of each call as best-effort
    /// cleanup.</para>
    /// </remarks>
    private void PopulateAtomically(string versionDir, Action<string> populateFolder)
    {
        var containerDir = Path.GetDirectoryName(versionDir)!;
        SweepOrphanTemps(containerDir);

        var tempDir = versionDir + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(tempDir);
            populateFolder(tempDir);
        }
        catch
        {
            // The existing versionDir is untouched (CleanTarget has not run yet
            // for dedup; the new-version branch never created it). Best-effort
            // delete of the partial temp; an orphan left here is reclaimed by
            // SweepOrphanTemps on the next call. Rethrow as-is: no swallowing,
            // no wrapping, callers see the actual failure.
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "Could not delete orphan temp dir {Path} after populateFolder failed.",
                    tempDir);
            }
            throw;
        }

        // Swap: remove the old version (no-op when versionDir does not exist
        // yet, i.e. the new-version branch), then atomic same-volume rename.
        CleanTarget(versionDir);
        Directory.Move(tempDir, versionDir);
    }

    /// <summary>
    /// Best-effort sweep of orphan temp directories left under
    /// <paramref name="containerDir"/> by a <see cref="PopulateAtomically"/>
    /// call whose process crashed before the swap. Recognizes the
    /// <c>&lt;versionFolder&gt;.tmp.&lt;guid&gt;</c> naming convention; any
    /// delete failure is logged + skipped so one locked dir does not abort the
    /// rest of the sweep or the caller. Version folders are 32-char hex GUIDs
    /// and container UUIDs are also GUIDs, so the <c>.tmp.</c> substring never
    /// matches a real tracked folder.
    /// </summary>
    private void SweepOrphanTemps(string containerDir)
    {
        if (!Directory.Exists(containerDir))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(containerDir))
        {
            var name = Path.GetFileName(dir);
            // Match the <versionFolder>.tmp.<guid> naming only when ".tmp." is
            // preceded by the version-folder name. Real orphans carry the
            // 32-char hex GUID prefix, so ".tmp." sits at position 32, never 0;
            // <= 0 skips both not-found and a hypothetical prefix-less ".tmp.*".
            if (name.IndexOf(".tmp.", StringComparison.Ordinal) <= 0)
            {
                continue;
            }
            try
            {
                Directory.Delete(dir, recursive: true);
                _logger.LogInformation("Swept orphan temp dir {Path} left by a prior crashed import.", dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete orphan temp dir {Path}.", dir);
            }
        }
    }

    /// <inheritdoc />
    public void RemoveVersion(Guid containerId, string versionFolder)
    {
        ArgumentNullException.ThrowIfNull(versionFolder);

        if (!_byId.TryGetValue(containerId, out var container))
        {
            return; // idempotent: unknown container is a no-op.
        }

        var existing = container.Versions.FirstOrDefault(v =>
            string.Equals(v.Folder, versionFolder, StringComparison.Ordinal));
        if (existing is null)
        {
            return; // idempotent: unknown folder is a no-op.
        }

        var baseFolder = EnsureBaseFolder();
        var wasLatest = existing.IsLatest;
        var versions = container.Versions.Where(v => !ReferenceEquals(v, existing)).ToList();

        // If the removed entry was latest, promote the newest remaining by
        // ImportedAt (stable on ties; MaxBy returns the first max for ties under
        // LINQ-to-Objects, matching the storage order).
        if (wasLatest && versions.Count > 0)
        {
            var newest = versions.MaxBy(v => v.ImportedAt)!;
            versions = versions
                .Select(v => v with { IsLatest = ReferenceEquals(v, newest) })
                .ToList();
        }

        var updated = container with { Versions = versions };
        _byId[containerId] = updated;
        WriteContainer(updated, baseFolder);

        // Best-effort: drop the on-disk folder. A missing / unwritable folder
        // is logged + swallowed so a prune never crashes on a stray lock.
        DeleteVersionDir(baseFolder, containerId, versionFolder);
        _logger.LogInformation(
            "Removed version folder '{Folder}' from container {Id}", versionFolder, containerId);
    }

    /// <inheritdoc />
    public string GetVersionFolderPath(Guid containerId, string versionFolder)
    {
        ArgumentNullException.ThrowIfNull(versionFolder);
        var baseFolder = EnsureBaseFolder();
        return VersionDir(baseFolder, containerId, versionFolder);
    }

    /// <inheritdoc />
    public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced)
    {
        ArgumentNullException.ThrowIfNull(referenced);
        var baseFolder = EnsureBaseFolder();

        // Drop unreferenced versions per container. Snapshot the containers
        // first (RemoveVersion mutates the index); each RemoveVersion call
        // operates on the live index, not the snapshot, so consecutive calls on
        // the same container see the updated state.
        foreach (var container in _byId.Values.ToArray())
        {
            var orphans = container.Versions
                .Where(v => !referenced.Contains((container.Id, v.Folder)))
                .Select(v => v.Folder)
                .ToArray();
            foreach (var folder in orphans)
            {
                RemoveVersion(container.Id, folder);
            }
        }

        // Remove containers left with zero versions (manifest + dir). Snapshot
        // again; the index shrank above.
        foreach (var empty in _byId.Values.Where(c => c.Versions.Count == 0).ToArray())
        {
            _byId.Remove(empty.Id);
            if (empty.Source is UntrackedSource)
            {
                _untrackedByName.Remove(empty.Name);
            }
            var containerDir = ContainerDir(baseFolder, empty.Id);
            if (Directory.Exists(containerDir))
            {
                try
                {
                    Directory.Delete(containerDir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not delete empty container dir {Path}", containerDir);
                }
            }
            _logger.LogInformation("Pruned empty container {Id} ('{Name}')", empty.Id, empty.Name);
        }
    }

    /// <inheritdoc />
    public void Rescan()
    {
        // Read the live mods root (the path the config currently points at) and
        // rebuild the index from it. Clear first: RebuildIndex only adds, so
        // without a clear a container removed from disk between scans would
        // survive as a stale entry.
        var baseFolder = EnsureBaseFolder();
        _byId.Clear();
        _untrackedByName.Clear();
        RebuildIndex(baseFolder);
        _logger.LogInformation("Rescanned mods repository at {Path} ({Count} containers).", baseFolder, _byId.Count);
    }

    /// <inheritdoc />
    public void Relocate(string newBasePath)
    {
        ValidateNewBasePath(newBasePath);

        // Atomic contract: Relocate owns the whole move + config save + rescan
        // so a save failure can never strand the files at the new path with the
        // config still pointing at the old one. The OLD path is read live first
        // (before the save flips ModsFolder to the new path).
        var config = _configLoader.Load();
        var oldBasePath = config.ModsFolder;

        // No-op when the destination is the same as the source: the subsequent
        // save + Rescan would be a confusing no-op anyway, and the conflict
        // check below would otherwise always fire (the current root is full of
        // the indexed UUIDs).
        if (SamePath(oldBasePath, newBasePath))
        {
            _logger.LogInformation(
                "Relocate target {Path} is the current mods root; no move needed.", newBasePath);
            return;
        }

        // Refuse to relocate into a directory that already contains one of the
        // indexed container UUIDs: that would silently shadow an existing
        // container (the scan would pick up the pre-existing dir's manifest,
        // not the moved one). The caller picks a fresh or empty destination.
        foreach (var containerId in _byId.Keys)
        {
            var conflictDir = Path.Combine(newBasePath, containerId.ToString());
            if (Directory.Exists(conflictDir))
            {
                throw new InvalidOperationException(
                    $"Cannot relocate into '{newBasePath}': it already contains a directory " +
                    $"named '{containerId}', which is a container UUID the repository tracks.");
            }
        }

        Directory.CreateDirectory(newBasePath);

        // Volume strategy, detected once from the two base paths (it is not
        // per-container): same-volume keeps the fast, atomic directory rename
        // (Directory.Move); cross-volume copies the tree + deletes the source,
        // because Directory.Move throws IOException across volumes (e.g.
        // Windows C: -> D:) rather than falling back to a copy. Without this
        // branch, a cross-volume relocate would throw on every container, the
        // save would still flip ModsFolder, Rescan would rebuild against an
        // empty new path, and the containers would be stranded (invisible, no
        // UI recovery).
        var crossVolume = !_sameVolume(oldBasePath, newBasePath);

        // Best-effort per-container move: one locked directory must not abort
        // the rest. Each container dir is moved whole via MoveContainerDir,
        // which picks its strategy from the volume flag above. The ids that
        // actually moved are tracked so a save failure can roll exactly them
        // back; containers that fail to move remain under the old path (their
        // files are untouched, but the relocated index at the new path will
        // not include them).
        var movedIds = new List<Guid>();
        var failed = 0;
        foreach (var containerId in _byId.Keys.ToArray())
        {
            var sourceDir = Path.Combine(oldBasePath, containerId.ToString());
            var destDir = Path.Combine(newBasePath, containerId.ToString());
            try
            {
                MoveContainerDir(sourceDir, destDir, crossVolume);
                movedIds.Add(containerId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                _logger.LogError(
                    ex,
                    "Could not move container {Id} from {Source} to {Dest} during relocate; " +
                    "its files remain at the old location; the relocated index will not include this container.",
                    containerId, sourceDir, destDir);
            }
        }

        // Persist ModsFolder = newPath. Two failure modes: a thrown exception
        // (a loader that reports failure) OR a silent failure (the production
        // ConfigLoader.Save swallows write errors by design, for the best-
        // effort Preferences flow). Either one strands the moved files at the
        // new path while the config still points at the old one, so either
        // triggers a rollback: the moved container dirs go back to the old
        // path, files + config agree again, and the failure surfaces to the
        // caller. The catch is deliberately broad (any exception, not just
        // IO/auth): the rollback is safe to run on any save failure, and the
        // re-load verification below catches the silent-swallow case.
        config.ModsFolder = newBasePath;
        Exception? saveFailure = null;
        try
        {
            _configLoader.Save(config);
        }
        catch (Exception ex)
        {
            saveFailure = ex;
        }

        if (saveFailure is null && !SamePath(_configLoader.Load().ModsFolder, newBasePath))
        {
            // The save did not throw but also did not persist (the swallowing
            // loader's silent-failure mode). Fabricate the failure so the
            // rollback + rethrow path is shared.
            saveFailure = new IOException(
                $"Config save did not persist ModsFolder='{newBasePath}' (silent failure).");
        }

        if (saveFailure is not null)
        {
            RollbackMoves(movedIds, newBasePath, oldBasePath, crossVolume);
            _logger.LogWarning(
                saveFailure,
                "Relocate config save failed after moving {Count} container(s) to {New}; " +
                "rolled them back to {Old} so files + config agree.",
                movedIds.Count, newBasePath, oldBasePath);
            throw saveFailure;
        }

        _logger.LogInformation(
            "Relocated {Moved} container(s) from {Old} to {New} ({Failed} failed to move) " +
            "and persisted the new mods root.",
            movedIds.Count, oldBasePath, newBasePath, failed);

        // Rebuild the index at the new path, which is now the live config path.
        Rescan();
    }

    /// <summary>
    /// Moves the given container dirs <paramref name="fromBasePath"/> back to
    /// <paramref name="toBasePath"/> (best-effort per container). Used only by
    /// <see cref="Relocate"/> to undo the move when the config save fails, so
    /// files + config agree at the old path again. A rollback move failure is
    /// logged + skipped (the container's files stay where they are; an operator
    /// can reconcile by hand) so one locked dir does not abort the rest of the
    /// rollback. <paramref name="crossVolume"/> selects the same move strategy
    /// <see cref="Relocate"/> used for the forward move (the volume
    /// relationship is symmetric).
    /// </summary>
    private void RollbackMoves(IReadOnlyList<Guid> movedIds, string fromBasePath, string toBasePath, bool crossVolume)
    {
        foreach (var containerId in movedIds)
        {
            var src = Path.Combine(fromBasePath, containerId.ToString());
            var dst = Path.Combine(toBasePath, containerId.ToString());
            try
            {
                MoveContainerDir(src, dst, crossVolume);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    ex,
                    "Rollback: could not move container {Id} back from {Src} to {Dst}; " +
                    "its files remain at the source path and need manual reconciliation.",
                    containerId, src, dst);
            }
        }
    }

    /// <summary>
    /// Moves a single container directory from <paramref name="sourceDir"/> to
    /// <paramref name="destDir"/>. Same-volume is a fast, atomic
    /// <see cref="Directory.Move"/> (a directory rename); cross-volume copies
    /// the tree via <see cref="DirectoryCopy.Copy"/> + deletes the source,
    /// because <see cref="Directory.Move"/> throws <see cref="IOException"/>
    /// across volumes (e.g. Windows C: -&gt; D:) rather than falling back to a
    /// copy.
    /// </summary>
    private static void MoveContainerDir(string sourceDir, string destDir, bool crossVolume)
    {
        if (crossVolume)
        {
            DirectoryCopy.Copy(sourceDir, destDir);
            Directory.Delete(sourceDir, recursive: true);
        }
        else
        {
            Directory.Move(sourceDir, destDir);
        }
    }

    /// <summary>
    /// Determines whether two absolute paths share a volume root (so a
    /// <see cref="Directory.Move"/> rename is valid). On Windows the roots are
    /// drive letters (e.g. <c>C:\</c>, <c>D:\</c>); on Linux every absolute
    /// path shares <c>/</c>, so paths under one tree resolve as same-volume.
    /// The comparison is ordinal; case-insensitive on Windows (drive-letter
    /// case). A path whose root cannot be determined is treated as
    /// cross-volume so the safe copy + delete path runs.
    /// </summary>
    private static bool SameVolumeByRoot(string pathA, string pathB)
    {
        var rootA = Path.GetPathRoot(Path.GetFullPath(pathA));
        var rootB = Path.GetPathRoot(Path.GetFullPath(pathB));
        if (string.IsNullOrEmpty(rootA) || string.IsNullOrEmpty(rootB))
        {
            return false;
        }
        return string.Equals(
            rootA,
            rootB,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates the relocate target: non-null/whitespace, absolute, and its
    /// parent directory must be creatable (a reasonable proxy for "writable
    /// location"). Throws <see cref="ArgumentException"/> otherwise.
    /// </summary>
    private static void ValidateNewBasePath(string newBasePath)
    {
        if (string.IsNullOrWhiteSpace(newBasePath))
        {
            throw new ArgumentException("Relocate target path must not be null or whitespace.", nameof(newBasePath));
        }

        if (!Path.IsPathRooted(newBasePath))
        {
            throw new ArgumentException(
                $"Relocate target path must be absolute (received '{newBasePath}').", nameof(newBasePath));
        }

        var parent = Path.GetDirectoryName(newBasePath);
        if (string.IsNullOrEmpty(parent))
        {
            // A rooted path with no parent component (e.g. "/" on Linux, a bare
            // drive root on Windows): not a usable mods root.
            throw new ArgumentException(
                $"Relocate target path '{newBasePath}' has no parent directory.", nameof(newBasePath));
        }

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException(
                $"Relocate target parent directory '{parent}' cannot be created: {ex.Message}", nameof(newBasePath), ex);
        }
    }

    /// <summary>
    /// Ordinal, case-insensitive path equality after full-path normalization.
    /// Used to short-circuit a relocate-to-same-path as a no-op.
    /// </summary>
    private static bool SamePath(string a, string b)
    {
        var na = Path.GetFullPath(a);
        var nb = Path.GetFullPath(b);
        return string.Equals(na, nb, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // ---- index rebuild ------------------------------------------------------

    private void RebuildIndex(string baseFolder)
    {
        foreach (var dir in Directory.EnumerateDirectories(baseFolder))
        {
            var name = Path.GetFileName(dir);
            if (!Guid.TryParse(name, out var id))
            {
                _logger.LogDebug("Skipping non-container directory under mods root: {Dir}", dir);
                continue;
            }

            // Sweep orphan temp dirs (<versionFolder>.tmp.<guid>) left by a
            // prior crashed import into this container. Completes the per-
            // AddVersion reactive sweep into a full cleanup: an orphan in a
            // container that is never re-imported is reclaimed at the next
            // index build (construction/rescan) instead of lingering on disk.
            SweepOrphanTemps(dir);

            var manifest = ContainerManifestPath(baseFolder, id);
            if (!File.Exists(manifest))
            {
                _logger.LogDebug("Skipping container directory with no container.json: {Dir}", dir);
                continue;
            }

            ModContainer? container;
            try
            {
                using var stream = File.OpenRead(manifest);
                container = JsonSerializer.Deserialize<ModContainer>(stream);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A corrupt manifest must not break the rest of the index.
                _logger.LogError(ex, "Container manifest at {Path} is unreadable; skipping.", manifest);
                continue;
            }

            if (container is null || container.Id != id)
            {
                // Defensive: a hand-edited manifest whose id does not match its
                // directory is treated as unreadable (the directory is the
                // source of truth for the path; mismatched ids would shadow).
                _logger.LogError(
                    "Container manifest at {Path} has a missing or mismatched id (dir={Dir}); skipping.",
                    manifest, id);
                continue;
            }

            _byId[id] = container;
            IndexUntrackedName(container);
        }
    }

    private void IndexUntrackedName(ModContainer container)
    {
        if (container.Source is not UntrackedSource)
        {
            return;
        }

        // Two untracked containers with the same Name is an edge case (the
        // import service dedups, so it can only arise from a hand-edit). The
        // last one wins the index; FindUntrackedByName returns it, and the
        // other is still reachable via Get(id) + List(). Log so it's visible.
        if (_untrackedByName.TryGetValue(container.Name, out var prior) && prior != container.Id)
        {
            _logger.LogWarning(
                "Duplicate untracked container name '{Name}' (ids {Prior} + {Current}); index points at {Current}.",
                container.Name, prior, container.Id, container.Id);
        }
        _untrackedByName[container.Name] = container.Id;
    }

    // ---- manifest persistence ------------------------------------------------

    private void WriteContainer(ModContainer container, string baseFolder)
    {
        var dir = ContainerDir(baseFolder, container.Id);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(container, JsonOptions);
        File.WriteAllText(ContainerManifestPath(baseFolder, container.Id), json, ManifestEncoding);
    }

    /// <summary>
    /// Removes a prior version dir/file at <paramref name="path"/> so a re-import
    /// is a clean repopulate. Idempotent (missing target is a no-op). A file
    /// (not a dir) at the target is also removed: defends against weird prior
    /// state.
    /// </summary>
    private static void CleanTarget(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.Directory) != 0)
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }

    private void DeleteVersionDir(string baseFolder, Guid containerId, string versionFolder)
    {
        var dir = VersionDir(baseFolder, containerId, versionFolder);
        if (!Directory.Exists(dir) && !File.Exists(dir))
        {
            return;
        }

        try
        {
            CleanTarget(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete version folder {Path}", dir);
        }
    }

    // ---- path helpers (all internal-only - paths derive from the config root + ids) --

    private static string ContainerDir(string baseFolder, Guid containerId) =>
        Path.Combine(baseFolder, containerId.ToString());
    private static string VersionDir(string baseFolder, Guid containerId, string versionFolder) =>
        Path.Combine(ContainerDir(baseFolder, containerId), versionFolder);
    private static string ContainerManifestPath(string baseFolder, Guid containerId) =>
        Path.Combine(ContainerDir(baseFolder, containerId), VersionManifestFileName);
}
