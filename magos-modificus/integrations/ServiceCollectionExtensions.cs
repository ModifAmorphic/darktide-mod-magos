using System.Net.Http.Headers;
using Magos.Modificus.General;
using Microsoft.Extensions.DependencyInjection;

namespace Magos.Modificus.Integrations;

/// <summary>DI registration for the Integrations library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGitHubClient"/> → <see cref="GitHubClient"/> as a
    /// typed HTTP client. The <c>HttpClient</c> (base URL + headers + optional
    /// auth) is configured from the live <see cref="MagosConfig.Integrations"/>
    /// section (<see cref="GitHubConfig"/>), resolved from the container via
    /// <see cref="IConfigLoader"/> (provided by <c>AddGeneral()</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configuration callback resolves <see cref="IConfigLoader"/> from the
    /// container on typed-client construction and reads the GitHub section from
    /// a fresh live snapshot, so a runtime config change takes effect the next
    /// time the typed client is constructed.</para>
    /// <para>
    /// Headers applied to every request: <c>User-Agent: Magos-Modificus</c>
    /// (required by GitHub) and <c>Accept: application/vnd.github+json</c>. When
    /// <see cref="GitHubConfig.Token"/> is set, it is sent as
    /// <c>Authorization: Bearer &lt;token&gt;</c> (raises the rate limit /
    /// unlocks private repos); anonymous access is used otherwise.</para>
    /// <para>
    /// The base URL is normalized to end with a trailing slash so relative
    /// request URIs resolve correctly against <c>HttpClient.BaseAddress</c>.</para>
    /// </remarks>
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Magos-Modificus");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            if (!string.IsNullOrWhiteSpace(gitHub.Token))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", gitHub.Token);
            }
        });

        return services;
    }

    private static class GitHubConfigDefaults
    {
        public const string BaseUrl = "https://api.github.com";
    }
}
