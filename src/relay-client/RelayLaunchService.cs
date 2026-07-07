using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Profiles;
using Modificus.Curator.Steam;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.RelayClient;

/// <summary>
/// Default <see cref="IRelayLaunchService"/>. A thin orchestrator: it runs
/// the platform-agnostic launch flow (discover → check completeness → prepare
/// mod root → launcher-exists → spawn → result mapping) and delegates the
/// platform-varying pieces to an <see cref="IPlatformLaunchStrategy"/> selected
/// once at DI registration from the runtime OS. Contains no per-launch OS branch.
/// </summary>
/// <remarks>
/// <para>
/// The strategy owns the spawn (direct <c>Process.Start</c> on Windows;
/// <c>proton run</c> + both <c>STEAM_COMPAT_*</c> env vars + <c>Z:\</c>-translated
/// args on Linux), the per-platform required discovery fields, and the launch
/// label for logging.</para>
/// <para>
/// Registered as a singleton: it holds no per-launch state. The active strategy
/// does not change at runtime. Reads <see cref="CuratorConfig"/> live from
/// <see cref="IConfigLoader"/> on each launch (one snapshot per op). Tests inject
/// the concrete Windows/Linux strategy (with a fake
/// <see cref="IProcessLauncher"/>) to exercise either path on any CI OS.</para>
/// </remarks>
internal sealed class RelayLaunchService : IRelayLaunchService
{
    /// <summary>The launcher executable filename (a Windows binary, run under
    /// Proton on Linux). Lives in <see cref="CuratorConfig.RelayDir"/>.</summary>
    internal const string LauncherExecutableName = "modificus_relay.exe";

    /// <summary>
    /// The Steam app id for Darktide. The launcher defaults to this value when
    /// <c>--steam-app-id</c> is omitted; Curator relies on that default and only
    /// emits <c>--steam-app-id</c> to override it (which the current config does
    /// not surface (see <c>ServiceCollectionExtensions</c> / future config work).
    /// </summary>
    internal const int DarktideSteamAppId = 1361210;

    private readonly IProfileService _profiles;
    private readonly ISteamService _steam;
    private readonly IConfigLoader _configLoader;
    private readonly IPlatformLaunchStrategy _strategy;
    private readonly ILogger<RelayLaunchService> _logger;

    public RelayLaunchService(
        IProfileService profiles,
        ISteamService steam,
        IConfigLoader configLoader,
        IPlatformLaunchStrategy strategy,
        ILogger<RelayLaunchService> logger)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public LaunchResult Launch(Guid profileId)
    {
        try
        {
            // One live config snapshot for the whole launch. RelayDir
            // + Logging.LogFile are read once here; a runtime config change via
            // the upcoming Settings window takes effect on the next launch.
            var config = _configLoader.Load();

            // Discovery first: if we cannot launch, do not touch the profile's
            // mod root (no point writing mods.lst for a launch that won't happen).
            var discovery = _steam.Discover();
            var missing = _strategy.RequiredDiscoveryFields(discovery);
            if (missing.Count > 0)
            {
                _logger.LogWarning(
                    "Discovery incomplete ({Platform}); missing: {Fields}.",
                    _strategy.Name, string.Join(", ", missing));
                return new LaunchResult(
                    LaunchStatus.DiscoveryIncomplete,
                    Message: $"Steam discovery is missing required fields: {string.Join(", ", missing)}.",
                    MissingDiscoveryFields: missing);
            }

            // PrepareModRoot writes mods.lst + ensures the mod root exists and
            // returns the --mod-path. KeyNotFoundException (unknown profile) is
            // caught below and mapped to LaunchStatus.Error.
            var modPath = _profiles.PrepareModRoot(profileId);

            var launcherPath = Path.Combine(config.RelayDir, LauncherExecutableName);
            if (!File.Exists(launcherPath))
            {
                _logger.LogError("Relay launcher not found at {Path}.", launcherPath);
                return ErrorResult($"Relay launcher not found at '{launcherPath}'.");
            }

            var gameBinary = discovery.DarktideGameBinaryPath!;
            var logFile = config.Logging.LogFile;

            var started = _strategy.Start(launcherPath, discovery, gameBinary, modPath, logFile);

            if (!started)
            {
                return ErrorResult($"Failed to start the Relay launcher at '{launcherPath}'.");
            }

            _logger.LogInformation("Launched profile {Id} via the {Platform} path.", profileId, _strategy.Name);
            return new LaunchResult(LaunchStatus.Launched, Message: null, MissingDiscoveryFields: Array.Empty<string>());
        }
        catch (KeyNotFoundException ex)
        {
            // Unknown profile (PrepareModRoot): surfaced as Error, not thrown.
            _logger.LogError(ex, "Launch failed: profile {Id} not found.", profileId);
            return ErrorResult($"Profile '{profileId}' was not found.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Launch failed for profile {Id}: I/O error.", profileId);
            return ErrorResult($"Launch failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Catch-all: the façade's contract is to always return a result
            // rather than push failure handling onto the caller.
            _logger.LogError(ex, "Launch failed for profile {Id}: unexpected error.", profileId);
            return ErrorResult($"Launch failed: {ex.Message}");
        }
    }

    private static LaunchResult ErrorResult(string message) =>
        new(LaunchStatus.Error, Message: message, MissingDiscoveryFields: Array.Empty<string>());
}
