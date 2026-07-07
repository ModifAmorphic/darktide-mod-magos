using Modificus.Curator.Steam;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.EnginseerClient;

/// <summary>
/// Windows <see cref="IPlatformLaunchStrategy"/>. Invokes
/// <c>curator_launcher.exe</c> directly -- no Proton, no path translation (native
/// Windows paths). Selected at DI registration when the host is Windows.
/// </summary>
internal sealed class WindowsLaunchStrategy : IPlatformLaunchStrategy
{
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<WindowsLaunchStrategy> _logger;

    public WindowsLaunchStrategy(IProcessLauncher launcher, ILogger<WindowsLaunchStrategy> logger)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Windows";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredDiscoveryFields(DiscoveryResult discovery)
    {
        var missing = new List<string>();

        // Windows needs Steam + the game binary (compatdata/Proton are unused -- native).
        if (discovery.SteamInstallPath is null) missing.Add(nameof(DiscoveryResult.SteamInstallPath));
        if (discovery.DarktideGameBinaryPath is null) missing.Add(nameof(DiscoveryResult.DarktideGameBinaryPath));

        return missing;
    }

    /// <inheritdoc />
    public bool Start(string launcherPath, DiscoveryResult discovery, string gameBinary, string modPath, string logFile)
    {
        // Direct invocation -- no Proton, no path translation (native Windows paths).
        // `discovery` is unused on Windows: the caller already extracted gameBinary
        // from it, and Windows needs no Proton/compat context.
        var args = BuildLauncherArgs(gameBinary, modPath, logFile);
        _logger.LogInformation("Launching (Windows) {Launcher} {Args}", launcherPath, FormatArgs(args));
        return _launcher.Start(launcherPath, args, environmentVariables: null);
    }

    /// <summary>
    /// Builds the launcher's own argument list (the flags AFTER
    /// <c>curator_launcher.exe</c>). Paths pass through verbatim -- Windows needs no
    /// <c>Z:\</c> translation.
    /// </summary>
    /// <remarks>
    /// <c>--log-level</c> is intentionally NOT emitted: <c>CuratorConfig.Logging.Level</c>
    /// is a Serilog level name for Curator's own log, but the Enginseer shell's level
    /// vocabulary differs -- forwarding the Serilog name silently mis-resolved levels.
    /// The two logs are decoupled; the launcher's <c>info</c> default is used.
    /// </remarks>
    internal static List<string> BuildLauncherArgs(string gameBinary, string modPath, string logFile) =>
        new()
        {
            "--game-binary", gameBinary,
            "--mod-path", modPath,
            "--log-file", logFile,
        };

    private static string FormatArgs(IReadOnlyList<string> args) => string.Join(' ', args);
}
