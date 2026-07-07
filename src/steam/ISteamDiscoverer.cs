namespace Modificus.Curator.Steam;

/// <summary>
/// Steam + Darktide (+ Linux: compatdata + Proton) discovery for one platform.
/// Implementations are platform-specific (<c>LinuxSteamDiscoverer</c>,
/// <c>WindowsSteamDiscoverer</c>) and share the platform-agnostic mechanics via
/// <see cref="SteamDiscoveryCore"/> (composition). The active implementation is
/// selected once, at DI registration, from
/// <see cref="SteamDiscoveryOptions.Platform"/> -- so <see cref="ISteamService"/>
/// itself never branches on platform.
/// </summary>
/// <remarks>
/// Splitting discovery behind this interface removes the prior
/// <c>SteamService.DiscoverLinux</c>/<c>DiscoverWindows</c> dispatch: the
/// service delegates to whichever discoverer the container wired (the
/// <c>Platform</c> knob exists precisely so cross-platform logic is exercisable
/// on one OS, e.g. Windows discovery on Linux CI).
/// </remarks>
internal interface ISteamDiscoverer
{
    /// <summary>
    /// Probes the platform-appropriate Steam install locations and resolves the
    /// Steam install, Darktide install, and (Linux) compatdata + Proton version.
    /// Never throws on missing pieces -- those are reported via
    /// <see cref="DiscoveryResult.Status"/> + the nullable fields (the escape hatch).
    /// </summary>
    DiscoveryResult Discover();
}
