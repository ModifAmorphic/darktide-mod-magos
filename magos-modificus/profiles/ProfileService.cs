using System.Text;
using System.Text.Json;
using Magos.Modificus.Config;
using Magos.Modificus.SharedMods;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Profiles;

/// <summary>
/// Filesystem-backed <see cref="IProfileService"/>. Each profile lives under
/// <c>&lt;ProfilesBaseFolder&gt;/&lt;guid&gt;/</c> with this layout:
/// </summary>
/// <remarks>
/// <code>
/// &lt;ProfilesBaseFolder&gt;/          (auto-created on first run)
///   &lt;guid&gt;/                        (profile dir; id-named)
///     profile.json                   (metadata + mod list — the source of truth)
///     mods/&lt;mod&gt;/                    (a profile's diverged copy of a mod, if any)
///     staged/                        (the staged mod root = the --mod-path;
///                                     REGENERATED each launch — a projection)
///       &lt;mod&gt;                       (symlink → shared &lt;mod&gt; OR mods/&lt;mod&gt;)
///       mods.lst                     (successfully-staged enabled mods, in order)
/// </code>
/// <para>
/// Staging is shared-first: each enabled mod resolves Share/Diverge against the
/// shared store (<see cref="ISharedModStore"/>); Share symlinks into the shared
/// store, Diverge symlinks into <c>mods/&lt;mod&gt;/</c> (skipped + warned if
/// absent — Phase 4 populates it). <b>Symlinks, never copies.</b> The shared
/// store + <c>mods/</c> hold the files; <c>staged/</c> is a symlink projection.</para>
/// <para>
/// Registered as a singleton: the service holds no per-request state — all
/// state lives on disk, and <see cref="MagosConfig"/> (its only config source)
/// is itself a singleton. Concurrent writes to the same profile are not
/// coordinated in Phase 2 (single-UI-thread assumption).</para>
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

    private readonly string _baseFolder;
    private readonly ISharedModStore _sharedStore;
    private readonly SymlinkCreator _symlink;
    private readonly ILogger<ProfileService> _logger;

    public ProfileService(
        MagosConfig config,
        ISharedModStore sharedStore,
        SymlinkCreator symlink,
        ILogger<ProfileService> logger)
    {
        // ProfilesBaseFolder is non-null by MagosConfig contract (defaults to
        // <app-data>/profiles). Directory.CreateDirectory is idempotent, so this
        // makes every subsequent op first-run safe without each one re-checking.
        _baseFolder = config.ProfilesBaseFolder;
        _sharedStore = sharedStore;
        _symlink = symlink;
        _logger = logger;
        Directory.CreateDirectory(_baseFolder);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProfileSummary> ListProfiles()
    {
        var summaries = new List<ProfileSummary>();
        foreach (var dir in Directory.EnumerateDirectories(_baseFolder))
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
        var dir = ProfileDir(id);
        return ReadProfileFile(dir); // throws KeyNotFoundException via EnsureReadable
    }

    /// <inheritdoc />
    public Profile CreateProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Profile name must not be null or whitespace.", nameof(name));
        }

        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            Mods = Array.Empty<ModListEntry>(),
        };

        // Scaffold the profile dir + staged/ + mods/ before persisting so a
        // crash between the two never leaves a profile.json without its tree.
        // staged/ is regenerated each PrepareModRoot; mods/ is populated by
        // Phase 4 — both pre-created for a predictable first-run shape.
        Directory.CreateDirectory(ProfileDir(profile.Id));
        Directory.CreateDirectory(StagedDir(profile.Id));
        Directory.CreateDirectory(ProfileModsDir(profile.Id));
        WriteProfileFile(profile);

        _logger.LogInformation("Created profile {Id} ('{Name}')", profile.Id, profile.Name);
        return profile;
    }

    /// <inheritdoc />
    public void RenameProfile(Guid id, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Profile name must not be null or whitespace.", nameof(newName));
        }

        var profile = GetProfile(id);
        var previous = profile.Name;
        profile.Name = newName;
        WriteProfileFile(profile);

        _logger.LogInformation("Renamed profile {Id} '{Previous}' -> '{Name}'", id, previous, newName);
    }

    /// <inheritdoc />
    public void DeleteProfile(Guid id)
    {
        var dir = ProfileDir(id);
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
    public void SetModOrder(Guid id, IReadOnlyList<string> modNamesInOrder)
    {
        var profile = GetProfile(id);
        var current = profile.Mods;

        // Index the desired order by name (first occurrence wins for dupes).
        var desiredIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < modNamesInOrder.Count; i++)
        {
            if (modNamesInOrder[i] is { Length: > 0 } n && !desiredIndex.ContainsKey(n))
            {
                desiredIndex[n] = i;
            }
        }

        // Stable sort: listed mods by their desired position first, then
        // unmentioned mods in their existing relative order. OrderBy is stable,
        // so equal keys keep storage order. Rebuild (immutable entries) with
        // renumbered Order.
        profile.Mods = current
            .OrderBy(m => desiredIndex.TryGetValue(m.Name, out var idx) ? idx : int.MaxValue)
            .Select((m, i) => m with { Order = i })
            .ToList();
        WriteProfileFile(profile);
    }

    /// <inheritdoc />
    public void SetModEnabled(Guid id, string modName, bool enabled)
    {
        var profile = GetProfile(id);
        _ = profile.Mods.FirstOrDefault(m => string.Equals(m.Name, modName, StringComparison.Ordinal))
            ?? throw UnknownMod(id, modName);

        // Rebuild (immutable entries): swap the matching entry for a copy with
        // the new Enabled. Write-through persists the whole aggregate.
        profile.Mods = profile.Mods
            .Select(m => string.Equals(m.Name, modName, StringComparison.Ordinal) ? m with { Enabled = enabled } : m)
            .ToList();
        WriteProfileFile(profile);
    }

    /// <inheritdoc />
    public void AddMod(Guid id, string modName) => AddMod(id, modName, ModVersionPolicy.Latest);

    /// <inheritdoc />
    public void AddMod(Guid id, string modName, ModVersionPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(modName))
        {
            throw new ArgumentException("Mod name must not be null or whitespace.", nameof(modName));
        }
        ArgumentNullException.ThrowIfNull(policy);

        var profile = GetProfile(id);

        // Idempotent: re-adding an existing mod is a no-op (keeps its order,
        // enabled state, and policy). Prevents duplicate entries from re-entrancy.
        if (profile.Mods.Any(m => string.Equals(m.Name, modName, StringComparison.Ordinal)))
        {
            return;
        }

        var nextOrder = profile.Mods.Count == 0 ? 0 : profile.Mods.Max(m => m.Order) + 1;
        profile.Mods = profile.Mods
            .Append(new ModListEntry { Name = modName, Enabled = true, Order = nextOrder, Policy = policy })
            .ToList();
        WriteProfileFile(profile);
    }

    /// <inheritdoc />
    public void SetModPolicy(Guid id, string modName, ModVersionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var profile = GetProfile(id);
        var entry = profile.Mods.FirstOrDefault(m => string.Equals(m.Name, modName, StringComparison.Ordinal))
            ?? throw UnknownMod(id, modName);

        // Persist the new policy first (metadata), then reconcile the diverged
        // copy against the shared store's resolution.
        profile.Mods = profile.Mods
            .Select(m => string.Equals(m.Name, modName, StringComparison.Ordinal) ? m with { Policy = policy } : m)
            .ToList();
        WriteProfileFile(profile);

        ReconcileDivergedCopy(id, modName, policy);
        _logger.LogInformation("Set policy for {Mod} on profile {Id} to {Policy}", modName, id, policy);
    }

    /// <inheritdoc />
    public void RemoveMod(Guid id, string modName)
    {
        var profile = GetProfile(id);
        var entry = profile.Mods.FirstOrDefault(m => string.Equals(m.Name, modName, StringComparison.Ordinal))
            ?? throw UnknownMod(id, modName);

        profile.Mods = profile.Mods.Where(m => !string.Equals(m.Name, modName, StringComparison.Ordinal)).ToList();
        WriteProfileFile(profile);

        // Drop the profile's diverged copy, if any (best-effort; never throws —
        // a missing dir is the normal case for a shared mod). The shared-store
        // copy is NOT touched (other profiles may still share it).
        var divergedModDir = ProfileModDir(id, modName);
        if (Directory.Exists(divergedModDir))
        {
            try
            {
                Directory.Delete(divergedModDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove diverged copy for {Mod} on profile {Id}", modName, id);
            }
        }
    }

    /// <inheritdoc />
    public string PrepareModRoot(Guid id)
    {
        var profile = GetProfile(id);
        var staged = StagedDir(id);

        // Regenerated each launch: clear the prior projection, then rebuild from
        // the current resolution. ClearStagedDir is symlink-aware (never follows
        // a symlink into the shared store — see the method).
        ClearStagedDir(staged);
        Directory.CreateDirectory(staged);

        // Resolve each enabled mod in Order; create the symlink for those that
        // resolve to a present target. mods.lst reflects what actually got
        // staged (a skipped mod has no entry in staged/ and must not be listed —
        // otherwise the loader would look for a mod dir that isn't there).
        var stagedNames = new List<string>();
        foreach (var mod in profile.Mods.Where(m => m.Enabled).OrderBy(m => m.Order))
        {
            var (target, resolve) = ResolveStagingTarget(id, mod.Name, mod.Policy);
            if (target is null)
            {
                // Diverge + no diverged copy yet (Phase 4 hasn't acquired it) →
                // skip + warn. The mod simply isn't in the staged root.
                _logger.LogWarning(
                    "Mod {Mod} on profile {Id} could not be staged (no shared copy, and mods/ copy absent — acquisition pending). Skipping.",
                    mod.Name, id);
                continue;
            }

            var linkPath = Path.Combine(staged, mod.Name);
            // A duplicate name can't arise through AddMod (idempotent) but a
            // hand-edited profile.json could carry one. The first occurrence
            // already created the symlink; later occurrences reuse it (graceful —
            // never crash on weird stored state; the name is listed per entry,
            // faithful to the Phase 1 contract).
            if (!Directory.Exists(linkPath) && !File.Exists(linkPath))
            {
                CreateSymlinkOrThrow(linkPath, target);
            }
            stagedNames.Add(mod.Name);
            _logger.LogDebug("Staged {Mod} on profile {Id} via {Resolve} -> {Target}", mod.Name, id, resolve, target);
        }

        WriteModList(stagedNames, staged);
        _logger.LogInformation("Staged {Count} mod(s) for profile {Id} at {Path}", stagedNames.Count, id, staged);
        return staged;
    }

    // ---- staging helpers ----------------------------------------------------

    /// <summary>
    /// Resolves the symlink target for a profile mod. Returns (target, label):
    /// a non-null target for Share (always, per the manifest) or Diverge-when-
    /// diverged-copy-present; null when Diverge-when-copy-absent (skip + warn).
    /// </summary>
    private (string? Target, string Resolve) ResolveStagingTarget(Guid id, string modName, ModVersionPolicy profilePolicy)
    {
        var shared = _sharedStore.Get(modName);

        // No shared entry → there's nothing to share. The profile can only use a
        // local/diverged copy (if present); else it's skipped.
        if (shared is null)
        {
            return DivergeTarget(id, modName);
        }

        var resolution = AllocationResolver.Resolve(shared.Policy, shared.ActualVersion, profilePolicy);
        if (resolution == AllocationResolution.Share)
        {
            // Share: trust the manifest — the files are at shared.Path (Phase 4's
            // job to place them; Add assumes they're there).
            return (shared.Path, nameof(AllocationResolution.Share));
        }

        return DivergeTarget(id, modName);
    }

    private (string? Target, string Resolve) DivergeTarget(Guid id, string modName)
    {
        var divergedModDir = ProfileModDir(id, modName);
        return Directory.Exists(divergedModDir)
            ? (divergedModDir, nameof(AllocationResolution.Diverge))
            : (null, nameof(AllocationResolution.Diverge));
    }

    /// <summary>
    /// Drops the profile's diverged copy when a policy change converges the mod
    /// back to Share (the shared copy is used again). Keeps it (no-op) when
    /// diverging or when there's no shared entry to converge to.
    /// </summary>
    private void ReconcileDivergedCopy(Guid id, string modName, ModVersionPolicy profilePolicy)
    {
        var shared = _sharedStore.Get(modName);
        if (shared is null)
        {
            // No shared entry → can't converge to Share; leave mods/ as-is
            // (acquisition will populate the store later).
            _logger.LogDebug(
                "Mod {Mod} on profile {Id} has no shared entry; mods/ left as-is.", modName, id);
            return;
        }

        var resolution = AllocationResolver.Resolve(shared.Policy, shared.ActualVersion, profilePolicy);
        if (resolution != AllocationResolution.Share)
        {
            // Still diverged (or just diverged) — mods/ is needed (or will be,
            // once Phase 4 acquires it). Nothing to drop.
            return;
        }

        // diverge → share: drop the local copy to reclaim space. Best-effort;
        // a missing dir (never diverged / not yet acquired) is a no-op.
        var divergedModDir = ProfileModDir(id, modName);
        if (Directory.Exists(divergedModDir))
        {
            try
            {
                Directory.Delete(divergedModDir, recursive: true);
                _logger.LogInformation(
                    "Mod {Mod} on profile {Id} converged to Share; dropped mods/ copy.", modName, id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex,
                    "Could not remove diverged copy for {Mod} on profile {Id} on converge.", modName, id);
            }
        }
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
                $"Failed to create symlink '{linkPath}' -> '{targetPath}'. Symlinks are required for shared-mod staging " +
                "(the manager never copies). On Windows, enable Developer Mode or run the manager as administrator; " +
                "on Linux, confirm write access to the profile's staged/ directory.",
                ex);
        }
    }

    /// <summary>
    /// Clears <c>staged/</c> for a rebuild — <b>symlink-aware</b>: it removes
    /// each top-level entry, deleting symlinks as links (never following them
    /// into the shared store or a diverged copy). This is data-safety-critical:
    /// a naive <c>Directory.Delete(staged, recursive: true)</c> could follow a
    /// directory symlink and delete shared mod files.
    /// </summary>
    private void ClearStagedDir(string staged)
    {
        if (!Directory.Exists(staged))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(staged))
        {
            FileAttributes attrs;
            try
            {
                attrs = File.GetAttributes(entry);
            }
            catch (FileNotFoundException)
            {
                continue; // raced away; nothing to delete
            }

            // ReparsePoint == symlink (file or dir). The link itself is removed
            // without touching the target — but the delete API must match the
            // link's kind, or Windows throws:
            //   - Directory symlink (ReparsePoint + Directory) → Directory.Delete.
            //     On Windows, File.Delete on a directory (incl. a dir-symlink)
            //     throws UnauthorizedAccessException ("Access denied" — Windows
            //     surfaces "is a directory" via the file-delete API as access-
            //     denied). Directory.Delete on a reparse point removes the point
            //     itself, NOT the target, so it stays data-safe on both platforms.
            //   - File symlink (ReparsePoint, not Directory) → File.Delete.
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry); // directory symlink → remove the link only
                }
                else
                {
                    File.Delete(entry);      // file symlink → remove the link only
                }
            }
            else if ((attrs & FileAttributes.Directory) != 0)
            {
                Directory.Delete(entry, recursive: true); // real directory → recurse
            }
            else
            {
                File.Delete(entry);                        // real file (mods.lst, etc.)
            }
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

        // Phase 2: a null/absent Policy on a mod entry (Phase 1 profile.json, or
        // a hand-edit) defaults to Latest. Same posture as Mods-null coercion.
        if (profile.Mods.Any(m => m.Policy is null))
        {
            profile.Mods = profile.Mods
                .Select(m => m.Policy is null ? m with { Policy = ModVersionPolicy.Latest } : m)
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

    private void WriteProfileFile(Profile profile)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(ProfileFilePath(ProfileDir(profile.Id)), json, ModListEncoding);
    }

    // ---- path helpers (all internal-only — never leak through the interface) --

    private string ProfileDir(Guid id) => Path.Combine(_baseFolder, id.ToString());
    private static string ProfileFilePath(string profileDir) => Path.Combine(profileDir, "profile.json");
    private string StagedDir(Guid id) => Path.Combine(ProfileDir(id), "staged");
    private string ProfileModsDir(Guid id) => Path.Combine(ProfileDir(id), "mods");
    private string ProfileModDir(Guid id, string modName) => Path.Combine(ProfileModsDir(id), modName);
    private static string ModListPath(string stagedRoot) => Path.Combine(stagedRoot, "mods.lst");

    private static KeyNotFoundException UnknownProfile(Guid id) =>
        new($"No profile exists with id '{id}'.");

    private static KeyNotFoundException UnknownMod(Guid id, string modName) =>
        new($"Profile '{id}' has no mod named '{modName}'.");
}
