using Magos.Modificus.Steam;

namespace Magos.Modificus.EnginseerClient;

/// <summary>
/// One platform's launch strategy: the spawn (via <see cref="IProcessLauncher"/>),
/// the discovery fields that platform requires, and a label for logging. The
/// active implementation is selected once, at DI registration, from the runtime
/// OS — so <see cref="EnginseerLaunchService"/> orchestrates the platform-agnostic
/// launch flow and contains no per-launch OS branch.
/// </summary>
/// <remarks>
/// Splitting the launch path behind this interface removes the prior
/// <c>LaunchWindows</c>/<c>LaunchLinux</c> dispatch in the launch service. The
/// strategy owns exactly what varies by platform; everything else (discovery,
/// <c>PrepareModRoot</c>, the launcher-existence check, result mapping, the
/// try/catch contract) stays in the orchestrator.
/// </remarks>
internal interface IPlatformLaunchStrategy
{
    /// <summary>A short label ("Windows" / "Linux") for log messages.</summary>
    string Name { get; }

    /// <summary>
    /// The discovery fields this platform requires but discovery could not
    /// resolve. Field names mirror <see cref="DiscoveryResult"/>'s properties so
    /// the UI can map them to prompt fields. Equivalent to
    /// <see cref="DiscoveryStatus"/> != <see cref="DiscoveryStatus.Complete"/>
    /// for this platform — derived from the fields directly so the result and
    /// the missing-field list cannot diverge.
    /// </summary>
    IReadOnlyList<string> RequiredDiscoveryFields(DiscoveryResult discovery);

    /// <summary>
    /// Spawns <c>magos_launcher.exe</c> for this platform. Windows: a direct
    /// invocation of <paramref name="launcherPath"/> with native (untranslated)
    /// args. Linux: <c>&lt;proton&gt; run &lt;launcherPath&gt; &lt;args&gt;</c>
    /// with both <c>STEAM_COMPAT_*</c> env vars and the path-valued flags
    /// <c>Z:\</c>-translated (the launcher runs under Wine). Fire-and-forget —
    /// returns <c>true</c> if the process started.
    /// </summary>
    /// <param name="launcherPath">Native path to <c>magos_launcher.exe</c>.</param>
    /// <param name="discovery">The resolved discovery (Linux reads the Proton +
    /// compat paths + Steam install from it; Windows ignores it — it already has
    /// <paramref name="gameBinary"/>).</param>
    /// <param name="gameBinary">The resolved Darktide game binary (non-null —
    /// discovery completeness was checked by the caller).</param>
    /// <param name="modPath">The prepared mod root (the <c>--mod-path</c>).</param>
    /// <param name="logFile">The shell log file (the <c>--log-file</c>).</param>
    bool Start(string launcherPath, DiscoveryResult discovery, string gameBinary, string modPath, string logFile);
}
