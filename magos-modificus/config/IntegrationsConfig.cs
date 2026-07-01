namespace Magos.Modificus.Config;

/// <summary>
/// External-service integration settings (mod sources). Bound from the
/// <c>Integrations</c> section of <see cref="MagosConfig"/> by the config loader
/// in <c>Magos.Modificus.General</c>. Every field carries a default so an absent
/// section yields a usable object.
/// </summary>
public sealed class IntegrationsConfig
{
    /// <summary>GitHub Releases client settings.</summary>
    public GitHubConfig GitHub { get; set; } = new();
}

/// <summary>
/// GitHub Releases client settings. The base URL defaults to the public GitHub
/// REST API; an optional personal access token raises the rate limit / unlocks
/// private repos (Phase 1: no token-management UI — supply via config only).
/// </summary>
public sealed class GitHubConfig
{
    /// <summary>
    /// The GitHub REST API root, without a trailing slash. Defaults to the
    /// public endpoint; override for GitHub Enterprise (<c>https://&lt;host&gt;/api/v3</c>).
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.github.com";

    /// <summary>
    /// An optional personal access token sent as a <c>Bearer</c> token. When
    /// unset, requests are anonymous (public releases need no auth).
    /// </summary>
    public string? Token { get; set; }
}
