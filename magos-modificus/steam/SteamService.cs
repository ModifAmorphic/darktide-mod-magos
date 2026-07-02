using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Steam;

/// <summary>
/// <see cref="ISteamService"/> implementation. A thin orchestrator: discovery is
/// delegated to the platform <see cref="ISteamDiscoverer"/> (selected once at DI
/// registration from <see cref="SteamDiscoveryOptions.Platform"/>) and the
/// game-running check to <see cref="IProcessLookup"/>. Holds no per-call state
/// and contains no platform dispatch — every OS-specific concern lives behind a
/// polymorphic collaborator wired at the composition root.
/// </summary>
/// <remarks>
/// Registered as a singleton. <see cref="Discover"/> never throws on missing
/// pieces — those are reported via <see cref="DiscoveryResult.Status"/> + the
/// nullable fields.
/// </remarks>
internal sealed class SteamService : ISteamService
{
    private readonly ISteamDiscoverer _discoverer;
    private readonly SteamDiscoveryOptions _options;
    private readonly IProcessLookup _processes;

    public SteamService(
        ISteamDiscoverer discoverer,
        SteamDiscoveryOptions options,
        IProcessLookup processes)
    {
        _discoverer = discoverer ?? throw new ArgumentNullException(nameof(discoverer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
    }

    /// <inheritdoc />
    public DiscoveryResult Discover() => _discoverer.Discover();

    /// <inheritdoc />
    public bool IsGameRunning() => _processes.IsRunning(_options.GameProcessName);
}
