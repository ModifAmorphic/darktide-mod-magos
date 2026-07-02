using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Steam;

/// <summary>
/// Windows <see cref="ISteamDiscoverer"/>. Resolves the Steam install (registry
/// first via <see cref="ISteamRegistryReader"/>, then the default path), derives
/// the Darktide install, and reports Compatdata/Proton as null (Windows is
/// native — they are unused). All platform-specific steps live here; the shared
/// mechanics (root resolution, library reading, Darktide probing) come from
/// <see cref="SteamDiscoveryCore"/>. Selected at DI registration when
/// <see cref="SteamDiscoveryOptions.Platform"/> is <see cref="DiscoveryPlatform.Windows"/>.
/// </summary>
internal sealed class WindowsSteamDiscoverer : ISteamDiscoverer
{
    private readonly SteamDiscoveryCore _core;
    private readonly SteamDiscoveryOptions _options;
    private readonly ISteamRegistryReader _registry;
    private readonly ILogger<WindowsSteamDiscoverer> _logger;

    public WindowsSteamDiscoverer(
        SteamDiscoveryCore core,
        SteamDiscoveryOptions options,
        ISteamRegistryReader registry,
        ILogger<WindowsSteamDiscoverer> logger)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public DiscoveryResult Discover()
    {
        var warnings = new List<string>();

        // Registry first (authoritative when present), then the default path.
        var registryPath = _registry.GetSteamPath();
        var resolved = _core.ResolveRoot(
            new SteamDiscoveryCore.RootCandidate(registryPath, IsFlatpak: false, FromRegistry: true),
            new SteamDiscoveryCore.RootCandidate(_options.WindowsDefaultSteamRoot, IsFlatpak: false, FromRegistry: false));

        if (resolved.Path is null)
        {
            _logger.LogWarning("Steam install not found (registry + default both invalid).");
            return SteamDiscoveryCore.Failed(warnings);
        }

        warnings.Add(resolved.FromRegistry
            ? "Steam install resolved from registry."
            : "Steam install resolved from default path (registry yielded nothing).");

        var libraries = _core.ReadLibraries(resolved.Path, warnings);
        var darktide = _core.FindDarktide(libraries);

        var status = StatusForWindows(resolved.Path, darktide);
        _logger.LogInformation(
            "Windows discovery: {Status} (steam={Steam}, darktide={Darktide}).",
            status, resolved.Path, darktide ?? "(missing)");

        // Compatdata/Proton are null by design on Windows (native — not used).
        return new DiscoveryResult(
            SteamInstallPath: resolved.Path,
            DarktideGameBinaryPath: darktide,
            CompatdataPath: null,
            ProtonBinaryPath: null,
            ProtonVersion: null,
            Status: status,
            Warnings: warnings);
    }

    private static DiscoveryStatus StatusForWindows(string? steam, string? darktide) =>
        steam is null ? DiscoveryStatus.Failed
        : darktide is null ? DiscoveryStatus.Partial
        : DiscoveryStatus.Complete;
}
