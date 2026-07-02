using System.Runtime.InteropServices;
using Magos.Modificus.Config;
using Magos.Modificus.Profiles;
using Magos.Modificus.Steam;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.EnginseerClient;

/// <summary>
/// Default <see cref="IEnginseerLaunchService"/>. Assembles the
/// <c>magos_launcher.exe</c> argument list from the profile (the
/// <c>--mod-path</c> via <see cref="IProfileService.PrepareModRoot"/>) and Steam
/// discovery (the <c>--game-binary</c>, plus the Proton wrapper + compat env vars
/// on Linux), then spawns the launcher through <see cref="IProcessLauncher"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows:</b> the launcher is invoked directly —
/// <c>Process.Start(launcher.exe, args)</c>, no Proton, no path translation.</para>
/// <para>
/// <b>Linux:</b> native Magos invokes
/// <c>&lt;proton&gt; run &lt;launcher.exe&gt; &lt;args&gt;</c> with
/// <c>STEAM_COMPAT_DATA_PATH</c> + <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c> set
/// from discovery, and the launcher's path-valued flags <c>Z:\</c>-translated
/// (the launcher runs under Wine and needs Windows paths).</para>
/// <para>
/// Registered as a singleton: it holds no per-launch state. The platform is
/// resolved once at construction (from <see cref="RuntimeInformation"/>); the OS
/// does not change at runtime. Tests force the platform via the internal
/// constructor to exercise both code paths on any CI OS.</para>
/// </remarks>
internal sealed class EnginseerLaunchService : IEnginseerLaunchService
{
    /// <summary>The launcher executable filename (a Windows binary, run under
    /// Proton on Linux). Lives in <see cref="MagosConfig.EnginseerRuntimeDir"/>.</summary>
    internal const string LauncherExecutableName = "magos_launcher.exe";

    /// <summary>
    /// The Steam app id for Darktide. The launcher defaults to this value when
    /// <c>--steam-app-id</c> is omitted; Magos relies on that default and only
    /// emits <c>--steam-app-id</c> to override it (which the current config does
    /// not surface — see <c>ServiceCollectionExtensions</c> / future config work).
    /// </summary>
    internal const int DarktideSteamAppId = 1361210;

    private readonly IProfileService _profiles;
    private readonly ISteamService _steam;
    private readonly MagosConfig _config;
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<EnginseerLaunchService> _logger;
    private readonly LaunchPlatform _platform;

    /// <summary>DI constructor — resolves the current OS for platform branching.</summary>
    public EnginseerLaunchService(
        IProfileService profiles,
        ISteamService steam,
        MagosConfig config,
        IProcessLauncher launcher,
        ILogger<EnginseerLaunchService> logger)
        : this(profiles, steam, config, launcher, logger, DetectPlatform())
    {
    }

