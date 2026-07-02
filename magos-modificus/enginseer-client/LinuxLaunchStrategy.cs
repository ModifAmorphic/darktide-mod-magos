using Magos.Modificus.Steam;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.EnginseerClient;

/// <summary>
/// Linux <see cref="IPlatformLaunchStrategy"/>. Magos runs natively (not
/// Proton-wrapped); <c>magos_launcher.exe</c> is a Windows binary, so this
/// invokes it under <c>&lt;proton&gt; run</c> using Darktide's own compatdata as
/// the Wine prefix, sets both <c>STEAM_COMPAT_*</c> env vars, and
/// <c>Z:\</c>-translates the launcher's path-valued flags. Selected at DI
/// registration when the host is Linux.
/// </summary>
internal sealed class LinuxLaunchStrategy : IPlatformLaunchStrategy
{
    private readonly IProcessLauncher _launcher;
    private readonly ILogger<LinuxLaunchStrategy> _logger;

    public LinuxLaunchStrategy(IProcessLauncher launcher, ILogger<LinuxLaunchStrategy> logger)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Linux";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredDiscoveryFields(DiscoveryResult discovery)
    {
        var missing = new List<string>();

        // Linux needs Steam + the game binary AND the Wine prefix (compatdata) + Proton.
        if (discovery.SteamInstallPath is null) missing.Add(nameof(DiscoveryResult.SteamInstallPath));
        if (discovery.DarktideGameBinaryPath is null) missing.Add(nameof(DiscoveryResult.DarktideGameBinaryPath));
        if (discovery.CompatdataPath is null) missing.Add(nameof(DiscoveryResult.CompatdataPath));
        if (discovery.ProtonBinaryPath is null) missing.Add(nameof(DiscoveryResult.ProtonBinaryPath));

        return missing;
    }

    /// <inheritdoc />
    public bool Start(string launcherPath, DiscoveryResult discovery, string gameBinary, string modPath, string logFile)
    {
        // The launcher's OWN args (--game-binary, --mod-path, --log-file) are
        // Windows paths (the launcher runs under Wine); the proton command + the
        // launcher.exe path are native Linux (Proton resolves the .exe from a
        // native path).
        var launcherArgs = BuildLauncherArgs(gameBinary, modPath, logFile);

        var arguments = new List<string>(capacity: launcherArgs.Count + 2)
        {
            "run",          // proton's "run this Windows binary" subcommand
            launcherPath,   // native Linux path — Proton resolves it
        };
        arguments.AddRange(launcherArgs);

        // Both Steam compat vars are required for Proton to use the right Wine
        // prefix + find the Steam client; RequiredDiscoveryFields guaranteed both
        // non-null above.
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

    /// <summary>
    /// Builds the launcher's own argument list (the flags AFTER
    /// <c>... proton run launcher.exe</c>). The path-valued flags are converted
    /// to Wine <c>Z:\</c> form so the launcher-under-Wine can resolve them.
    /// </summary>
    /// <remarks>
    /// <c>--log-file</c> is a path the launcher-under-Wine opens, so it must be
    /// <c>Z:\</c>-translated too (otherwise <c>magos_enginseer.log</c> can't be
    /// written where Magos expects). <c>--log-level</c> is intentionally NOT
    /// emitted (the shell's level vocabulary differs from Serilog's).
    /// </remarks>
    internal static List<string> BuildLauncherArgs(string gameBinary, string modPath, string logFile) =>
        new()
        {
            "--game-binary", WinePath.ToWine(gameBinary),
            "--mod-path", WinePath.ToWine(modPath),
            "--log-file", WinePath.ToWine(logFile),
        };

    private static string FormatArgs(IReadOnlyList<string> args) => string.Join(' ', args);
}
