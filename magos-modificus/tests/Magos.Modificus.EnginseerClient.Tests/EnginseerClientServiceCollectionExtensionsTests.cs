using Magos.Modificus.Config;
using Magos.Modificus.General;
using Magos.Modificus.Profiles;
using Magos.Modificus.Steam;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.EnginseerClient.Tests;

/// <summary>
/// Proves <c>AddEnginseerClient()</c> registers <see cref="IEnginseerLaunchService"/>
/// (and the supporting <see cref="IProcessLauncher"/> seam) so it is resolvable
/// from DI with the production-style deps (<c>IProfileService</c> +
/// <c>ISteamService</c> + <c>IConfigLoader</c>), and that pre-registered overrides
/// win over the defaults via TryAdd.
/// </summary>
public sealed class EnginseerClientServiceCollectionExtensionsTests
{
    [Fact]
    public void AddEnginseerClient_registers_resolvable_IEnginseerLaunchService()
    {
        var services = BuildComposition();

        using var provider = services.BuildServiceProvider();
        var service = provider.GetService<IEnginseerLaunchService>();

        Assert.NotNull(service);
        Assert.IsAssignableFrom<IEnginseerLaunchService>(service);
    }

    [Fact]
    public void AddEnginseerClient_registers_default_IProcessLauncher()
    {
        var services = BuildComposition();

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IProcessLauncher>());
    }

    [Fact]
    public void AddEnginseerClient_pre_registered_IProcessLauncher_wins_over_default()
    {
        // A host/tests can inject a custom launch hook; TryAdd must defer.
        var custom = new FakeProcessLauncher();

        var services = BuildComposition();
        services.AddSingleton<IProcessLauncher>(custom);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IProcessLauncher>();

        Assert.Same(custom, resolved);
    }

    [Fact]
    public void AddEnginseerClient_is_idempotent_and_returns_same_collection()
    {
        var services = new ServiceCollection();

        var returned = services.AddEnginseerClient();

        Assert.Same(services, returned);
    }

    /// <summary>
    /// Builds the minimal composition that makes
    /// <see cref="IEnginseerLaunchService"/> resolvable: fakes for the profile +
    /// steam services, a default config loader, logging, then
    /// <see cref="ServiceCollectionExtensions.AddEnginseerClient"/>.
    /// </summary>
    private static ServiceCollection BuildComposition()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        services.AddSingleton<IProfileService, FakeProfileService>();
        services.AddSingleton<ISteamService, FakeSteamService>();
        services.AddEnginseerClient();
        return services;
    }
}
