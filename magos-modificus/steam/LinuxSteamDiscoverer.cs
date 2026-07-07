using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Steam;

/// <summary>
/// Linux <see cref="ISteamDiscoverer"/>. Resolves the Steam install (native
/// default, then Flatpak), derives the Darktide install, Proton prefix
/// (compatdata), and Proton version. All platform-specific steps live here; the
/// shared mechanics (root resolution, library reading, Darktide probing) come
/// from <see cref="SteamDiscoveryCore"/>. Selected at DI registration when
/// <see cref="SteamDiscoveryOptions.Platform"/> is <see cref="DiscoveryPlatform.Linux"/>.
/// </summary>
internal sealed class LinuxSteamDiscoverer : ISteamDiscoverer
{
    private readonly SteamDiscoveryCore _core;
    private readonly SteamDiscoveryOptions _options;
    private readonly ILogger<LinuxSteamDiscoverer> _logger;

    public LinuxSteamDiscoverer(
        SteamDiscoveryCore core,
        SteamDiscoveryOptions options,
        ILogger<LinuxSteamDiscoverer> logger)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public DiscoveryResult Discover()
    {
        var warnings = new List<string>();

        // Ordered candidates: native default first, then Flatpak. The first one
        // whose libraryfolders.vdf exists wins; Flatpak is flagged for a warning.
        var resolved = _core.ResolveRoot(
            new SteamDiscoveryCore.RootCandidate(_options.LinuxDefaultSteamRoot, IsFlatpak: false, FromRegistry: false),
            new SteamDiscoveryCore.RootCandidate(_options.LinuxFlatpakSteamRoot, IsFlatpak: true, FromRegistry: false));

        if (resolved.Path is null)
        {
            _logger.LogWarning("Steam install not found (no candidate carried a valid libraryfolders.vdf).");
            return SteamDiscoveryCore.Failed(warnings);
        }

        if (resolved.IsFlatpak)
        {
            warnings.Add("Flatpak Steam detected; some Steam integrations may be limited.");
        }

        var libraries = _core.ReadLibraries(resolved.Path, warnings);
        var darktide = _core.FindDarktide(libraries);
        var compatdata = FindCompatdata(resolved.Path, libraries);
        var proton = FindProton(resolved.Path, _options.LinuxCompatibilityToolsDir, warnings);

        var status = SteamDiscoveryCore.ComputeStatus(
            _options.Platform, resolved.Path, darktide, compatdata, proton?.Path);
        _logger.LogInformation(
            "Linux discovery: {Status} (steam={Steam}, darktide={Darktide}, compatdata={Compatdata}, proton={Proton}).",
            status, resolved.Path, darktide ?? "(missing)", compatdata ?? "(missing)", proton?.Path ?? "(missing)");

        return new DiscoveryResult(
            SteamInstallPath: resolved.Path,
            DarktideGameBinaryPath: darktide,
            CompatdataPath: compatdata,
            ProtonBinaryPath: proton?.Path,
            ProtonVersion: proton?.Version,
            Status: status,
            Warnings: warnings);
    }

    /// <summary>
    /// Resolves the Darktide compatdata (Proton prefix) for the configured app
    /// id. Probes the main Steam install first, then each library declared in
    /// <c>libraryfolders.vdf</c> (in order); the first existing dir wins.
    /// </summary>
    /// <remarks>
    /// The prefix is created on whichever drive Steam chose at install time, so
    /// it frequently lives under a Steam *library* rather than the main install
    /// (e.g. <c>/games/steamapps/compatdata/&lt;appid&gt;/</c>). Probing the
    /// main install first preserves prior behavior — when the prefix is there it
    /// still wins — and the library scan is deterministic (VDF order).
    /// </remarks>
    private string? FindCompatdata(string steamRoot, IReadOnlyList<string> libraries)
    {
        var appId = _options.DarktideAppId.ToString(CultureInfo.InvariantCulture);

        // Main install first, then each library in VDF order — the main install
        // is yielded explicitly so it's probed first even if the VDF lists it
        // later (or omits it); the explicit duplicate is skipped below.
        foreach (var root in CompatdataCandidateRoots(steamRoot, libraries))
        {
            var dir = Path.Combine(root, "steamapps", "compatdata", appId);
            if (Directory.Exists(dir))
            {
                return dir;
            }
        }

        return null;
    }

