using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Nxm.Tests;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddNxm"/>: the router, IPC server,
/// and no-op mod-download default are registered; the platform registrar is
/// registered on the host OS (Windows or Linux); the OAuth handler seam is
/// GONE (Stage 2 removed it); and the "last registration wins" handler override
/// convention works (a later AddSingleton supersedes the no-op default).
/// </summary>
public sealed class AddNxmServiceCollectionTests
{
    [Fact]
    public void AddNxm_registers_router_server_and_mod_download_handler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNxm();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<NxmRouter>(provider.GetRequiredService<INxmRouter>());
        Assert.NotNull(provider.GetRequiredService<NxmIpcServer>());
        // The no-op default is an internal type; resolve via the interface.
        Assert.NotNull(provider.GetRequiredService<INxmModDownloadHandler>());
    }

    [Fact]
    public void AddNxm_registers_platform_registrar_on_host_os()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNxm();

        using var provider = services.BuildServiceProvider();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.NotNull(provider.GetRequiredService<INxmHandlerRegistrar>());
        }
    }

    [Fact]
    public async Task Later_handler_registration_overrides_the_no_op_default()
    {
        // Documents the "last registration wins" convention Stage 3 relies on:
        // register AddNxm() first, then AddSingleton a real handler. MS DI
        // resolves the LAST one.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNxm();
        var real = new RecordingModHandler();
        services.AddSingleton<INxmModDownloadHandler>(real);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<INxmModDownloadHandler>();
        Assert.Same(real, resolved);

        // The router, resolved after the override, captures the real handler.
        var router = provider.GetRequiredService<INxmRouter>();
        await router.RouteAsync("nxm://warhammer40kdarktide/mods/8/files/5820");
        Assert.Single(real.Handled);
    }

    private sealed class RecordingModHandler : INxmModDownloadHandler
    {
        public List<NxmModDownloadUrl> Handled { get; } = new();
        public Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default)
        {
            Handled.Add(url);
            return Task.CompletedTask;
        }
    }
}