    /// <summary>Test constructor — forces the platform so both code paths are
    /// exercisable on any CI OS (Windows-arg tests run on Linux CI, etc.).</summary>
    internal EnginseerLaunchService(
        IProfileService profiles,
        ISteamService steam,
        MagosConfig config,
        IProcessLauncher launcher,
        ILogger<EnginseerLaunchService> logger,
        LaunchPlatform platform)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _platform = platform;
    }

    /// <inheritdoc />
    public LaunchResult Launch(Guid profileId)
    {
        try
        {
            // Discovery first: if we cannot launch, do not touch the profile's
            // mod root (no point writing mods.lst for a launch that won't happen).
            var discovery = _steam.Discover();
            var missing = MissingDiscoveryFields(discovery, _platform);
            if (missing.Count > 0)
            {
                _logger.LogWarning(
                    "Discovery incomplete ({Platform}); missing: {Fields}.",
                    _platform, string.Join(", ", missing));
                return new LaunchResult(
                    LaunchStatus.DiscoveryIncomplete,
                    Message: $"Steam discovery is missing required fields: {string.Join(", ", missing)}.",
                    MissingDiscoveryFields: missing);
            }

            // PrepareModRoot writes mods.lst + ensures the mod root exists and
            // returns the --mod-path. KeyNotFoundException (unknown profile) is
            // caught below and mapped to LaunchStatus.Error.
            var modPath = _profiles.PrepareModRoot(profileId);

            var launcherPath = Path.Combine(_config.EnginseerRuntimeDir, LauncherExecutableName);
            if (!File.Exists(launcherPath))
            {
                _logger.LogError("Enginseer runtime launcher not found at {Path}.", launcherPath);
                return ErrorResult($"Enginseer runtime launcher not found at '{launcherPath}'.");
            }

            var gameBinary = discovery.DarktideGameBinaryPath!;
            var logFile = _config.Logging.LogFile;
            var logLevel = _config.Logging.Level;

            var started = _platform == LaunchPlatform.Windows
                ? LaunchWindows(launcherPath, gameBinary, modPath, logFile, logLevel)
                : LaunchLinux(discovery, launcherPath, gameBinary, modPath, logFile, logLevel);

            if (!started)
            {
                return ErrorResult($"Failed to start the Enginseer launcher at '{launcherPath}'.");
            }

            _logger.LogInformation("Launched profile {Id} via the {Platform} path.", profileId, _platform);
            return new LaunchResult(LaunchStatus.Launched, Message: null, MissingDiscoveryFields: Array.Empty<string>());
        }
        catch (KeyNotFoundException ex)
        {
            // Unknown profile (PrepareModRoot) — surfaced as Error, not thrown.
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

    // ---- Windows -----------------------------------------------------------

    private bool LaunchWindows(
        string launcherPath, string gameBinary, string modPath, string logFile, string logLevel)
    {
        // Direct invocation — no Proton, no path translation (native Windows paths).
        var args = BuildLauncherArgs(gameBinary, modPath, logFile, logLevel, translate: false);
        _logger.LogInformation("Launching (Windows) {Launcher} {Args}", launcherPath, FormatArgs(args));
        return _launcher.Start(launcherPath, args, environmentVariables: null);
    }

    // ---- Linux -------------------------------------------------------------

    private bool LaunchLinux(
        DiscoveryResult discovery,
        string launcherPath,
        string gameBinary,
        string modPath,
        string logFile,
        string logLevel)
    {
        // The launcher's OWN args (--game-binary, --mod-path) are Windows paths
        // (the launcher runs under Wine); the proton command + the launcher.exe
        // path are native Linux (Proton resolves the .exe from a native path).
        var launcherArgs = BuildLauncherArgs(gameBinary, modPath, logFile, logLevel, translate: true);

        var arguments = new List<string>(capacity: launcherArgs.Count + 2)
        {
            "run",          // proton's "run this Windows binary" subcommand
            launcherPath,   // native Linux path — Proton resolves it
        };
        arguments.AddRange(launcherArgs);

        // Both Steam compat vars are required for Proton to use the right Wine
        // prefix + find the Steam client; discovery guaranteed non-null above.
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["STEAM_COMPAT_DATA_PATH"] = discovery.CompatdataPath!,
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = discovery.SteamInstallPath!,
        };

        _logger.LogInformation(
            "Launching (Linux) {Proton} run {Launcher} {Args}",
            discovery.ProtonBinaryPath, launcherPath, FormatArgs(arguments));
        return _launcher.Start(discovery.ProtonBinaryPath!, arguments, env);
    }

    // ---- shared arg assembly ----------------------------------------------

    /// <summary>
    /// Builds the launcher's own argument list (the flags AFTER
    /// <c>magos_launcher.exe</c> / <c>... proton run launcher.exe</c>). When
    /// <paramref name="translate"/> is set (Linux), the path-valued flags are
    /// converted to Wine <c>Z:\</c> form so the launcher-under-Wine can resolve them.
    /// </summary>
    private static List<string> BuildLauncherArgs(
        string gameBinary, string modPath, string logFile, string logLevel, bool translate)
    {
        var game = translate ? WinePath.ToWine(gameBinary) : gameBinary;
        var mod = translate ? WinePath.ToWine(modPath) : modPath;

        return new List<string>
        {
            "--game-binary", game,
            "--mod-path", mod,
            "--log-file", logFile,
            "--log-level", logLevel,
        };
    }

    /// <summary>
    /// The discovery fields the current OS requires but discovery could not
    /// resolve. Field names mirror <see cref="DiscoveryResult"/>'s properties so
    /// the UI can map them to prompt fields. By the Steam service's construction
    /// this is equivalent to <see cref="DiscoveryStatus"/> != Complete (Complete
    /// ⟺ every OS-required field is non-null) — derived from the fields directly
    /// so the result and the missing-field list cannot diverge.
    /// </summary>
    private static IReadOnlyList<string> MissingDiscoveryFields(DiscoveryResult d, LaunchPlatform platform)
    {
        var missing = new List<string>();

        // Both platforms need Steam + the game binary.
        if (d.SteamInstallPath is null) missing.Add(nameof(DiscoveryResult.SteamInstallPath));
        if (d.DarktideGameBinaryPath is null) missing.Add(nameof(DiscoveryResult.DarktideGameBinaryPath));

        // Linux additionally needs the Wine prefix (compatdata) + Proton.
        if (platform == LaunchPlatform.Linux)
        {
            if (d.CompatdataPath is null) missing.Add(nameof(DiscoveryResult.CompatdataPath));
            if (d.ProtonBinaryPath is null) missing.Add(nameof(DiscoveryResult.ProtonBinaryPath));
        }

        return missing;
    }

    private static LaunchResult ErrorResult(string message) =>
        new(LaunchStatus.Error, Message: message, MissingDiscoveryFields: Array.Empty<string>());

    private static LaunchPlatform DetectPlatform() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? LaunchPlatform.Windows : LaunchPlatform.Linux;

    private static string FormatArgs(IReadOnlyList<string> args) => string.Join(' ', args);
}
