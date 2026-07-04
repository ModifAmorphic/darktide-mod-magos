using System.Text;
using System.Text.Json;
using Magos.Modificus.Config;
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
/// Registered as a singleton: it holds the in-memory index (cheap to rebuild),
/// and <see cref="MagosConfig"/> (its only config source) is itself a singleton.
/// Concurrent writes are not coordinated (single-UI-thread assumption).</para>
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

    private readonly string _baseFolder;
    private readonly ILogger<ModRepository> _logger;

    // Primary index: containerId -> container. Source identity lookups are
    // served by scanning this (cheap for dozens of containers); the untracked-
    // name index below is the only dedicated secondary index (untracked dedup
    // happens on every import).
    private readonly Dictionary<Guid, ModContainer> _byId = new();

    // Untracked-name -> containerId (only untracked containers are entered).
    // Nexus/GitHub lookups scan _byId (identity is fully on the source record).
    private readonly Dictionary<string, Guid> _untrackedByName = new(StringComparer.Ordinal);

    public ModRepository(MagosConfig config, ILogger<ModRepository> logger)
    {
        // ModsFolder is non-null by MagosConfig contract (defaults to
        // <app-data>/mods). Directory.CreateDirectory is idempotent, so
        // this makes every subsequent op first-run safe without each re-checking.
        _baseFolder = config.ModsFolder;
        _logger = logger;
        Directory.CreateDirectory(_baseFolder);
        RebuildIndex();
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

        var container = new ModContainer
        {
            Id = Guid.NewGuid(),
            Source = source,
            Name = name,
            Versions = Array.Empty<ModVersion>(),
        };

        Directory.CreateDirectory(ContainerDir(container.Id));
        WriteContainer(container);

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

        var containerDir = ContainerDir(containerId);
        Directory.CreateDirectory(containerDir);

        var existing = container.Versions.FirstOrDefault(v =>
            string.Equals(v.VersionString, versionString, StringComparison.Ordinal));

        List<ModVersion> versions;
        if (existing is not null)
        {
            // Dedup: reuse the existing folder. Clean + repopulate so a re-import
            // refreshes the files; leave the version entry (Folder, VersionString,
            // IsLatest, ImportedAt) unchanged so the manifest ordering stays
            // stable. (Re-importing a version is a file refresh, not a re-order.)
            var versionDir = VersionDir(containerId, existing.Folder);
            CleanTarget(versionDir);
            populateFolder(versionDir);
            versions = container.Versions.ToList();
            _logger.LogInformation(
                "Re-imported version '{Version}' on container {Id} (folder reused: {Folder})",
                versionString, containerId, existing.Folder);
        }
        else
        {
            // New version: new opaque folder + new entry stamped now; the new
            // entry is the newest by ImportedAt, so it becomes IsLatest and the
            // flag is cleared on every other version.
            var folder = Guid.NewGuid().ToString("N");
            var versionDir = VersionDir(containerId, folder);
            Directory.CreateDirectory(versionDir);
            populateFolder(versionDir);

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
        WriteContainer(updated);
        return updated;
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
        WriteContainer(updated);

        // Best-effort: drop the on-disk folder. A missing / unwritable folder
        // is logged + swallowed so a prune never crashes on a stray lock.
        DeleteVersionDir(containerId, versionFolder);
        _logger.LogInformation(
            "Removed version folder '{Folder}' from container {Id}", versionFolder, containerId);
    }

    /// <inheritdoc />
    public string GetVersionFolderPath(Guid containerId, string versionFolder)
    {
        ArgumentNullException.ThrowIfNull(versionFolder);
        return VersionDir(containerId, versionFolder);
    }

    /// <inheritdoc />
    public void PruneUnreferenced(IReadOnlySet<(Guid ContainerId, string VersionFolder)> referenced)
    {
        ArgumentNullException.ThrowIfNull(referenced);

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
            var containerDir = ContainerDir(empty.Id);
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

    // ---- index rebuild ------------------------------------------------------

    private void RebuildIndex()
    {
        foreach (var dir in Directory.EnumerateDirectories(_baseFolder))
        {
            var name = Path.GetFileName(dir);
            if (!Guid.TryParse(name, out var id))
            {
                _logger.LogDebug("Skipping non-container directory under mods root: {Dir}", dir);
                continue;
            }

            var manifest = ContainerManifestPath(id);
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

    private void WriteContainer(ModContainer container)
    {
        var dir = ContainerDir(container.Id);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(container, JsonOptions);
        File.WriteAllText(ContainerManifestPath(container.Id), json, ManifestEncoding);
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

    private void DeleteVersionDir(Guid containerId, string versionFolder)
    {
        var dir = VersionDir(containerId, versionFolder);
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

    private string ContainerDir(Guid containerId) => Path.Combine(_baseFolder, containerId.ToString());
    private string VersionDir(Guid containerId, string versionFolder) =>
        Path.Combine(ContainerDir(containerId), versionFolder);
    private string ContainerManifestPath(Guid containerId) =>
        Path.Combine(ContainerDir(containerId), VersionManifestFileName);
}
