using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Magos.Modificus.EnginseerClient;

/// <summary>DI registration for the Enginseer-client library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEnginseerLaunchService"/> →
    /// <see cref="EnginseerLaunchService"/> and the supporting
    /// <see cref="IProcessLauncher"/> seam. The service resolves
    /// <c>IProfileService</c>, <c>ISteamService</c>, <c>MagosConfig</c>, and
    /// <see cref="IProcessLauncher"/> from the container (all provided by the
    /// other <c>Add&lt;Library&gt;()</c> extensions + <c>AddGeneral()</c>).
    /// </summary>
    /// <remarks>
    /// <para><see cref="IProcessLauncher"/> is registered with <c>TryAdd</c> so
    /// tests (and hosts wiring a custom launch hook) can pre-register an override
    /// before calling <see cref="AddEnginseerClient"/> — the same pattern the
    /// Steam library uses for its platform seams (<c>ISteamRegistryReader</c>,
    /// <c>IProcessLookup</c>).</para>
    /// </remarks>
    public static IServiceCollection AddEnginseerClient(this IServiceCollection services)
    {
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<IEnginseerLaunchService, EnginseerLaunchService>();
        return services;
    }
}
