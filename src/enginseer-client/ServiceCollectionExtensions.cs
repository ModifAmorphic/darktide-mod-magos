using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Modificus.Curator.EnginseerClient;

/// <summary>DI registration for the Enginseer-client library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEnginseerLaunchService"/> →
    /// <see cref="EnginseerLaunchService"/> and its supporting collaborators: the
    /// <see cref="IProcessLauncher"/> spawn seam and the platform
    /// <see cref="IPlatformLaunchStrategy"/>. The service resolves
    /// <c>IProfileService</c>, <c>ISteamService</c>, <c>IConfigLoader</c>, the
    /// strategy, and <see cref="IProcessLauncher"/> from the container (all
    /// provided by the other <c>Add&lt;Library&gt;()</c> extensions +
    /// <c>AddGeneral()</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IProcessLauncher"/> is registered with <c>TryAdd</c> so tests
    /// (and hosts wiring a custom launch hook) can pre-register an override before
    /// calling <see cref="AddEnginseerClient"/>, the same pattern the Steam
    /// library uses for its platform seams. The <see cref="IPlatformLaunchStrategy"/>
    /// is selected once, here, from the host OS (<see cref="WindowsLaunchStrategy"/>
    /// on Windows, <see cref="LinuxLaunchStrategy"/> on Linux); the launch service
    /// therefore contains no per-call OS branch. Both are <c>TryAdd</c> so tests
    /// can pre-register a concrete strategy to exercise either path on any CI OS.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddEnginseerClient(this IServiceCollection services)
    {
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            services.TryAddSingleton<IPlatformLaunchStrategy, WindowsLaunchStrategy>();
        else
            services.TryAddSingleton<IPlatformLaunchStrategy, LinuxLaunchStrategy>();

        services.AddSingleton<IEnginseerLaunchService, EnginseerLaunchService>();
        return services;
    }
}
