using System.IO.Compression;
using Magos.Modificus.Config;
using Magos.Modificus.General;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Mods;

/// <summary>
/// Filesystem-backed <see cref="IModImportService"/>. Resolves (or creates)
/// the container for the source, then extracts a <c>.zip</c> / copies a folder
/// into the repository-provided opaque version folder, via
/// <see cref="IModRepository.AddVersion"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Container resolution:</b> Nexus by <see cref="NexusSource.ModId"/>,
/// GitHub by <see cref="GitHubSource.Owner"/>/<see cref="GitHubSource.Repo"/>,
/// Untracked by <see cref="ModContainer.Name"/> (the import <c>modName</c>).
/// A re-import of the same identity resolves to the existing container instead
/// of creating a new one, so the container is the dedup unit across imports.</para>
/// <para>
/// <b>Version resolution:</b> re-importing the same <paramref name="version"/>
/// dedups: <see cref="IModRepository.AddVersion"/> reuses the existing version
/// folder + refreshes its files; a new <paramref name="version"/> creates a new
/// opaque version folder and flips <see cref="ModVersion.IsLatest"/> to it (it
/// is the newest by <see cref="ModVersion.ImportedAt"/>).</para>
/// <para>
/// This service does NOT touch profile mod lists: the caller adds the profile
/// reference via <c>IProfileService.AddMod</c> after the import succeeds
/// (import the repository copy, then reference it from the profile).</para>
/// <para>
/// Registered as a singleton (no per-request state). The single-UI-thread
/// assumption holds, matching <see cref="ModRepository"/> +
/// <c>ProfileService</c>. The mods root folder is read live from
/// <see cref="IConfigLoader"/> on each import, so a runtime folder change via
/// the upcoming Settings window routes the next import to the new path.</para>
/// </remarks>
internal sealed class ModImportService : IModImportService
{
    private const string ZipExtension = ".zip";

    private readonly IModRepository _repo;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<ModImportService> _logger;

    public ModImportService(
        IModRepository repo,
        IConfigLoader configLoader,
        ILogger<ModImportService> logger)
    {
        _repo = repo;
        _configLoader = configLoader;
        _logger = logger;
    }

    /// <inheritdoc />
    public (Guid ContainerId, string VersionString) Import(
        string sourcePath,
        string modName,
        ModSource source,
        string version)
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

        // One live snapshot for the whole import. ModsFolder is non-null by
        // MagosConfig contract (defaults to <app-data>/mods); CreateDirectory
        // is idempotent, first-import safe, and runs on the live path so a
        // runtime folder change routes this import to the new dir.
        var modsFolder = _configLoader.Load().ModsFolder;
        Directory.CreateDirectory(modsFolder);

        // Confine modName to a single direct child of the mods root
        // before any filesystem op, even though the import target is now an
        // opaque UUID folder (not modName-derived). modName is user-editable
        // (the import modal lets the user rename), so the same confinement
        // guards against names carrying path separators, ".." or an absolute
        // path - which would later clash with the symlink-name sanitization in
        // staging or confuse the untracked-by-name index. The explicit
        // separator check is cross-platform; GetFullPath then normalizes so the
        // prefix check is a real containment test (it also rejects a bare "..").
        ValidateModName(modName, modsFolder);

        // Resolve or create the container (the dedup unit). Untracked dedups by
        // the modName; Nexus/GitHub dedup by source identity.
        var container = ResolveContainer(source, modName);

        // The populate callback: extract zip or copy folder into the path the
        // repo provides. The repo decides whether that path is a fresh folder
        // (new version) or a cleaned-and-reused one (dedup of the same tag).
        void Populate(string versionDir)
        {
            if (IsZip(sourcePath))
            {
                _logger.LogInformation(
                    "Importing {Mod} from .zip '{Source}' -> '{Target}'", modName, sourcePath, versionDir);
                ZipFile.ExtractToDirectory(sourcePath, versionDir);
            }
            else
            {
                _logger.LogInformation(
                    "Importing {Mod} from folder '{Source}' -> '{Target}'", modName, sourcePath, versionDir);
                CopyDirectory(sourcePath, versionDir);
            }
        }

        _repo.AddVersion(container.Id, version, Populate);

        _logger.LogInformation(
            "Imported {Mod} (source={Source}, version={Version}) onto container {Id}",
            modName, source, version, container.Id);
        return (container.Id, version);
    }

    // ---- helpers -----------------------------------------------------------

    private ModContainer ResolveContainer(ModSource source, string modName)
    {
        // Untracked dedups by name; Nexus/GitHub by source identity. Create the
        // container if absent.
        if (source is UntrackedSource)
        {
            return _repo.FindUntrackedByName(modName) ?? _repo.CreateContainer(source, modName);
        }
        return _repo.FindBySource(source) ?? _repo.CreateContainer(source, modName);
    }

    private static void ValidateModName(string modName, string modsFolder)
    {
        var rootFull = Path.GetFullPath(modsFolder);
        var targetFull = Path.GetFullPath(Path.Combine(rootFull, modName));
        if (modName.IndexOf('/') >= 0
            || modName.IndexOf('\\') >= 0
            || !targetFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Mod name must not contain path separators, '..', or be an absolute path.", nameof(modName));
        }
    }

    private static bool IsZip(string path) =>
        Path.GetExtension(path).Equals(ZipExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Recursively copies <paramref name="sourceDir"/> to
    /// <paramref name="targetDir"/>. Creates the target tree as it goes. Mirrors
    /// <see cref="ZipFile.ExtractToDirectory"/>'s "faithful copy of the source tree"
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
