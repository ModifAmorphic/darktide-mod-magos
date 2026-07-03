using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;
using Magos.Modificus.General;

namespace Magos.Modificus.General.Tests;

/// <summary>
/// Proves the General DI registration is resolvable: AddGeneral() registers
/// the config, logger factory, logging, config loader, and app-state store so
/// any component can take <c>ILogger&lt;T&gt;</c> / <see cref="MagosConfig"/> /
/// <see cref="IAppStateStore"/> via constructor injection.
/// </summary>
public sealed class GeneralServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGeneral_registers_resolvable_services()
    {
        var config = MagosConfig.CreateDefault();
        using var loggerFactory = new LoggerFactory();

        var services = new ServiceCollection();
        services.AddGeneral(config, loggerFactory);
        var provider = services.BuildServiceProvider();

        Assert.Same(config, provider.GetRequiredService<MagosConfig>());
        Assert.Same(loggerFactory, provider.GetRequiredService<ILoggerFactory>());
        Assert.NotNull(provider.GetService<ILogger<GeneralServiceCollectionExtensionsTests>>());
        Assert.IsType<ConfigLoader>(provider.GetRequiredService<IConfigLoader>());
        Assert.IsType<AppStateStore>(provider.GetRequiredService<IAppStateStore>());
    }

    [Fact]
    public void AddGeneral_allows_an_IAppStateStore_override_via_TryAdd()
    {
        // TryAdd so a test/host can pre-register an override before AddGeneral.
        var config = MagosConfig.CreateDefault();
        using var loggerFactory = new LoggerFactory();
        var custom = new CustomAppStateStore();

        var services = new ServiceCollection();
        services.AddSingleton<IAppStateStore>(custom);
        services.AddGeneral(config, loggerFactory);
        var provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<IAppStateStore>());
    }

    private sealed class CustomAppStateStore : IAppStateStore
    {
        public Guid? ActiveProfileId { get; set; }
    }
}
