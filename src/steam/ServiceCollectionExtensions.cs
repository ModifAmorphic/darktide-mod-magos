using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Steam;

/// <summary>DI registration for the Steam library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISteamService"/> → <see cref="SteamService"/> and its
    /// supporting services (the shared <see cref="SteamDiscoveryCore"/> + the
    /// platform discoverer + platform seams). Resolves the real OS defaults via
    /// <see cref="SteamDiscoveryOptions.CreateDefault"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The platform <see cref="ISteamDiscoverer"/> is selected by a factory keyed
    /// on <see cref="SteamDiscoveryOptions.Platform"/> -- NOT the runtime OS. This
    /// preserves the test-injectable <c>Platform</c> knob: a fixture (or host)
    /// overrides <see cref="SteamDiscoveryOptions"/> to force a platform, and the
    /// discoverer follows it (so Windows discovery runs on Linux CI and vice
    /// versa). <see cref="SteamService"/> therefore contains no platform dispatch.
    /// </para>
    /// <para>
    /// Supporting services (<see cref="SteamDiscoveryOptions"/>,
    /// <see cref="ISteamRegistryReader"/>, <see cref="IProcessLookup"/>) are
    /// registered with <c>TryAdd</c> so tests (and hosts with custom paths) can
    /// pre-register overrides -- the discovery pipeline is then fully exercisable
    /// against fixture layouts. <see cref="ISteamRegistryReader"/> is registered
    /// ONLY on Windows (fail-fast: resolving it on Linux surfaces the
    /// misconfiguration rather than silently no-opping a Windows-only capability).
    /// The <see cref="IProcessLookup"/> implementation is selected once, here,
    /// from the host OS: <see cref="LinuxProcessLookup"/> on Linux (matches
    /// <c>/proc</c> argv[0]-stem; kernel <c>comm</c> is unreliable under Proton)
    /// and <see cref="WinProcessLookup"/> elsewhere (matches process comm via
    /// <see cref="System.Diagnostics.Process"/>).
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSteam(this IServiceCollection services)
    {
        services.TryAddSingleton(_ => SteamDiscoveryOptions.CreateDefault());
        services.TryAddSingleton<SteamDiscoveryCore>();

        // Discoverer follows the (overridable) Platform knob, not the runtime OS.
        services.TryAddSingleton<ISteamDiscoverer>(sp =>
            sp.GetRequiredService<SteamDiscoveryOptions>().Platform == DiscoveryPlatform.Linux
                ? new LinuxSteamDiscoverer(
                    sp.GetRequiredService<SteamDiscoveryCore>(),
                    sp.GetRequiredService<SteamDiscoveryOptions>(),
                    sp.GetRequiredService<ILogger<LinuxSteamDiscoverer>>())
                : new WindowsSteamDiscoverer(
                    sp.GetRequiredService<SteamDiscoveryCore>(),
                    sp.GetRequiredService<SteamDiscoveryOptions>(),
                    sp.GetRequiredService<ISteamRegistryReader>(),
                    sp.GetRequiredService<ILogger<WindowsSteamDiscoverer>>()));

        // Windows-only capability: NOT registered on Linux (fail-fast if resolved).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            services.TryAddSingleton<ISteamRegistryReader, SteamRegistryReader>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            services.TryAddSingleton<IProcessLookup, LinuxProcessLookup>();
        else
            services.TryAddSingleton<IProcessLookup, WinProcessLookup>();

        services.AddSingleton<ISteamService, SteamService>();
        return services;
    }
}