    private static IEnumerable<string> CompatdataCandidateRoots(string steamRoot, IReadOnlyList<string> libraries)
    {
        yield return steamRoot;
        foreach (var lib in libraries)
        {
            // Skip the main install when the VDF lists it — it's yielded first above.
            if (!string.Equals(lib, steamRoot, StringComparison.Ordinal))
            {
                yield return lib;
            }
        }
    }

    /// <summary>
    /// The Proton selection heuristic (deep Steam per-game config parsing is out
    /// of v1):
    /// <list type="number">
    /// <item><term>1</term><description><c>Proton - Experimental</c> in <c>steamapps/common</c> (common default).</description></item>
    /// <item><term>2</term><description>The highest-versioned <c>Proton X.Y</c> in <c>steamapps/common</c>.</description></item>
    /// <item><term>3</term><description>The highest-versioned build in the injected <c>compatibilitytools.d</c> (ProtonUp-GE).</description></item>
    /// <item><term>4</term><description>Nothing → null (escape hatch; UI prompts).</description></item>
    /// </list>
    /// The chosen source is recorded in <paramref name="warnings"/>.
    /// </summary>
    private (string Path, string Version)? FindProton(
        string steamRoot, string? compatToolsDir, List<string> warnings)
    {
        var common = Path.Combine(steamRoot, "steamapps", "common");

        // (1) Proton - Experimental — the common Steam default.
        const string ExperimentalDir = "Proton - Experimental";
        var experimental = Path.Combine(common, ExperimentalDir, "proton");
        if (File.Exists(experimental))
        {
            warnings.Add("Selected Proton - Experimental (default heuristic).");
            return (experimental, ExperimentalDir);
        }

        // (2) Highest-versioned Proton X.Y in steamapps/common.
        if (BestProton(common) is { } commonBest)
        {
            warnings.Add($"Selected {commonBest.Dir} (highest-versioned Proton in steamapps/common).");
            return (Path.Combine(common, commonBest.Dir, "proton"), commonBest.Dir);
        }

        // (3) A custom build in compatibilitytools.d (ProtonUp-GE).
        if (!string.IsNullOrWhiteSpace(compatToolsDir) && BestProton(compatToolsDir) is { } geBest)
        {
            warnings.Add($"Selected {geBest.Dir} (from compatibilitytools.d).");
            return (Path.Combine(compatToolsDir, geBest.Dir, "proton"), geBest.Dir);
        }

        // (4) Nothing found → escape hatch.
        warnings.Add("No Proton build found; user will be prompted (escape hatch).");
        return null;
    }

    /// <summary>
    /// Picks the highest-versioned Proton build (by parsed major.minor) under
    /// <paramref name="parent"/>. A candidate must carry a <c>proton</c> entry
    /// script (the defining trait of a Proton install — this also excludes the
    /// Darktide game dir, which sits in <c>steamapps/common</c> but has no
    /// proton). Dir names like <c>Proton 9.0</c>, <c>Proton 5.13</c>,
    /// <c>GE-Proton9-3</c>; <c>Proton - Experimental</c> (no version) is
    /// intentionally excluded here — it's handled explicitly upstream. Ties keep
    /// directory-enumeration order.
    /// </summary>
    private static (string Dir, Version Version)? BestProton(string parent)
    {
        if (!Directory.Exists(parent))
        {
            return null;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(parent);
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        (string Dir, Version Ver) best = default;
        var found = false;
        foreach (var dir in dirs)
        {
            if (!File.Exists(Path.Combine(dir, "proton")))
            {
                continue;
            }

            var name = Path.GetFileName(dir);
            var version = TryParseProtonVersion(name);
            if (version is null)
            {
                continue;
            }

            if (!found || version.CompareTo(best.Ver) > 0)
            {
                best = (name, version);
                found = true;
            }
        }

        return found ? best : null;
    }

    // Matches the first <digits>(<sep><digits>)? in a Proton dir name, where
    // <sep> is '.' (official "Proton 9.0") or '-' (custom "GE-Proton9-3"). e.g.
    // "Proton 9.0" -> 9.0, "Proton 5.13" -> 5.13, "Proton 5.0-10" -> 5.0
    // (build patch ignored), "GE-Proton9-3" -> 9.3. "Proton - Experimental"
    // (no digits) -> null.
    private static Version? TryParseProtonVersion(string name)
    {
        var match = Regex.Match(name, @"(\d+)(?:[.-](\d+))?", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = match.Groups[2].Success
            ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
            : 0;
        return new Version(major, minor);
    }
}
