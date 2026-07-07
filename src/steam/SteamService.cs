using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Steam;

/// <summary>
/// <see cref="ISteamService"/> implementation. A thin orchestrator: discovery is
/// delegated to the platform <see cref="ISteamDiscoverer"/> (selected once at DI
/// registration from <see cref="SteamDiscoveryOptions.Platform"/>) and the
/// game-running check to <see cref="IProcessLookup"/>. Holds no per-call state
/// and contains no platform dispatch; every OS-specific concern lives behind a
/// polymorphic collaborator wired at the composition root.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton. <see cref="Discover"/> never throws on missing
/// pieces; those are reported via <see cref="DiscoveryResult.Status"/> + the
/// nullable fields.</para>
/// <para>
/// <b>Validate + heal + persist (Track C review fix):</b> <see cref="Discover"/>
/// runs a four-step pipeline per call:
/// <list type="number">
/// <item><description><b>Validate</b> the persisted
/// <see cref="DiscoveryConfig"/> user overrides (read live via
/// <see cref="IConfigLoader"/>). For each platform-relevant field, an override
/// that exists on disk (a directory for Steam install + compatdata; a file for
/// the Darktide binary + Proton script) is <i>valid</i> and kept as-is. A null /
/// whitespace / non-existent override <i>needs healing</i>.</description></item>
/// <item><description><b>Heal</b>: if any field needs healing, run the platform
/// discoverer once. Each healing field picks up the discoverer's value; a field
/// the discoverer also couldn't find stays null (<i>still missing</i>).
/// <b>Fast path:</b> when every field is valid the discoverer is skipped
/// entirely.</description></item>
/// <item><description><b>Selectively persist</b>: if any field was healed to a
/// non-null value, re-read the config fresh and write ONLY the healed fields'
/// <c>User*Path</c> back (valid fields are untouched, preserving user edits; a
/// hand-edit on disk between calls is visible because the read-modify-save
/// starts from the current file).</description></item>
/// <item><description><b>Return</b> a <see cref="DiscoveryResult"/> with the
/// final paths (valid + healed) and a status computed from them via the shared
/// <see cref="SteamDiscoveryCore.ComputeStatus"/> rule (the same rule the
/// discoverer used).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Platform-gating:</b> on Windows the compatdata + Proton fields are Linux-
/// only (Windows is native) and are neither validated nor healed; they stay null
/// in the result. On Linux all four fields are checked + healed.</para>
/// <para>
/// <b>Caller contract:</b>
/// <list type="bullet">
/// <item>The composition root calls <see cref="Discover"/> at startup (non-
/// blocking). A missing-fields result is logged as a warning so the user can
/// still use the app; they just cannot launch until resolved.</item>
/// <item><see cref="EnginseerClient.EnginseerLaunchService"/> calls
/// <see cref="Discover"/> at launch (blocking). A missing-fields result yields
/// <see cref="EnginseerClient.LaunchResult.Status"/> =
/// <see cref="EnginseerClient.LaunchStatus.DiscoveryIncomplete"/>, surfacing the
/// escape-hatch modal.</item>
/// <item>The Settings window reads <see cref="DiscoveryConfig"/> directly (now
/// populated by the startup Discover's validate + heal), so the discovery fields
/// show the current paths rather than blanks.</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class SteamService : ISteamService
{
    private readonly ISteamDiscoverer _discoverer;
    private readonly SteamDiscoveryOptions _options;
    private readonly IProcessLookup _processes;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<SteamService> _logger;

    public SteamService(
        ISteamDiscoverer discoverer,
        SteamDiscoveryOptions options,
        IProcessLookup processes,
        IConfigLoader configLoader,
        ILogger<SteamService> logger)
    {
        _discoverer = discoverer ?? throw new ArgumentNullException(nameof(discoverer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public DiscoveryResult Discover()
    {
        // One config snapshot at the top (live-read: a Settings / escape-hatch
        // write between calls is visible on the next Discover()).
        var discovery = _configLoader.Load().Discovery;
        var platform = _options.Platform;

        // Platform-gating: on Windows the compatdata + Proton fields are Linux-
        // only (native: unused). They are neither validated nor healed.
        var checkCompatdata = platform == DiscoveryPlatform.Linux;
        var checkProton = platform == DiscoveryPlatform.Linux;

        // (1) Validate each platform-relevant field. A valid field is one whose
        // override exists on disk with the right kind (dir vs. file). Anything
        // else (null/whitespace, or non-existent) needs healing.
        var steamValid = IsValidPath(discovery.UserSteamInstallPath, isDirectory: true);
        var darktideValid = IsValidPath(discovery.UserDarktideGameBinaryPath, isDirectory: false);
        var compatdataValid = !checkCompatdata
            || IsValidPath(discovery.UserCompatdataPath, isDirectory: true);
        var protonValid = !checkProton
            || IsValidPath(discovery.UserProtonBinaryPath, isDirectory: false);

        var anyNeedsHealing = !steamValid || !darktideValid || !compatdataValid || !protonValid;

        // (2) Heal: run the discoverer once if any field needs healing. Fast
        // path: when every field is valid, the discoverer is skipped entirely.
        DiscoveryResult? auto = null;
        if (anyNeedsHealing)
        {
            auto = _discoverer.Discover();
        }

        // Build the final values: valid fields stay as-is; healing fields pick
        // up the discoverer's value (which may itself be null -> still missing).
        var steam = steamValid ? discovery.UserSteamInstallPath : auto!.SteamInstallPath;
        var darktide = darktideValid ? discovery.UserDarktideGameBinaryPath : auto!.DarktideGameBinaryPath;
        var compatdata = checkCompatdata
            ? (compatdataValid ? discovery.UserCompatdataPath : auto!.CompatdataPath)
            : null;
        var proton = checkProton
            ? (protonValid ? discovery.UserProtonBinaryPath : auto!.ProtonBinaryPath)
            : null;

        // ProtonVersion is a derived description of the auto-discovered Proton
        // dir. It is carried only when the Proton path came from the discoverer
        // (a healed field). A user override that survived validation drops the
        // label: it may not describe the user-chosen path. On Windows the field
        // is Linux-only + never discovered, so the label is always null there.
        string? protonVersion;
        if (!checkProton || protonValid)
        {
            protonVersion = null;
        }
        else
        {
            protonVersion = auto!.ProtonVersion;
        }

        // (3) Selective save: re-read the config fresh and write ONLY the healed
        // fields that resolved to a non-null value. Valid fields are NOT
        // overwritten (preserves user edits), and a hand-edit on disk between
        // the top-of-call read + this save is preserved too (the read-modify-
        // save starts from the current file, not the stale snapshot).
        var healedSteam = !steamValid && steam is not null;
        var healedDarktide = !darktideValid && darktide is not null;
        var healedCompatdata = checkCompatdata && !compatdataValid && compatdata is not null;
        var healedProton = checkProton && !protonValid && proton is not null;

        if (healedSteam || healedDarktide || healedCompatdata || healedProton)
        {
            var fresh = _configLoader.Load();
            if (healedSteam)
            {
                fresh.Discovery.UserSteamInstallPath = steam;
            }
            if (healedDarktide)
            {
                fresh.Discovery.UserDarktideGameBinaryPath = darktide;
            }
            if (healedCompatdata)
            {
                fresh.Discovery.UserCompatdataPath = compatdata;
            }
            if (healedProton)
            {
                fresh.Discovery.UserProtonBinaryPath = proton;
            }
            _configLoader.Save(fresh);
            _logger.LogInformation(
                "Discovery healed + persisted: {Fields}.",
                string.Join(", ", HealedFieldNames(
                    healedSteam, healedDarktide, healedCompatdata, healedProton)));
        }

        // (4) Build the result + compute status from the final paths.
        var status = SteamDiscoveryCore.ComputeStatus(platform, steam, darktide, compatdata, proton);
        var warnings = auto?.Warnings ?? Array.Empty<string>();

        if (status != DiscoveryStatus.Complete)
        {
            _logger.LogWarning(
                "Discovery is {Status}: steam={Steam}, darktide={Darktide}, compatdata={Compatdata}, proton={Proton}.",
                status,
                steam ?? "(missing)",
                darktide ?? "(missing)",
                compatdata ?? "(missing)",
                proton ?? "(missing)");
        }

        return new DiscoveryResult(
            SteamInstallPath: steam,
            DarktideGameBinaryPath: darktide,
            CompatdataPath: compatdata,
            ProtonBinaryPath: proton,
            ProtonVersion: protonVersion,
            Status: status,
            Warnings: warnings);
    }

    /// <inheritdoc />
    public bool IsGameRunning() => _processes.IsRunning(_options.GameProcessName);

    /// <summary>
    /// Whether a path is a usable override of the given kind: non-null/non-
    /// whitespace AND exists on disk as a directory (when <paramref name="isDirectory"/>
    /// is <c>true</c>) or a file (otherwise). The cheap existence check that
    /// decides whether a field needs healing.
    /// </summary>
    private static bool IsValidPath(string? path, bool isDirectory) =>
        !string.IsNullOrWhiteSpace(path)
        && (isDirectory ? Directory.Exists(path) : File.Exists(path));

    /// <summary>
    /// The display names of the healed fields for the log line. Each flag
    /// already accounts for platform-gating (compatdata + Proton are only
    /// flagged when checked), so the names map 1:1 to the flags.
    /// </summary>
    private static IEnumerable<string> HealedFieldNames(
        bool steam, bool darktide, bool compatdata, bool proton)
    {
        if (steam) yield return "SteamInstallPath";
        if (darktide) yield return "DarktideGameBinaryPath";
        if (compatdata) yield return "CompatdataPath";
        if (proton) yield return "ProtonBinaryPath";
    }
}
