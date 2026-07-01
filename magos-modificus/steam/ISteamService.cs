namespace Magos.Modificus.Steam;

/// <summary>
/// Steam discovery + game-running detection. Steam **discovers** everything
/// needed to launch Darktide modded on the current OS (Steam install, Darktide
/// install, compatdata, Proton version) and reports missing pieces via
/// <see cref="DiscoveryResult.Status"/>; it does NOT set env vars or invoke
/// Proton — that is Enginseer-client's job (consuming the <see cref="DiscoveryResult"/>).
/// </summary>
/// <remarks>
/// <para><b>Phase 1 → Phase 3 stability:</b> the discovery result is a flat
/// record of nullables — Phase 3 (UI) reads it and the null fields drive the
/// escape-hatch prompt form. A future Phase (non-steam shortcuts, Phase 5) adds
/// methods here; the interface is designed to grow cleanly.</para>
/// </remarks>
public interface ISteamService
{
    /// <summary>
    /// Probes the OS-appropriate Steam install locations and resolves the
    /// Steam install, Darktide install, compatdata, and Proton version. Never
    /// throws on missing pieces — those are reported via <see cref="DiscoveryResult.Status"/>
    /// + the nullable fields (the escape hatch).
    /// </summary>
    DiscoveryResult Discover();

    /// <summary>
    /// Whether Darktide is currently running. Cross-platform best-effort check
    /// against the game's process name; Phase 1 uses the simple name match
    /// (Linux-under-Proton naming may differ — refine if it proves wrong).
    /// </summary>
    bool IsGameRunning();
}

/// <summary>
/// The outcome of a Steam discovery pass. Fields are nullable: a null means
/// "couldn't resolve this — the UI should prompt for it" (the escape hatch).
/// <see cref="Status"/> summarizes whether everything critical for the current
/// OS was found.
/// </summary>
/// <param name="SteamInstallPath">Steam client dir → <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c>.</param>
/// <param name="DarktideGameBinaryPath">Native path to <c>Darktide.exe</c>
/// (Enginseer-client Z:\-translates on Linux for <c>--game-binary</c>).</param>
/// <param name="CompatdataPath">Wine prefix → <c>STEAM_COMPAT_DATA_PATH</c> (Linux only).</param>
/// <param name="ProtonBinaryPath">The <c>proton</c> script for <c>proton run</c> (Linux only).</param>
/// <param name="ProtonVersion">Informational label (e.g. "Proton - Experimental").</param>
/// <param name="Status">Complete / Partial / Failed — see <see cref="DiscoveryStatus"/>.</param>
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
