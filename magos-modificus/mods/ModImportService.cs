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

        // Confine modName to a single direct child of the mods root before any
        // filesystem op, even though the import target is an opaque UUID folder
        // (not modName-derived). modName is user-editable (the import modal lets
        // the user rename), so the same confinement guards against names
        // carrying path separators, ".." or an absolute path, which would
        // confuse the untracked-by-name index (the dedup key). The explicit
        // separator check is cross-platform; GetFullPath then normalizes so the
        // prefix check is a real containment test (it also rejects a bare "..").
        ValidateModName(modName, modsFolder);

        // Validate the source structure BEFORE resolving the container or
        // creating a version: an invalid source throws immediately, placing no
        // files and creating no container/version. The source must contain
        // exactly one base directory with a <base>.mod descriptor inside it (the
        // descriptor filename matches the base folder name). This is the
        // structure the mod loader needs: mods bake their folder name into their
        // code (e.g. Mods.file.dofile("dmf/scripts/...")), so the staged symlink
        // MUST carry the base name for the mod's hardcoded paths to resolve.
        // Validating at import time guarantees the staging invariant (exactly
        // one base subdir per version folder) without storing the name anywhere
        // (staging re-derives it from the on-disk structure). Both import kinds
        // fail fast; neither places files on a validation failure.
        var isZip = IsZip(sourcePath);
        var baseName = isZip ? ValidateZipStructure(sourcePath) : ValidateFolderStructure(sourcePath);

        // Resolve or create the container (the dedup unit). Untracked dedups by
        // the modName; Nexus/GitHub dedup by source identity. Runs only after the
        // source validated, so an invalid source creates no container.
        var container = ResolveContainer(source, modName);

        // The populate callback: extract zip or copy folder into the path the
        // repo provides. The repo decides whether that path is a fresh folder
        // (new version) or a cleaned-and-reused one (dedup of the same tag).
        // Both kinds preserve the base folder under <versionDir>/<base>/:
        //   - zip: ExtractToDirectory reproduces the archive's single top-level
        //     directory (validated above), yielding <versionDir>/<base>/<files>.
        //   - folder: the picked folder IS the base, so it is copied itself
        //     (not its contents) into <versionDir>/<base>/.
        // The result is a consistent <versionDir>/<base>/ shape across both
        // import kinds, which is what staging's base-folder discovery relies on.
        void Populate(string versionDir)
        {
            if (isZip)
            {
                _logger.LogInformation(
                    "Importing {Mod} (base '{Base}') from .zip '{Source}' -> '{Target}'",
                    modName, baseName, sourcePath, versionDir);
                ZipFile.ExtractToDirectory(sourcePath, versionDir);
            }
            else
            {
                var target = Path.Combine(versionDir, baseName);
                _logger.LogInformation(
                    "Importing {Mod} (base '{Base}') from folder '{Source}' -> '{Target}'",
                    modName, baseName, sourcePath, target);
                DirectoryCopy.Copy(sourcePath, target);
            }
        }

        _repo.AddVersion(container.Id, version, Populate);

        _logger.LogInformation(
            "Imported {Mod} (base '{Base}', source={Source}, version={Version}) onto container {Id}",
            modName, baseName, source, version, container.Id);
        return (container.Id, version);
    }

    /// <inheritdoc />
    public string GetBaseName(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"Import source not found: '{sourcePath}'. Provide a folder or a .zip archive.", sourcePath);
        }
        // Pure peek: reuse the same structure validation as Import (no duplicated
        // logic, no container/version created, no files placed). The add flow
        // calls this before Import to pre-check a base-name collision; Import
        // re-validates (cheap, and must stay self-validating for direct callers).
        return IsZip(sourcePath) ? ValidateZipStructure(sourcePath) : ValidateFolderStructure(sourcePath);
    }

    /// <inheritdoc />
    public ModContainer? FindExistingContainer(ModSource source, string modName)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Mirrors ResolveContainer minus the create: the dedup lookup only. The
        // add flow uses this to exclude a re-add (same container) from the
        // collision check. The dedup rules live here, not at the call site.
        return source is UntrackedSource
            ? _repo.FindUntrackedByName(modName)
            : _repo.FindBySource(source);
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

    // ---- source-structure validation ---------------------------------------
    //
    // Both import kinds validate the source BEFORE any file is placed (and
    // before the container/version is created). The required shape is exactly
    // one base directory containing a <base>.mod descriptor whose filename
    // matches the base folder name. See PrepareModRoot for why the base name is
    // load-bearing (mods bake their folder name into their code).

    /// <summary>
    /// Validates a <c>.zip</c> archive's structure <em>before</em> extraction:
    /// exactly one top-level directory, no loose top-level files, and a
    /// <c>&lt;base&gt;/&lt;base&gt;.mod</c> descriptor inside the base directory (the
    /// descriptor filename matches the base folder name). Performs no extraction.
    /// </summary>
    /// <param name="zipPath">The archive to inspect.</param>
    /// <returns>The validated base folder name (the single top-level
    /// directory).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the archive has a
    /// loose top-level file, zero or multiple top-level directories, or no
    /// matching <c>&lt;base&gt;/&lt;base&gt;.mod</c> descriptor.</exception>
    /// <remarks>
    /// Nexus mod zips ship the mod folder at the archive root (e.g.
    /// <c>dmf.zip</c> contains <c>dmf/dmf.mod</c>, <c>dmf/scripts/...</c>). The
    /// descriptor filename convention (<c>&lt;base&gt;.mod</c>) is what the mod
    /// loader resolves, so the base name is load-bearing. Inspecting entries
    /// before extracting (rather than catching a post-extraction mismatch) means
    /// an invalid archive leaves nothing on disk.
    /// </remarks>
    private static string ValidateZipStructure(string zipPath)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read);

        var topLevelDirs = new HashSet<string>(StringComparer.Ordinal);
        string? looseFile = null;
        foreach (var entry in archive.Entries)
        {
            // Zip entries use '/' per the spec; normalize '\\' defensively for
            // archives authored by tools that mangle the separator.
            var fullName = entry.FullName.Replace('\\', '/');
            var slash = fullName.IndexOf('/');
            if (slash < 0)
            {
                looseFile ??= fullName; // a top-level file with no parent dir
                continue;
            }
            topLevelDirs.Add(fullName.Substring(0, slash));
        }

        if (looseFile is not null)
        {
            throw new InvalidOperationException(
                $"Invalid mod archive '{zipPath}': found a loose top-level file '{looseFile}'. " +
                "The archive must contain a single mod folder with its '<base>.mod' descriptor inside " +
                "(e.g. 'dmf/dmf.mod'). Repackage the mod so its base folder is at the archive root.");
        }

        if (topLevelDirs.Count == 0)
        {
            // An archive whose entries are all loose files (no directory at all)
            // is distinct from the multi-directory case; surfaced with its own
            // message so the cause is clear.
            throw new InvalidOperationException(
                $"Invalid mod archive '{zipPath}': the archive has no mod folder. " +
                "It must contain a single mod folder with its '<base>.mod' descriptor inside.");
        }

        if (topLevelDirs.Count != 1)
        {
            var list = string.Join(", ", topLevelDirs.OrderBy(n => n, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"Invalid mod archive '{zipPath}': expected exactly one top-level mod folder, found " +
                $"{topLevelDirs.Count} ({list}). The archive must contain a single mod folder with its " +
                "'<base>.mod' descriptor inside.");
        }

        var baseName = topLevelDirs.Single();
        var descriptor = baseName + "/" + baseName + ".mod";
        // GetEntry is an exact-name lookup (and ignores '\\' mangling), so scan
        // entries with the same normalization used above.
        var hasDescriptor = archive.Entries.Any(e =>
            e.FullName.Replace('\\', '/').Equals(descriptor, StringComparison.Ordinal));
        if (!hasDescriptor)
        {
            throw new InvalidOperationException(
                $"Invalid mod archive '{zipPath}': no '{descriptor}' descriptor found inside the mod folder. " +
                "The descriptor filename must match the mod folder name (e.g. 'dmf/dmf.mod').");
        }

        return baseName;
    }

    /// <summary>
    /// Validates a folder's structure: non-empty, and containing a
    /// <c>&lt;folderName&gt;.mod</c> descriptor whose filename matches the folder
    /// name. The picked folder IS the mod's base folder (single-base is inherent
    /// to picking one folder; the <c>.mod</c> check also rejects a pick that is
    /// actually a <em>parent</em> of several mods). Performs no copy.
    /// </summary>
    /// <param name="sourceDir">The picked folder.</param>
    /// <returns>The validated base folder name (the picked folder's name).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the base name
    /// can't be derived, the folder is empty, or no matching
    /// <c>&lt;folderName&gt;.mod</c> descriptor exists.</exception>
    private static string ValidateFolderStructure(string sourceDir)
    {
        var baseName = GetFolderBaseName(sourceDir);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new InvalidOperationException(
                $"Invalid mod folder '{sourceDir}': could not derive a base folder name. " +
                "Pick a folder whose name is the mod's base name.");
        }

        if (!Directory.EnumerateFileSystemEntries(sourceDir).Any())
        {
            throw new InvalidOperationException(
                $"Invalid mod folder '{sourceDir}': the folder '{baseName}' is empty. " +
                "It must contain the mod's files, including a '<base>.mod' descriptor.");
        }

        var descriptor = Path.Combine(sourceDir, baseName + ".mod");
        if (!File.Exists(descriptor))
        {
            throw new InvalidOperationException(
                $"Invalid mod folder '{sourceDir}': no '{baseName}.mod' descriptor found inside the folder. " +
                "The descriptor filename must match the folder name (the picked folder must BE the mod, " +
                "not a parent containing several mods).");
        }

        return baseName;
    }

    /// <summary>
    /// Derives the base folder name from a folder path: the final path segment
    /// after trimming trailing directory separators. Used to name the copied
    /// folder under the version directory.
    /// </summary>
    private static string GetFolderBaseName(string sourceDir)
    {
        var trimmed = sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed);
    }
}
