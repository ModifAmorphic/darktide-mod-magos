using System.Net.Http.Headers;
using Duende.IdentityModel.OidcClient.Browser;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>DI registration for the Integrations library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the GitHub + Nexus clients. The GitHub client is a typed
    /// <c>HttpClient</c> configured from the live <see cref="CuratorConfig.Integrations.GitHub"/>
    /// section. The Nexus client is a typed <c>HttpClient</c> with per-request
    /// auth applied by the auth message factory selector (which reads
    /// <see cref="NexusConfig.AuthMethod"/> live). The Nexus auth service +
    /// the loopback <see cref="IBrowser"/> + the token store are singletons.
    /// The acquisition service (download + extract + place orchestration over
    /// <see cref="INexusClient"/> + <see cref="IModImportService"/>) is a
    /// singleton; both the nxm download handler and the per-mod update
    /// button resolve it. The update-check service (one-call v2 GraphQL
    /// <c>modsByUid</c> batch query for the active profile's
    /// LatestPolicy + NexusSource mods) is a singleton; the mod-list view binds badges to
    /// its <see cref="IUpdateCheckService.LastResult"/> + subscribes to
    /// <see cref="IUpdateCheckService.CheckCompleted"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The GitHub configuration callback resolves <see cref="IConfigLoader"/> from
    /// the container on typed-client construction and reads the GitHub section from
    /// a fresh live snapshot, so a runtime config change takes effect the next
    /// time the typed client is constructed.</para>
    /// <para>
    /// <b>GitHub headers applied to every request:</b> <c>User-Agent: Modificus-Curator</c>
    /// (required by GitHub) and <c>Accept: application/vnd.github+json</c>. When
    /// <see cref="GitHubConfig.Token"/> is set, it is sent as
    /// <c>Authorization: Bearer &lt;token&gt;</c> (raises the rate limit /
    /// unlocks private repos); anonymous access is used otherwise.</para>
    /// <para>
    /// The GitHub base URL is normalized to end with a trailing slash so relative
    /// request URIs resolve correctly against <c>HttpClient.BaseAddress</c>. The
    /// Nexus API base URL is normalized the same way.</para>
    /// </remarks>
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddGitHub(services);
        AddNexus(services);
        AddAcquisition(services);
        AddUpdateCheck(services);

        return services;
    }

    private static void AddGitHub(IServiceCollection services)
    {
        services.AddHttpClient<IGitHubClient, GitHubClient>((serviceProvider, client) =>
        {
            var config = serviceProvider.GetRequiredService<IConfigLoader>().Load();
            var gitHub = config.Integrations.GitHub;

            // Trim whitespace + trailing slashes, then re-append one slash so
            // relative request URIs resolve against BaseAddress predictably
            // (the classic BaseAddress footgun). A blank value falls back to the
            // public GitHub API.
            var baseUrl = (gitHub.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
            {
                baseUrl = GitHubConfigDefaults.BaseUrl;
            }

            client.BaseAddress = new Uri(baseUrl + "/", UriKind.Absolute);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Modificus-Curator");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            if (!string.IsNullOrWhiteSpace(gitHub.Token))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", gitHub.Token);
            }
        });
    }

    private static void AddNexus(IServiceCollection services)
    {
        // The typed Nexus client. Base URL + default headers are configured here
        // (the auth headers are per-request via the auth factory; the BaseAddress
        // is the API root, used for everything except the OAuth userinfo endpoint,
        // which the client composes from the OAuth base URL it reads live).
        services.AddHttpClient<INexusClient, NexusClient>((serviceProvider, client) =>
        {
            var nexus = serviceProvider.GetRequiredService<IConfigLoader>().Load().Integrations.Nexus;
            var baseUrl = (nexus.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
            {
                baseUrl = NexusConfigDefaults.BaseUrl;
            }
            client.BaseAddress = new Uri(baseUrl + "/", UriKind.Absolute);
        });

        // Auth message factories (selected live by NexusConfig.AuthMethod).
        // The selector reads AuthMethod per request; the inner factories are
        // singletons that read their credentials live from config.
        services.AddSingleton<ApiKeyMessageFactory>();
        services.AddSingleton<NoneMessageFactory>();
        // OAuth factory depends on INexusTokenStore (= NexusOAuthTokenStore).
        services.AddSingleton<OAuth2MessageFactory>();
        services.AddSingleton<INexusAuthMessageFactory, NexusAuthMessageFactorySelector>();

        // The loopback IBrowser (production default; the OAuth flow constructs a
        // fresh OidcClient per login so this is shared, not per-call).
        services.AddSingleton<IBrowser, LoopbackBrowser>();

        // The Nexus OAuth token store owns the OidcClient + the live token
        // persistence + the loopback login + the refresh path. It is the smaller
        // surface the OAuth message factory depends on (INexusTokenStore),
        // SEPARATE from the auth service so the DI graph has no cycle
        // (AuthService -> Client -> AuthFactory -> TokenStore; TokenStore
        // depends only on config + the browser).
        services.AddSingleton<NexusOAuthTokenStore>();
        services.AddSingleton<INexusTokenStore>(sp => sp.GetRequiredService<NexusOAuthTokenStore>());

        // The Nexus auth service (OAuth login + API-key validate + sign-out +
        // current-state read). Depends on the token store + the v1 client.
        services.AddSingleton<NexusAuthService>();
        services.AddSingleton<INexusAuthService>(sp => sp.GetRequiredService<NexusAuthService>());
    }

    /// <summary>
    /// Registers the mod acquisition service. A thin orchestrator over
    /// <see cref="INexusClient"/> + <see cref="IModImportService"/> + a plain
    /// <c>HttpClient</c> (from the factory, for the raw CDN archive download).
    /// Singleton: holds no per-call state.
    /// </summary>
    private static void AddAcquisition(IServiceCollection services)
    {
        services.AddSingleton<IModAcquisitionService, ModAcquisitionService>();
    }

    /// <summary>
    /// Registers the Nexus update-check service. Queries the v2 GraphQL
    /// <c>modsByUid</c> batch endpoint for the active profile's LatestPolicy +
    /// NexusSource mods via <see cref="INexusClient"/> +
    /// <see cref="IModRepository"/> + <see cref="Profiles.IProfileService"/>.
    /// Singleton: holds the last result (<see cref="IUpdateCheckService.LastResult"/>)
    /// so the mod-list view can bind badges to it without re-running the check, and
    /// publishes updates through <see cref="IUpdateCheckService.CheckCompleted"/>.
    /// </summary>
    private static void AddUpdateCheck(IServiceCollection services)
    {
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
    }

    private static class GitHubConfigDefaults
    {
        public const string BaseUrl = "https://api.github.com";
    }

    private static class NexusConfigDefaults
    {
        public const string BaseUrl = "https://api.nexusmods.com";
    }
}
