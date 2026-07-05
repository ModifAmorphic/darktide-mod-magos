using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Nxm;

/// <summary>DI registration for the Nxm library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the nxm router, IPC server, no-op handler defaults, and the
    /// platform <see cref="INxmHandlerRegistrar"/>. The composition root binds +
    /// starts the IPC server after building the provider (the pipe bind is the
    /// single-instance claim).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Handler override convention (last registration wins).</b> The two
    /// handler defaults (<see cref="NoOpNxmModDownloadHandler"/> /
    /// <see cref="NoOpNxmOAuthCallbackHandler"/>) are registered here with plain
    /// <c>AddSingleton</c> (not <c>TryAdd</c>). Stage 2 / 3 register real
    /// implementations AFTER <c>AddNxm()</c> via
    /// <c>services.AddSingleton&lt;INxmOAuthCallbackHandler, ...&gt;()</c>
    /// (or the mod-download equivalent); MS DI resolves the LAST registration,
    /// so the real handler supersedes the no-op. The router captures whichever
    /// handler is resolved at its (singleton) construction.
    /// </para>
    /// <para>
    /// <b>Platform registrar.</b> <see cref="WindowsNxmHandlerRegistrar"/> is
    /// registered on Windows, <see cref="LinuxNxmHandlerRegistrar"/> on Linux.
    /// Resolving <see cref="INxmHandlerRegistrar"/> on any other platform fails
    /// fast (honest failure rather than a silent no-op). Each registrar is
    /// annotated <c>[SupportedOSPlatform]</c>; selection happens here at DI
    /// registration via <see cref="OperatingSystem.IsWindows"/> /
    /// <see cref="OperatingSystem.IsLinux"/> (the canonical CA1416 guards),
    /// mirroring <c>SteamRegistryReader</c> + <c>IPlatformLaunchStrategy</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddNxm(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // No-op handler defaults; plain AddSingleton so later registrations win.
        services.AddSingleton<INxmModDownloadHandler, NoOpNxmModDownloadHandler>();
        services.AddSingleton<INxmOAuthCallbackHandler, NoOpNxmOAuthCallbackHandler>();

        services.AddSingleton<INxmRouter, NxmRouter>();
        services.AddSingleton<NxmIpcServer>();

        // Platform registrar. Each factory helper is [SupportedOSPlatform]-
        // annotated; the call sites are guarded by the canonical CA1416
        // OperatingSystem.Is*() checks.
        if (OperatingSystem.IsWindows())
            services.AddSingleton<INxmHandlerRegistrar>(CreateWindowsRegistrar);
        else if (OperatingSystem.IsLinux())
            services.AddSingleton<INxmHandlerRegistrar>(CreateLinuxRegistrar);

        return services;
    }

    [SupportedOSPlatform("windows")]
    private static INxmHandlerRegistrar CreateWindowsRegistrar(IServiceProvider sp) =>
        new WindowsNxmHandlerRegistrar(
            NxmHandlerPaths.GetHandlerExePath(),
            sp.GetRequiredService<ILogger<WindowsNxmHandlerRegistrar>>());

    [SupportedOSPlatform("linux")]
    private static INxmHandlerRegistrar CreateLinuxRegistrar(IServiceProvider sp) =>
        new LinuxNxmHandlerRegistrar(
            NxmHandlerPaths.GetHandlerExePath(),
            sp.GetRequiredService<ILogger<LinuxNxmHandlerRegistrar>>());
}

