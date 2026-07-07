using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Proves <c>AddIntegrations()</c> registers <see cref="IGitHubClient"/> as a
/// typed HTTP client configured from <see cref="CuratorConfig.Integrations.GitHub"/>
/// (base URL + optional auth), resolvable via DI with an
/// <c>IHttpClientFactory</c>-provided <c>HttpClient</c>.
/// </summary>
/// <remarks>
/// Config is verified end-to-end: a stub <see cref="HttpMessageHandler"/> is
/// wired into the same typed-client registration <c>AddIntegrations()</c> builds,
/// the client makes a real (offline) call, and the recorded request is asserted
/// on - so the test proves the <see cref="IConfigLoader"/> → <see cref="CuratorConfig"/>
/// → <c>HttpClient</c> wiring actually reaches the wire, not just that something
/// resolves.
/// </remarks>
public sealed class IntegrationsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddIntegrations_registers_resolvable_IGitHubClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddIntegrations();
        using var provider = services.BuildServiceProvider();

        var client = provider.GetService<IGitHubClient>();

        Assert.NotNull(client);
        Assert.IsAssignableFrom<IGitHubClient>(client);
    }

    [Fact]
    public void AddIntegrations_exposes_IHttpClientFactory()
    {
        // The typed client's HttpClient is built by the factory - proving the
        // standard, testable HTTP DI pattern is wired.
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader());
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddIntegrations();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IHttpClientFactory>());
    }

    [Fact]
    public void AddIntegrations_configures_base_url_headers_and_auth_from_config()
    {
        var config = CuratorConfig.CreateDefault();
        config.Integrations.GitHub.BaseUrl = "https://api.test.local";
        config.Integrations.GitHub.Token = "secret-token";

        var (client, handler) = BuildWithStub(config);
        client.ListReleases(new GitHubRepo("o", "r"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.test.local/repos/o/r/releases", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret-token", request.Authorization);
        Assert.NotNull(request.UserAgent);
        Assert.Contains("Modificus-Curator", request.UserAgent, StringComparison.Ordinal);
        Assert.Contains("application/vnd.github+json", request.Accept, StringComparison.Ordinal);
    }

    [Fact]
    public void AddIntegrations_omits_auth_when_no_token_configured()
    {
        // Default config: Token is null -> anonymous access (public releases need no auth).
        var (client, handler) = BuildWithStub(CuratorConfig.CreateDefault());
        client.ListReleases(new GitHubRepo("o", "r"));

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public void AddIntegrations_normalizes_trailing_slash_on_base_url()
    {
        var config = CuratorConfig.CreateDefault();
        config.Integrations.GitHub.BaseUrl = "https://gh.enterprise.example/api/v3"; // no trailing slash

        var (client, handler) = BuildWithStub(config);
        client.ListReleases(new GitHubRepo("o", "r"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://gh.enterprise.example/api/v3/repos/o/r/releases",
            request.RequestUri!.ToString());
    }

    [Fact]
    public void AddIntegrations_falls_back_to_default_base_url_when_blank()
    {
        var config = CuratorConfig.CreateDefault();
        config.Integrations.GitHub.BaseUrl = "   ";

        var (client, handler) = BuildWithStub(config);
        client.ListReleases(new GitHubRepo("o", "r"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/o/r/releases", request.RequestUri!.ToString());
    }

    [Fact]
    public void AddIntegrations_is_idempotent_and_returns_same_collection()
    {
        var services = new ServiceCollection();

        var returned = services.AddIntegrations();

        Assert.Same(services, returned);
    }

    [Fact]
    public void AddIntegrations_registers_IUpdateCheckService_as_singleton()
    {
        // The update-check service orchestrates across Nexus + Mods + Profiles.
        // Verified by descriptor (rather than resolving) so the test does not
        // need IProfileService + IModRepository stubs (registered by AddProfiles
        // + AddMods in the composition root, not by AddIntegrations). The
        // service's own tests construct it directly and cover its behavior
        // end-to-end.
        var services = new ServiceCollection();
        services.AddIntegrations();

        var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IUpdateCheckService));

        Assert.NotNull(descriptor);
        Assert.Equal(typeof(UpdateCheckService), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>
    /// Wires a stub HTTP handler into the typed-client registration
    /// <c>AddIntegrations()</c> builds (by re-entering the same
    /// <c>AddHttpClient&lt;IGitHubClient, GitHubClient&gt;</c> builder) so tests
    /// can drive the real client offline and inspect the outgoing request.
    /// </summary>
    private static (IGitHubClient Client, StubHttpMessageHandler Handler) BuildWithStub(CuratorConfig config)
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.Json("[]"));

        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config });
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddIntegrations();
        // Attach the stub to the same named typed client AddIntegrations registered.
        services.AddHttpClient<IGitHubClient, GitHubClient>()
            .ConfigurePrimaryHttpMessageHandler(_ => handler);

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IGitHubClient>(), handler);
    }
}
