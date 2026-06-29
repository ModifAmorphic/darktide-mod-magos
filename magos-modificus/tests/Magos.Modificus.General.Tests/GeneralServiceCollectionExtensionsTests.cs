using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;
using Magos.Modificus.General;

namespace Magos.Modificus.General.Tests;

/// <summary>
/// Proves the General DI registration is resolvable: AddGeneral() registers
/// the config, logger factory, logging, and config loader so any component can
/// take <c>ILogger&lt;T&gt;</c> / <see cref="MagosConfig"/> via constructor
/// injection.
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
    }
}
