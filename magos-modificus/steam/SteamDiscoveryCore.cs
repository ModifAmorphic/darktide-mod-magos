using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Steam;

/// <summary>
/// The platform-agnostic mechanics of Steam discovery: candidate-root
/// resolution, <c>libraryfolders.vdf</c> reading, Darktide probing, and the
/// all-null failure result. Shared by <see cref="LinuxSteamDiscoverer"/> and
/// <see cref="WindowsSteamDiscoverer"/> via composition — each discoverer
/// injects this and layers its own platform-specific steps (Linux: compatdata +
/// Proton; Windows: registry). This is composition, not inheritance.
/// </summary>
internal sealed class SteamDiscoveryCore
{
    private readonly SteamDiscoveryOptions _options;
    private readonly ILogger<SteamDiscoveryCore> _logger;

    public SteamDiscoveryCore(SteamDiscoveryOptions options, ILogger<SteamDiscoveryCore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Picks the first candidate whose path is non-empty and carries a valid
    /// <c>libraryfolders.vdf</c>; returns a null-path <see cref="ResolvedRoot"/>
    /// when none qualifies.
    /// </summary>
    public ResolvedRoot ResolveRoot(params RootCandidate[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path) && SteamRootIsValid(candidate.Path))
            {
                return new ResolvedRoot(candidate.Path, candidate.IsFlatpak, candidate.FromRegistry);
            }
        }
        return new ResolvedRoot(Path: null, IsFlatpak: false, FromRegistry: false);
    }

    /// <summary>
    /// Reads + parses <c>steamapps/libraryfolders.vdf</c> under
    /// <paramref name="steamRoot"/>; always includes the Steam root itself as a
    /// fallback library (it's normally listed as library "0"). IO/permission
    /// failures degrade to a root-only search + a warning.
    /// </summary>
    public IReadOnlyList<string> ReadLibraries(string steamRoot, List<string> warnings)
    {
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            var content = File.ReadAllText(vdf);
            var libs = LibraryFoldersVdf.Parse(content);

            // The Steam install root is always a usable library even if the VDF
            // omits it (it normally lists itself as library "0"); ensure it's
            // probed so a missing/malformed VDF doesn't hide a locally-installed
            // Darktide. De-dup against what the VDF already provided.
            if (!libs.Any(l => string.Equals(l, steamRoot, StringComparison.Ordinal)))
            {
                libs = libs.Append(steamRoot).ToList();
            }

            if (libs.Count > 1)
            {
                warnings.Add($"Searched {libs.Count} Steam libraries for Darktide.");
            }

            return libs;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read {Vdf}; falling back to Steam root only.", vdf);
            warnings.Add("Could not read libraryfolders.vdf; searched Steam root only.");
            return new[] { steamRoot };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Permission denied reading {Vdf}; falling back to Steam root only.", vdf);
            warnings.Add("Permission denied reading libraryfolders.vdf; searched Steam root only.");
            return new[] { steamRoot };
        }
    }

    /// <summary>
    /// Probes <c>&lt;lib&gt;/steamapps/common/&lt;DarktideCommonDir&gt;/binaries/&lt;GameBinaryName&gt;</c>
    /// across every library; first hit wins. Returns null if Darktide is not
    /// found under any library.
    /// </summary>
    public string? FindDarktide(IReadOnlyList<string> libraries)
    {
        foreach (var lib in libraries)
        {
            var exe = Path.Combine(
                lib, "steamapps", "common",
                _options.DarktideCommonDir, "binaries", _options.GameBinaryName);

            if (File.Exists(exe))
            {
                return exe;
            }
        }

        _logger.LogInformation("Darktide not found under any Steam library.");
        return null;
    }

    /// <summary>A <see cref="DiscoveryStatus.Failed"/> result with all paths null.</summary>
    public static DiscoveryResult Failed(IReadOnlyList<string> warnings) =>
        new(
            SteamInstallPath: null,
            DarktideGameBinaryPath: null,
            CompatdataPath: null,
            ProtonBinaryPath: null,
            ProtonVersion: null,
            Status: DiscoveryStatus.Failed,
            Warnings: warnings);

    private static bool SteamRootIsValid(string root) =>
        Directory.Exists(root)
        && File.Exists(Path.Combine(root, "steamapps", "libraryfolders.vdf"));

    /// <summary>A candidate Steam install root probed by <see cref="ResolveRoot"/>.</summary>
    public sealed record RootCandidate(string? Path, bool IsFlatpak, bool FromRegistry);

    /// <summary>The Steam root <see cref="ResolveRoot"/> settled on (null-path when none qualified).</summary>
    public sealed record ResolvedRoot(string? Path, bool IsFlatpak, bool FromRegistry);
}
