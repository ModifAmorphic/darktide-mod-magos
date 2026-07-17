namespace Modificus.Curator.Steam;

/// <summary>
/// Steam + Darktide (+ Linux: compatdata + Proton) discovery for one platform.
/// One implementation per <see cref="DiscoveryPlatform"/>; the active one is
/// selected from <see cref="SteamDiscoveryOptions.Platform"/> so
/// <see cref="ISteamService"/> never branches on platform.
/// </summary>
internal interface ISteamDiscoverer
{
    /// <summary>
    /// Probes the platform-appropriate Steam install locations and resolves the
    /// Steam install, Darktide install, and (Linux) compatdata + Proton version.
    /// Never throws on missing pieces: those are reported via
    /// <see cref="DiscoveryResult.Status"/> + the nullable fields (the escape hatch).
    /// </summary>
    DiscoveryResult Discover();
}
