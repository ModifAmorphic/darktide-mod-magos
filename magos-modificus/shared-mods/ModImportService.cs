using System.IO.Compression;
using Magos.Modificus.Config;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.SharedMods;

/// <summary>
/// Filesystem-backed <see cref="IModImportService"/>. Imports a folder
/// (recursive copy) or a <c>.zip</c> archive (<see cref="ZipFile.ExtractToDirectory"/>,
/// in-box for net10.0) into <c>&lt;SharedModsFolder&gt;/&lt;modName&gt;/</c>, then
/// upserts the shared-store entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Upsert semantics:</b> the target <c>&lt;SharedModsFolder&gt;/&lt;modName&gt;/</c>
/// is deleted first (if it exists), then re-populated. The manifest entry is
/// then upserted via <see cref="ISharedModStore.Add"/> (replaces a same-named
/// entry, else appends).</para>
/// <para>
/// Registered as a singleton (no per-request state; the only state, the
/// <c>SharedModsFolder</c>, lives on the singleton <c>MagosConfig</c>). The
/// single-UI-thread assumption holds, matching <see cref="SharedModStore"/> +
/// <c>ProfileService</c>.</para>
/// </remarks>
internal sealed class ModImportService : IModImportService
{
    private const string ZipExtension = ".zip";

    private readonly ISharedModStore _store;
    private readonly string _sharedModsFolder;
    private readonly ILogger<ModImportService> _logger;

    public ModImportService(
        ISharedModStore store,
        MagosConfig config,
        ILogger<ModImportService> logger)
    {
        _store = store;
        // SharedModsFolder is non-null by MagosConfig contract (defaults to
        // <app-data>/shared-mods). Directory.CreateDirectory is idempotent,
        // first-import safe.
        _sharedModsFolder = config.SharedModsFolder;
        _logger = logger;
        Directory.CreateDirectory(_sharedModsFolder);
    }

    /// <inheritdoc />
    public SharedModEntry Import(string sourcePath, string modName, ModSource source, string version)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (string.IsNullOrWhiteSpace(modName))
        {
            throw new ArgumentException("Mod name must not be null or whitespace.", nameof(modName));
        }
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(version);

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Import source not found: '{sourcePath}'. Provide a folder or a .zip archive.", sourcePath);
        }

        // Confine modName to a single direct child of the shared-mods root
        // before any filesystem op. modName is user-editable (the import modal
        // lets the user rename), so it must not carry path separators, ".." or
        // be an absolute path: any of those could escape the root and drive
        // CleanTarget's recursive delete outside it. The explicit separator
        // check is cross-platform (a "\" is a legal filename char on Unix but
        // still not a valid mod name here); GetFullPath then normalizes the
        // combined path so the prefix check is a real containment test (it
        // also rejects a bare "..", which has no separator).
        var rootFull = Path.GetFullPath(_sharedModsFolder);
        var targetFull = Path.GetFullPath(Path.Combine(rootFull, modName));
        if (modName.IndexOf('/') >= 0
            || modName.IndexOf('\\') >= 0
            || !targetFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Mod name must not contain path separators, '..', or be an absolute path.", nameof(modName));
        }

        // Clean target first (upsert semantics). A prior import (or a hand-placed
        // folder) is replaced wholesale so the shared copy matches the new source.
        CleanTarget(targetFull);

        if (IsZip(sourcePath))
        {
            _logger.LogInformation("Importing {Mod} from .zip '{Source}' -> '{Target}'", modName, sourcePath, targetFull);
            ZipFile.ExtractToDirectory(sourcePath, targetFull);
        }
        else
        {
            _logger.LogInformation("Importing {Mod} from folder '{Source}' -> '{Target}'", modName, sourcePath, targetFull);
            CopyDirectory(sourcePath, targetFull);
        }

        var entry = new SharedModEntry
        {
            Name = modName,
            Source = source,
            ActualVersion = version,
            Path = targetFull,
            // The import does not set a policy: the entry's Policy stays at its
            // default (Latest). The caller sets a profile-side policy via
            // IProfileService.AddMod/SetModPolicy; the shared entry's policy is
            // an allocation concern, set when a profile diverges (Phase 4 path).
            Policy = ModVersionPolicy.Latest,
        };
        _store.Add(entry);

        _logger.LogInformation(
            "Imported {Mod} (source={Source}, version={Version}) at '{Path}'", modName, source, version, targetFull);
        return entry;
    }

    // ---- helpers -----------------------------------------------------------

    private static bool IsZip(string path) =>
        Path.GetExtension(path).Equals(ZipExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes a prior target dir/file at <paramref name="target"/> so the import
    /// is a clean upsert. Idempotent (missing target is a no-op). A file (not a
    /// dir) at the target is also removed: defends against weird prior state.
    /// </summary>
    private static void CleanTarget(string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return;
        }

        // File.GetAttributes distinguishes dir vs file (the delete API must
        // match the entry kind or it throws). Recurse only for real directories.
        var attrs = File.GetAttributes(target);
        if ((attrs & FileAttributes.Directory) != 0)
        {
            Directory.Delete(target, recursive: true);
        }
        else
        {
            File.Delete(target);
        }
    }

    /// <summary>
    /// Recursively copies <paramref name="sourceDir"/> to
    /// <paramref name="targetDir"/>. Creates the target tree as it goes. Mirrors
    /// <c>ZipFile.ExtractToDirectory</c>'s "faithful copy of the source tree"
    /// semantics for the folder-import path.
    /// </summary>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        // Files first (cheap; then recurse into subdirectories).
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }
}
