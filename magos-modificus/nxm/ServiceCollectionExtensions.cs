using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Nxm;

/// <summary>DI registration for the Nxm library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the nxm router, IPC server, no-op mod-download default, and the
    /// platform <see cref="INxmHandlerRegistrar"/>. The composition root binds +
    /// starts the IPC server after building the provider (single-instance is
    /// enforced via process enumeration in <see cref="SingleInstanceGuard"/>
    /// before the pipe bind; the pipe bind itself is non-fatal and degrades
    /// gracefully on failure).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Handler override convention (last registration wins).</b> The no-op
    /// default (<see cref="NoOpNxmModDownloadHandler"/>) is registered here with
    /// plain <c>AddSingleton</c> (not <c>TryAdd</c>). Stage 3 registers a real
    /// implementation AFTER <c>AddNxm()</c> via
    /// <c>services.AddSingleton&lt;INxmModDownloadHandler, ...&gt;()</c>; MS DI
    /// resolves the LAST registration, so the real handler supersedes the no-op.
    /// The router captures whichever handler is resolved at its (singleton)
    /// construction.
    /// </para>
    /// <para>
    /// <b>Stage 2 removed the <c>INxmOAuthCallbackHandler</c> seam.</b> Magos
    /// OAuth uses a loopback HTTP redirect (RFC 8252), independent of the nxm
    /// handler; the <c>nxm://oauth/callback</c> URL shape is still parsed so the
    /// router can recognize it, but it is logged + dropped rather than routed.
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

        // No-op mod-download default; plain AddSingleton so a later registration wins.
        services.AddSingleton<INxmModDownloadHandler, NoOpNxmModDownloadHandler>();

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

