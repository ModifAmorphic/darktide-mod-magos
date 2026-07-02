using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Magos.Modificus.Steam;

/// <summary>DI registration for the Steam library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISteamService"/> → <see cref="SteamService"/> and its
    /// supporting services (discovery options + platform seams). Resolves the
    /// real OS defaults via <see cref="SteamDiscoveryOptions.CreateDefault"/>.
    /// </summary>
    /// <remarks>
    /// Supporting services (<see cref="SteamDiscoveryOptions"/>,
    /// <see cref="ISteamRegistryReader"/>, <see cref="IProcessLookup"/>) are
    /// registered with <c>TryAdd</c> so tests (and hosts with custom paths) can
    /// pre-register overrides — the discovery pipeline is then fully exercisable
    /// against fixture layouts. The <see cref="IProcessLookup"/> implementation
    /// is selected once, here, from the host OS: <see cref="LinuxProcessLookup"/>
    /// on Linux (matches <c>/proc</c> argv[0]-stem; kernel <c>comm</c> is
    /// unreliable under Proton) and <see cref="WinProcessLookup"/> elsewhere
    /// (matches process comm via <see cref="System.Diagnostics.Process"/>).
    /// </remarks>
    public static IServiceCollection AddSteam(this IServiceCollection services)
    {
        services.TryAddSingleton(_ => SteamDiscoveryOptions.CreateDefault());
        services.TryAddSingleton<ISteamRegistryReader, SteamRegistryReader>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            services.TryAddSingleton<IProcessLookup, LinuxProcessLookup>();
        else
            services.TryAddSingleton<IProcessLookup, WinProcessLookup>();

        services.AddSingleton<ISteamService, SteamService>();
        return services;
    }
}
