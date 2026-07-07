using System.Text;
using System.Text.Json;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Profiles;

/// <summary>
/// Filesystem-backed <see cref="IProfileService"/>. Each profile lives under
/// <c>&lt;ProfilesBaseFolder&gt;/&lt;guid&gt;/</c> with this layout:
/// </summary>
/// <remarks>
/// <code>
/// &lt;ProfilesBaseFolder&gt;/          (auto-created on first run)
///   &lt;guid&gt;/                        (profile dir; id-named)
///     profile.json                   (metadata + mod list - the source of truth)
///     staged/                        (the staged mod root = the --mod-path;
///                                     REGENERATED each launch - a projection)
///       &lt;baseName&gt;                 (symlink -> &lt;versionFolder&gt;/&lt;baseName&gt;/)
///       mods.lst                     (successfully-staged enabled mods, in order)
/// </code>
/// <para>
/// A profile references mods by <see cref="ModListEntry.ContainerId"/>; it stores
/// no mod files. Staging resolves each enabled mod's
/// <see cref="ModVersionPolicy"/> against its <see cref="ModContainer"/> (via
/// <see cref="IModRepository"/>), discovers the mod's base folder inside the
/// resolved version folder, and symlinks <c>staged/&lt;baseName&gt;</c> to
/// <c>&lt;versionFolder&gt;/&lt;baseName&gt;/</c>. <b>The base name (not the
/// container's display name) is the link + mods.lst name</b>: mods bake their
/// folder name into their code, so the link must carry the base name for the
/// mod's hardcoded paths to resolve. Staging is a simple loop: base-name
/// collisions are blocked at import time (<see cref="GetBaseNameCollision"/>),
/// so staging never sees two mods with the same base folder name in normal use.
/// <b>Symlinks, never copies.</b> The repository holds the files;
/// <c>staged/</c> is a symlink projection.</para>
/// <para>
/// Registered as a singleton: the service holds no per-request state (all state
/// lives on disk). The profiles base folder is read live from
/// <see cref="IConfigLoader"/>.<see cref="IConfigLoader.Load"/> on each public
/// operation (one snapshot per op), so a runtime folder change via the upcoming
/// Settings window takes effect immediately. <see cref="Directory.CreateDirectory"/>
/// runs per-op (idempotent) on the live path. Concurrent writes to the same
/// profile are not coordinated (single-UI-thread assumption).</para>
/// </remarks>
internal sealed class ProfileService : IProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    // mods.lst is UTF-8 without BOM (the Lua loader reads it line-by-line; a
    // BOM would surface as a stray prefix on the first mod name).
    private static readonly Encoding ModListEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly IConfigLoader _configLoader;
    private readonly IModRepository _repo;
    private readonly SymlinkCreator _symlink;
    private readonly ILogger<ProfileService> _logger;

    /// <inheritdoc />
    public event EventHandler<ProfileSummary>? ProfileCreated;

    public ProfileService(
        IConfigLoader configLoader,
        IModRepository repo,
        SymlinkCreator symlink,
        ILogger<ProfileService> logger)
    {
        _configLoader = configLoader;
        _repo = repo;
        _symlink = symlink;
        _logger = logger;
    }

    /// <summary>
    /// Reads the profiles base folder from the live config snapshot and ensures
    /// it exists. Called at the top of each public operation so a runtime folder
    /// change takes effect immediately (the directory is created on the live
    /// path, and subsequent path helpers derive from it).
    /// </summary>
    private string EnsureBaseFolder()
    {
        var baseFolder = _configLoader.Load().ProfilesBaseFolder;
        // ProfilesBaseFolder is non-null by CuratorConfig contract (defaults to
        // <app-data>/profiles). Directory.CreateDirectory is idempotent, so this
        // makes every subsequent op first-run safe without each re-checking.
        Directory.CreateDirectory(baseFolder);
        return baseFolder;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProfileSummary> ListProfiles()
    {
        var baseFolder = EnsureBaseFolder();
        var summaries = new List<ProfileSummary>();
        foreach (var dir in Directory.EnumerateDirectories(baseFolder))
        {
            var name = Path.GetFileName(dir);
            if (!Guid.TryParse(name, out var id))
            {
                _logger.LogDebug("Skipping non-profile directory under profiles base: {Dir}", dir);
                continue;
            }

            try
            {
                var profile = ReadProfileFile(dir);
                summaries.Add(new ProfileSummary(id, profile.Name));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A single unreadable profile must not break listing the rest.
                _logger.LogWarning(ex, "Skipping unreadable profile at {Dir}", dir);
            }
        }

        // Predictable order for the UI profile picker: sort by Name, ordinal
        // (stable, so equal names keep enumeration order).
        return summaries.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public Profile GetProfile(Guid id)
    {
        var baseFolder = EnsureBaseFolder();
        return ReadProfileFile(ProfileDir(baseFolder, id)); // throws KeyNotFoundException via EnsureReadable
    }

    /// <inheritdoc />
    public Profile CreateProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Profile name must not be null or whitespace.", nameof(name));
        }

        var baseFolder = EnsureBaseFolder();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            Mods = Array.Empty<ModListEntry>(),
        };

        // Scaffold the profile dir + staged/ before persisting so a crash between
        // the two never leaves a profile.json without its tree. staged/ is
        // regenerated each PrepareModRoot.
        Directory.CreateDirectory(ProfileDir(baseFolder, profile.Id));
        Directory.CreateDirectory(StagedDir(baseFolder, profile.Id));
        WriteProfileFile(profile, baseFolder);

        _logger.LogInformation("Created profile {Id} ('{Name}')", profile.Id, profile.Name);

        // Notify subscribers (the DMF new-profile prompt coordinator). Raised
        // AFTER the persist committed so a subscriber that reads the profile
        // back sees it. Raised synchronously; subscribers are expected to defer
        // any UI work (the coordinator records the signal + processes it once
        // the owning dialog closes, to avoid a dialog-on-dialog).
        ProfileCreated?.Invoke(this, new ProfileSummary(profile.Id, profile.Name));

        return profile;
    }

    /// <inheritdoc />
    public void RenameProfile(Guid id, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Profile name must not be null or whitespace.", nameof(newName));
        }

        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        var previous = profile.Name;
        profile.Name = newName;
        WriteProfileFile(profile, baseFolder);

        _logger.LogInformation("Renamed profile {Id} '{Previous}' -> '{Name}'", id, previous, newName);
    }

    /// <inheritdoc />
    public void DeleteProfile(Guid id)
    {
        var baseFolder = EnsureBaseFolder();
        var dir = ProfileDir(baseFolder, id);
        if (!Directory.Exists(dir))
        {
            throw UnknownProfile(id);
        }

        Directory.Delete(dir, recursive: true);
        _logger.LogInformation("Deleted profile {Id}", id);
    }

    /// <inheritdoc />
    public IReadOnlyList<ModListEntry> GetModList(Guid id) => GetProfile(id).Mods;

    /// <inheritdoc />
    public void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder)
    {
        ArgumentNullException.ThrowIfNull(containerIdsInOrder);
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        var current = profile.Mods;

        // Index the desired order by containerId (first occurrence wins for dupes).
        var desiredIndex = new Dictionary<Guid, int>();
        for (var i = 0; i < containerIdsInOrder.Count; i++)
        {
            var cid = containerIdsInOrder[i];
            if (cid != Guid.Empty && !desiredIndex.ContainsKey(cid))
            {
                desiredIndex[cid] = i;
            }
        }

        // Stable sort: listed mods by their desired position first, then
        // unmentioned mods in their existing relative order. OrderBy is stable,
        // so equal keys keep storage order. Rebuild (immutable entries) with
        // renumbered Order.
        profile.Mods = current
            .OrderBy(m => desiredIndex.TryGetValue(m.ContainerId, out var idx) ? idx : int.MaxValue)
            .Select((m, i) => m with { Order = i })
            .ToList();
        WriteProfileFile(profile, baseFolder);
    }

    /// <inheritdoc />
    public void SetModEnabled(Guid id, Guid containerId, bool enabled)
    {
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        _ = profile.Mods.FirstOrDefault(m => m.ContainerId == containerId)
            ?? throw UnknownMod(id, containerId);

        // Rebuild (immutable entries): swap the matching entry for a copy with
        // the new Enabled. Write-through persists the whole aggregate.
        profile.Mods = profile.Mods
            .Select(m => m.ContainerId == containerId ? m with { Enabled = enabled } : m)
            .ToList();
        WriteProfileFile(profile, baseFolder);
    }

    /// <inheritdoc />
    public void AddMod(Guid id, Guid containerId, ModVersionPolicy policy)
    {
        if (containerId == Guid.Empty)
        {
            throw new ArgumentException("Container id must not be Guid.Empty.", nameof(containerId));
        }
        ArgumentNullException.ThrowIfNull(policy);

        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));

        // Idempotent: re-adding an existing container is a no-op (keeps its order,
        // enabled state, and policy). Prevents duplicate entries from re-entrancy.
        if (profile.Mods.Any(m => m.ContainerId == containerId))
        {
            return;
        }

        var nextOrder = profile.Mods.Count == 0 ? 0 : profile.Mods.Max(m => m.Order) + 1;
        profile.Mods = profile.Mods
            .Append(new ModListEntry { ContainerId = containerId, Enabled = true, Order = nextOrder, Policy = policy })
            .ToList();
        WriteProfileFile(profile, baseFolder);
    }

    /// <inheritdoc />
    public void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        _ = profile.Mods.FirstOrDefault(m => m.ContainerId == containerId)
            ?? throw UnknownMod(id, containerId);

        // Defense-in-depth: a PinnedPolicy must reference a version that exists
        // in the container. The UI's pin dropdown can only produce ids the
        // container already holds, but a programmatic call (or an id held stale
        // across a repository change) must not silently create a phantom pin
        // that skips+warns at every stage. LatestPolicy needs no check: it
        // resolves dynamically to whatever the container currently marks
        // IsLatest.
        if (policy is PinnedPolicy pinned)
        {
            var container = _repo.Get(containerId);
            if (container is null || !container.Versions.Any(v => v.Folder == pinned.VersionId))
            {
                throw new ArgumentException(
                    $"No version with id '{pinned.VersionId}' exists on container '{containerId}'. " +
                    "A Pinned policy must reference a present version.",
                    nameof(policy));
            }
        }

        // Persist the new policy. Resolution happens at stage time, so there's
        // no on-disk transition (no diverged copy to reconcile).
        profile.Mods = profile.Mods
            .Select(m => m.ContainerId == containerId ? m with { Policy = policy } : m)
            .ToList();
        WriteProfileFile(profile, baseFolder);
        _logger.LogInformation("Set policy for container {Container} on profile {Id} to {Policy}", containerId, id, policy);
    }

    /// <inheritdoc />
    public void RemoveMod(Guid id, Guid containerId)
    {
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        _ = profile.Mods.FirstOrDefault(m => m.ContainerId == containerId)
            ?? throw UnknownMod(id, containerId);

        profile.Mods = profile.Mods.Where(m => m.ContainerId != containerId).ToList();
        WriteProfileFile(profile, baseFolder);

        // The repository copy is NOT touched: other profiles may still reference
        // it, and the startup prune reclaims it when no profile does.
    }

    /// <inheritdoc />
    public string PrepareModRoot(Guid id)
    {
        var baseFolder = EnsureBaseFolder();
        var profile = ReadProfileFile(ProfileDir(baseFolder, id));
        var staged = StagedDir(baseFolder, id);

        // Regenerated each launch: clear the prior projection, then rebuild from
        // the current resolution. ClearStagedDir is symlink-aware (never follows
        // a symlink into the repository - see the method).
        ClearStagedDir(staged);
        Directory.CreateDirectory(staged);

        // Resolve each enabled mod in Order; create the symlink for those that
        // resolve to a present version folder. mods.lst reflects what actually
        // got staged (a skipped mod has no entry in staged/ and must not be
        // listed - otherwise the loader would look for a mod dir that isn't
        // there).
        //
        // This is a SIMPLE loop: base-name collisions are blocked at import time
        // (the add flow calls GetBaseNameCollision), so staging never sees two
        // mods with the same base folder name in normal use. No dedupe / no
        // last-wins / no disambiguation. (A hand-edited profile.json that somehow
        // creates a duplicate base name would throw SymlinkStagingException here
        // on the second link - an accepted edge; no defensive logic is added.)
        var stagedNames = new List<string>();
        foreach (var mod in profile.Mods.Where(m => m.Enabled).OrderBy(m => m.Order))
        {
            var (baseName, target, skipReason) = ResolveStagingTarget(mod);
            if (baseName is null || target is null)
            {
                // The mod couldn't be resolved to a stageable base folder
                // (missing container/version, missing version folder, or a
                // corrupted version folder with zero/multiple subdirs). Skip +
                // warn; it has no entry in staged/ or mods.lst.
                _logger.LogWarning(
                    "Mod {Container} on profile {Id} could not be staged ({Reason}). Skipping.",
                    mod.ContainerId, id, skipReason);
                continue;
            }

            var linkPath = Path.Combine(staged, baseName);
            CreateSymlinkOrThrow(linkPath, target);
            stagedNames.Add(baseName);
            _logger.LogDebug(
                "Staged container {Container} on profile {Id} as '{Link}' -> {Target}",
                mod.ContainerId, id, baseName, target);
        }

        WriteModList(stagedNames, staged);
        _logger.LogInformation("Staged {Count} mod(s) for profile {Id} at {Path}", stagedNames.Count, id, staged);
        return staged;
    }

    // ---- staging helpers ----------------------------------------------------

    /// <summary>
    /// Resolves a profile mod entry to its on-disk staging target: the mod's base
    /// folder name + the absolute symlink target (<c>&lt;versionFolder&gt;/&lt;baseName&gt;/</c>).
    /// Returns a non-null <c>SkipReason</c> (and null base name + target) when the
    /// entry can't be staged. Pure: no logging, no side effects. Shared by
    /// <see cref="PrepareModRoot"/> (staging, warns on skip) and
    /// <see cref="GetBaseNameCollision"/> (silent), so the two paths cannot drift.
    /// </summary>
    /// <remarks>
    /// The base name is <b>not stored</b>; it is derived from the validated
    /// on-disk structure (the single subdirectory inside the version folder,
    /// which the import validation guarantees). Mods bake their folder name into
    /// their code, so the symlink MUST carry the base name (not the container's
    /// display name) for the mod's hardcoded paths to resolve. A version folder
    /// with zero or multiple subdirs (corrupted / legacy data predating the
    /// import validation) can't yield a base name and is skipped.
    /// </remarks>
    private (string? BaseName, string? Target, string? SkipReason) ResolveStagingTarget(ModListEntry mod)
    {
        var container = _repo.Get(mod.ContainerId);
        if (container is null)
        {
            return (null, null, "container not found");
        }

        var version = container.ResolveVersion(mod.Policy);
        if (version is null)
        {
            return (null, null, $"no version resolves for policy {mod.Policy}");
        }

        var versionFolder = _repo.GetVersionFolderPath(mod.ContainerId, version.Folder);
        if (!Directory.Exists(versionFolder))
        {
            // Defensive: the manifest points at a folder that is not on disk
            // (a hand-delete between prune + stage).
            return (null, null, $"version folder {version.Folder} is missing on disk");
        }

        // Discover the mod's base folder: the import validation guarantees the
        // version folder contains exactly one subdirectory (the base, named to
        // match its <base>.mod descriptor). A corrupted/inconsistent version
        // folder (zero/multiple subdirs) can't yield a base name.
        string[] baseDirs;
        try
        {
            baseDirs = Directory.GetDirectories(versionFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null, $"version folder {version.Folder} is not readable");
        }

        if (baseDirs.Length != 1)
        {
            return (null, null,
                $"version folder {version.Folder} has {baseDirs.Length} subdirectories; expected exactly one base folder");
        }

        var baseName = Path.GetFileName(baseDirs[0]);
        // The symlink target is the base folder inside the version folder:
        // <versionFolder>/<baseName>/.
        return (baseName, Path.Combine(versionFolder, baseName), null);
    }

    /// <inheritdoc />
    public ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new ArgumentException("Base name must not be null or whitespace.", nameof(baseName));
        }

        // Throws KeyNotFoundException via GetProfile if the profile is unknown.
        var profile = GetProfile(id);

        // Consider ALL mods (enabled + disabled): a disabled colliding mod could
        // be enabled later. excludeContainerId skips a re-add of the same
        // container (AddMod is idempotent on it, so a re-add is a no-op, not a
        // collision). A mod whose base name can't be resolved (missing
        // container/version/corrupted folder) is skipped silently: it can't
        // collide. Base-name comparison is ordinal (folder names are case-sensitive
        // on Linux; an ordinal match is the conservative choice cross-platform).
        foreach (var mod in profile.Mods)
        {
            if (excludeContainerId is Guid exclude && mod.ContainerId == exclude)
            {
                continue;
            }

            var (resolved, _, _) = ResolveStagingTarget(mod);
            if (resolved is not null && string.Equals(resolved, baseName, StringComparison.Ordinal))
            {
                return mod;
            }
        }
        return null;
    }

    /// <summary>
    /// Creates a symlink, throwing <see cref="SymlinkStagingException"/> with a
    /// clear, actionable message on failure. Never silently copies.
    /// </summary>
    private void CreateSymlinkOrThrow(string linkPath, string targetPath)
    {
        try
        {
            _symlink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SymlinkStagingException(
                $"Failed to create symlink '{linkPath}' -> '{targetPath}'. Symlinks are required for mod staging " +
                "(the manager never copies). On Windows, enable Developer Mode or run the manager as administrator; " +
                "on Linux, confirm write access to the profile's staged/ directory.",
                ex);
        }
    }

    /// <summary>
    /// Clears <c>staged/</c> for a rebuild: <b>symlink-aware</b>. It removes
    /// each top-level entry via <see cref="DeleteStagedEntry"/>, which deletes
    /// symlinks as links (never following them into the repository). This is
    /// data-safety-critical: a naive
    /// <c>Directory.Delete(staged, recursive: true)</c> could follow a directory
    /// symlink and delete the repository's mod files.
    /// </summary>
    private void ClearStagedDir(string staged)
    {
        if (!Directory.Exists(staged))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(staged))
        {
            DeleteStagedEntry(entry);
        }
    }

    /// <summary>
    /// Deletes a single staged entry, <b>symlink-aware</b>: a reparse point
    /// (file or directory symlink) is removed as a link only, never followed
    /// into the repository. A real directory is recursed; a real file is deleted.
    /// This is data-safety-critical: the staged root holds symlinks into the
    /// repository, so a naive recursive delete would follow them and destroy the
    /// mod files.
    /// </summary>
    /// <remarks>
    /// The delete API must match the link's kind, or Windows throws:
    /// <list type="bullet">
    /// <item><description>Directory symlink (ReparsePoint + Directory):
    /// <see cref="Directory.Delete(string)"/>. On Windows,
    /// <see cref="File.Delete(string)"/> on a directory (incl. a dir-symlink)
    /// throws <see cref="UnauthorizedAccessException"/> ("Access denied"; Windows
    /// surfaces "is a directory" via the file-delete API as access-denied).
    /// <see cref="Directory.Delete(string)"/> on a reparse point removes the
    /// point itself, NOT the target, so it stays data-safe on both platforms.
    /// </description></item>
    /// <item><description>File symlink (ReparsePoint, not Directory):
    /// <see cref="File.Delete(string)"/>.</description></item>
    /// </list>
    /// </remarks>
    private static void DeleteStagedEntry(string entry)
    {
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(entry);
        }
        catch (FileNotFoundException)
        {
            return; // raced away; nothing to delete
        }
        catch (DirectoryNotFoundException)
        {
            return; // raced away; nothing to delete
        }

        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            if ((attrs & FileAttributes.Directory) != 0)
            {
                Directory.Delete(entry); // directory symlink -> remove the link only
            }
            else
            {
                File.Delete(entry);      // file symlink -> remove the link only
            }
        }
        else if ((attrs & FileAttributes.Directory) != 0)
        {
            Directory.Delete(entry, recursive: true); // real directory -> recurse
        }
        else
        {
            File.Delete(entry);                        // real file (mods.lst, etc.)
        }
    }

    // ---- mods.lst generation ------------------------------------------------

    private void WriteModList(List<string> stagedNames, string stagedRoot)
    {
        // The successfully-staged enabled mods, in Order. Faithful to what's in
        // staged/ (skipped mods are absent here too). No DMF-first enforcement,
        // no auto-sort (those are higher-layer concerns).
        var sb = new StringBuilder();
        foreach (var name in stagedNames)
        {
            sb.Append(name).Append('\n');
        }

        File.WriteAllText(ModListPath(stagedRoot), sb.ToString(), ModListEncoding);
    }

    // ---- persistence helpers ------------------------------------------------

    private Profile ReadProfileFile(string profileDir)
    {
        var file = ProfileFilePath(profileDir);
        EnsureReadable(file, profileDir);
        using var stream = File.OpenRead(file);
        var profile = JsonSerializer.Deserialize<Profile>(stream) ?? new Profile();

        // System.Text.Json can leave a non-nullable property as null if the
        // file explicitly carries null (e.g. a hand-edit). Coerce Mods so
        // downstream enumeration never NRE.
        profile.Mods ??= Array.Empty<ModListEntry>();

        // Fresh-start tolerance + null-Policy coercion. Two passes:
        //   - drop entries whose ContainerId is Guid.Empty (a legacy entry
        //     deserialized without its container id; the spec is fresh-
        //     start, so these are dropped + logged, not migrated);
        //   - coerce a null Policy to Latest (a hand-edit, or a legacy entry).
        if (profile.Mods.Any(m => m.Policy is null))
        {
            profile.Mods = profile.Mods
                .Select(m => m.Policy is null ? m with { Policy = ModVersionPolicy.Latest } : m)
                .ToList();
        }

        var dropped = profile.Mods.Where(m => m.ContainerId == Guid.Empty).ToList();
        if (dropped.Count > 0)
        {
            _logger.LogWarning(
                "Dropped {Count} legacy mod entries from profile at {Dir} (no ContainerId; legacy shape). " +
                "The spec is fresh-start: re-add mods through the import flow.",
                dropped.Count, profileDir);
            profile.Mods = profile.Mods.Where(m => m.ContainerId != Guid.Empty).ToList();
        }

        // Fresh-start tolerance: a legacy pinned entry (the pre-versionId shape)
        // carries a $kind:"pinned" Policy whose JSON has a "Version" tag string.
        // Under the new shape that property is unrecognized and skipped, leaving
        // the deserialized PinnedPolicy's VersionId empty. A PinnedPolicy with
        // an empty VersionId is a phantom pin (no version resolves); drop it +
        // log so the entry is re-added and re-pinned through the import flow.
        // Same fresh-start posture as the ContainerId drop above.
        var droppedPhantomPins = profile.Mods
            .Where(m => m.Policy is PinnedPolicy p && string.IsNullOrEmpty(p.VersionId))
            .ToList();
        if (droppedPhantomPins.Count > 0)
        {
            _logger.LogWarning(
                "Dropped {Count} phantom-pinned mod entries from profile at {Dir} (empty VersionId; legacy pinned shape). " +
                "The spec is fresh-start: re-pin mods through the policy dropdown.",
                droppedPhantomPins.Count, profileDir);
            profile.Mods = profile.Mods
                .Where(m => !(m.Policy is PinnedPolicy p && string.IsNullOrEmpty(p.VersionId)))
                .ToList();
        }

        return profile;
    }

    private static void EnsureReadable(string file, string profileDir)
    {
        if (!Directory.Exists(profileDir) || !File.Exists(file))
        {
            throw new KeyNotFoundException($"No profile exists at '{profileDir}'.");
        }
    }

    private void WriteProfileFile(Profile profile, string baseFolder)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(ProfileFilePath(ProfileDir(baseFolder, profile.Id)), json, ModListEncoding);
    }

    // ---- path helpers (all internal-only - never leak through the interface) --

    private static string ProfileDir(string baseFolder, Guid id) => Path.Combine(baseFolder, id.ToString());
    private static string ProfileFilePath(string profileDir) => Path.Combine(profileDir, "profile.json");
    private static string StagedDir(string baseFolder, Guid id) => Path.Combine(ProfileDir(baseFolder, id), "staged");
    private static string ModListPath(string stagedRoot) => Path.Combine(stagedRoot, "mods.lst");

    private static KeyNotFoundException UnknownProfile(Guid id) =>
        new($"No profile exists with id '{id}'.");

    private static KeyNotFoundException UnknownMod(Guid id, Guid containerId) =>
        new($"Profile '{id}' has no mod with container id '{containerId}'.");
}
