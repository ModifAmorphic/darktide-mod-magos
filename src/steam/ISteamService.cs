namespace Modificus.Curator.Steam;

/// <summary>
/// Steam discovery + game-running detection. Steam **discovers** everything
/// needed to launch Darktide modded on the current OS (Steam install, Darktide
/// install, compatdata, Proton version) and reports missing pieces via
/// <see cref="DiscoveryResult.Status"/>; it does NOT set env vars or invoke
/// Proton -- that is Enginseer-client's job (consuming the <see cref="DiscoveryResult"/>).
/// </summary>
/// <remarks>
/// <para><b>Discovery result shape:</b> the discovery result is a flat
/// record of nullables -- the UI reads it and the null fields drive the
/// escape-hatch prompt form. The interface exposes discovery + game-running
/// detection only.</para>
/// </remarks>
public interface ISteamService
{
    /// <summary>
    /// Validates + heals + selectively persists discovery, then returns the
    /// result. Delegates the platform <c>ISteamDiscoverer</c> (selected once at
    /// DI registration from <see cref="SteamDiscoveryOptions.Platform"/>) only
    /// when a field needs healing; when every persisted override is valid (path
    /// exists on disk) the discoverer is skipped entirely (fast path). Never
    /// throws on missing pieces: those are reported via
    /// <see cref="DiscoveryResult.Status"/> + the nullable fields (the escape
    /// hatch).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Validate + heal + persist:</b> see
    /// <see cref="SteamService"/>'s remarks for the four-step pipeline. In
    /// short: read the live <see cref="DiscoveryConfig"/> user overrides,
    /// validate each platform-relevant field's path on disk, heal the invalid
    /// ones from the discoverer (one run), persist ONLY the healed fields back
    /// to config (preserving valid fields + any hand-edit between the read +
    /// save), and return a result with the final paths.</para>
    /// <para>
    /// <b>Platform-gating:</b> on Windows only Steam install + Darktide binary
    /// are checked + healed (compatdata + Proton are Linux-only and stay null).
    /// On Linux all four are checked + healed.</para>
    /// </remarks>
    DiscoveryResult Discover();

    /// <summary>
    /// Whether Darktide is currently running. Cross-platform best-effort check
    /// against the game's process name; this uses the simple name match
    /// (Linux-under-Proton naming may differ, refine if it proves wrong).
    /// </summary>
    bool IsGameRunning();
}

/// <summary>
/// The outcome of a Steam discovery pass. Fields are nullable: a null means
/// "couldn't resolve this -- the UI should prompt for it" (the escape hatch).
/// <see cref="Status"/> summarizes whether everything critical for the current
/// OS was found.
/// </summary>
/// <param name="SteamInstallPath">Steam client dir → <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c>.</param>
/// <param name="DarktideGameBinaryPath">Native path to <c>Darktide.exe</c>
/// (Enginseer-client Z:\-translates on Linux for <c>--game-binary</c>).</param>
/// <param name="CompatdataPath">Wine prefix → <c>STEAM_COMPAT_DATA_PATH</c> (Linux only).</param>
/// <param name="ProtonBinaryPath">The <c>proton</c> script for <c>proton run</c> (Linux only).</param>
/// <param name="ProtonVersion">Informational label (e.g. "Proton - Experimental").</param>
/// <param name="Status">Complete / Partial / Failed -- see <see cref="DiscoveryStatus"/>.</param>
/// <param name="Warnings">Non-fatal notes (e.g. "Flatpak Steam detected", Proton-selection reason).</param>
public sealed record DiscoveryResult(
    string? SteamInstallPath,
    string? DarktideGameBinaryPath,
    string? CompatdataPath,
    string? ProtonBinaryPath,
    string? ProtonVersion,
    DiscoveryStatus Status,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Coarse status of a discovery pass:
/// <list type="bullet">
/// <item><term>Complete</term><description>Every critical field for the current OS is non-null.</description></item>
/// <item><term>Partial</term><description>Steam was located but some critical fields are missing
/// (the nullables indicate what the UI should prompt for).</description></item>
/// <item><term>Failed</term><description>Could not even locate Steam (UI prompts for the Steam dir).</description></item>
/// </list>
/// </summary>
public enum DiscoveryStatus
{
    Complete,
    Partial,
    Failed,
}

/// <summary>
/// The platform discovery runs against. Production picks this from the runtime
/// OS; tests can force a platform to exercise cross-platform logic on one OS.
/// Darktide ships on Windows (native) and Linux (Proton) only.
/// </summary>
public enum DiscoveryPlatform
{
    /// <summary>Linux: discovers Steam + Darktide + compatdata + Proton.</summary>
    Linux,

    /// <summary>Windows: discovers Steam + Darktide only (native; Proton/compatdata unused).</summary>
    Windows,
}
