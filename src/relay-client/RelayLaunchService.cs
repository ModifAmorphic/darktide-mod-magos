using System.ComponentModel;
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
    /// The app-local Relay folder name. A Velopack install on Windows ships
    /// Relay alongside the app under <c>&lt;AppContext.BaseDirectory&gt;/relay/</c>;
    /// this is the fallback when no Relay is deployed at
    /// <see cref="CuratorConfig.RelayDir"/>.
    /// </summary>
    internal const string AppLocalRelayFolderName = "relay";

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
            // returns the --mod-path. A staging-link creation failure surfaces as
            // the raised built-in exception (Win32Exception from the junction
            // path on Windows, IOException / UnauthorizedAccessException from the
            // symlink path on Linux) and is mapped to StagingFailed here; the
            // exception's message is carried on the result so the UI can append
            // it to the localized framing. KeyNotFoundException (unknown profile)
            // is caught below and mapped to LaunchStatus.Error.
            string modPath;
            try
            {
                modPath = _profiles.PrepareModRoot(profileId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                _logger.LogError(ex, "Staging failed for profile {Id}.", profileId);
                return new LaunchResult(
                    LaunchStatus.StagingFailed,
                    Message: ex.Message,
                    MissingDiscoveryFields: Array.Empty<string>());
            }

            var launcherPath = ResolveLauncherPath(
                config.RelayDir, AppContext.BaseDirectory, OperatingSystem.IsWindows());
            if (launcherPath is null)
            {
                // Neither the configured RelayDir nor the Windows app-local
                // payload had the launcher. Report the configured path in the
                // error: that is where Curator looks first and what a user
                // would reconfigure. The app-local path is an internal
                // fallback, not something to surface.
                var configuredPath = Path.Combine(config.RelayDir, LauncherExecutableName);
                _logger.LogError(
                    "Relay launcher not found at {Path} (nor app-local under {Base}).",
                    configuredPath, AppContext.BaseDirectory);
                return ErrorResult($"Relay launcher not found at '{configuredPath}'.");
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

    /// <summary>
    /// Resolves the Relay launcher path with a deliberate precedence:
    /// <list type="number">
    /// <item><term>Configured <c>configRelayDir</c> first.</term>
    /// <description>If the launcher exists there, use it. This honors an
    /// explicit user override and the data-root default once Relay is deployed
    /// there (the Linux layout, and the Windows dev/data layout).</description></item>
    /// <item><term>App-local fallback (Windows only).</term>
    /// <description>If the configured dir's launcher is missing and
    /// <paramref name="isWindows"/> is true, look under
    /// <c>&lt;baseDirectory&gt;/relay/</c>. A Velopack install ships Relay there
    /// (app-local inside the payload); the <c>current\</c> directory is replaced
    /// in place on update, so the path is stable. Linux does NOT use this:
    /// Relay stays at the data-root <c>relay/</c> folder.</description></item>
    /// <item><term>Otherwise <c>null</c>.</term>
    /// <description>The caller reports not-found against the configured
    /// path.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Factored as a pure function of its inputs so the precedence is
    /// unit-testable on any CI OS. The production call passes
    /// <see cref="AppContext.BaseDirectory"/> and
    /// <see cref="OperatingSystem.IsWindows"/>.
    /// </remarks>
    internal static string? ResolveLauncherPath(string configRelayDir, string baseDirectory, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(configRelayDir);
        ArgumentNullException.ThrowIfNull(baseDirectory);

        var configLauncher = Path.Combine(configRelayDir, LauncherExecutableName);
        if (File.Exists(configLauncher))
        {
            return configLauncher;
        }

        if (isWindows)
        {
            var appLocalLauncher = Path.Combine(baseDirectory, AppLocalRelayFolderName, LauncherExecutableName);
            if (File.Exists(appLocalLauncher))
            {
                return appLocalLauncher;
            }
        }

        return null;
    }

    private static LaunchResult ErrorResult(string message) =>
        new(LaunchStatus.Error, Message: message, MissingDiscoveryFields: Array.Empty<string>());
}
