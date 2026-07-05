using Magos.Modificus.Config;
using Magos.Modificus.General;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Steam;

/// <summary>
/// <see cref="ISteamService"/> implementation. A thin orchestrator: discovery is
/// delegated to the platform <see cref="ISteamDiscoverer"/> (selected once at DI
/// registration from <see cref="SteamDiscoveryOptions.Platform"/>) and the
/// game-running check to <see cref="IProcessLookup"/>. Holds no per-call state
/// and contains no platform dispatch; every OS-specific concern lives behind a
/// polymorphic collaborator wired at the composition root.
/// </summary>
/// <remarks>
/// Registered as a singleton. <see cref="Discover"/> never throws on missing
/// pieces; those are reported via <see cref="DiscoveryResult.Status"/> + the
/// nullable fields.
/// <para>
/// <b>User overrides (Track C):</b> <see cref="Discover"/> reads the live
/// <see cref="MagosConfig.Discovery"/> section once per call (via
/// <see cref="IConfigLoader"/>) and overlays any user-supplied paths onto the
/// discoverer's result, then recomputes <see cref="DiscoveryResult.Status"/>
/// using the shared <see cref="SteamDiscoveryCore.ComputeStatus"/> rule. A
/// null/whitespace override leaves the auto-discovered value in place. This
/// keeps the overlay's completeness rule identical, by construction, to the one
/// the discoverer used.</para>
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
        // One config snapshot per discovery call (live-read: a Settings/escape-
        // hatch write between calls is visible on the next Discover()).
        var discovery = _configLoader.Load().Discovery;

        // Platform discovery first (unchanged), then overlay user overrides.
        var auto = _discoverer.Discover();
        return ApplyUserOverrides(auto, discovery);
    }

    /// <inheritdoc />
    public bool IsGameRunning() => _processes.IsRunning(_options.GameProcessName);

    /// <summary>
    /// Overlays the user-supplied discovery overrides onto a discoverer result
    /// and recomputes the status. For each of the four path fields, a non-null/
    /// non-whitespace override replaces the auto-discovered value as-is (no
    /// re-verify); a null/whitespace override keeps the auto value. After
    /// overlaying, the status is recomputed via
    /// <see cref="SteamDiscoveryCore.ComputeStatus"/> so it reflects the final
    /// field values against the platform's completeness rule.
    /// </summary>
    /// <param name="auto">The discoverer's result (auto-discovered values).</param>
    /// <param name="overrides">The live user-override section (nullable fields).</param>
    /// <returns>A new <see cref="DiscoveryResult"/> with overrides applied + the
    /// status recomputed.</returns>
    private DiscoveryResult ApplyUserOverrides(DiscoveryResult auto, DiscoveryConfig overrides)
    {
        var steam = Override(auto.SteamInstallPath, overrides.UserSteamInstallPath);
        var darktide = Override(auto.DarktideGameBinaryPath, overrides.UserDarktideGameBinaryPath);
        var compatdata = Override(auto.CompatdataPath, overrides.UserCompatdataPath);
        var proton = Override(auto.ProtonBinaryPath, overrides.UserProtonBinaryPath);

        // The Proton version label is a derived description of the auto-
        // discovered Proton dir. If the user overrode the Proton binary path,
        // the auto label no longer describes the path in use; null it so the UI
        // shows nothing rather than a misleading label. (Informational only;
        // launch uses the binary path, not the label.)
        var protonVersion = overrides.UserProtonBinaryPath is null || proton == auto.ProtonBinaryPath
            ? auto.ProtonVersion
            : null;

        var status = SteamDiscoveryCore.ComputeStatus(_options.Platform, steam, darktide, compatdata, proton);

        if (status != auto.Status)
        {
            _logger.LogInformation(
                "Discovery status changed after user overrides: {Before} -> {After}.",
                auto.Status, status);
        }

        return auto with
        {
            SteamInstallPath = steam,
            DarktideGameBinaryPath = darktide,
            CompatdataPath = compatdata,
            ProtonBinaryPath = proton,
            ProtonVersion = protonVersion,
            Status = status,
        };
    }

    /// <summary>
    /// Returns <paramref name="overrideValue"/> when it is a usable path
    /// (non-null + non-whitespace); otherwise falls back to
    /// <paramref name="autoValue"/>. The "trust the user" rule: a supplied
    /// value is used as-is with no re-verify.
    /// </summary>
    private static string? Override(string? autoValue, string? overrideValue) =>
        !string.IsNullOrWhiteSpace(overrideValue) ? overrideValue : autoValue;
}
