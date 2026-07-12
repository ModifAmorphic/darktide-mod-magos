namespace Modificus.Curator.Integrations;

/// <summary>
/// Build-time constants for the Nexus OAuth flow. The <c>client_id</c> is the
/// SSO identifier Nexus issues when the application is registered for public
/// use: per their API Acceptable Use Policy, a public-facing app is registered
/// by contacting support with a testing build, after which Nexus issues a
/// "slug" for the SSO, and that slug is the client_id. Until Curator is
/// registered, <c>"modificus-curator"</c> is a development placeholder (live
/// authorize will not recognize it); the API-key path is the validated auth
/// method in the meantime, and OAuth is exercised against stubbed endpoints.
/// </summary>
/// <remarks>
/// <b>This is a build-time constant, not config and not an env var.</b> When
/// Nexus issues the slug at registration, it lands here as a code change.
/// Modificus Curator has no env-var pattern (config is file-based via
/// <see cref="General.IConfigLoader"/>).
/// </remarks>
internal static class NexusOAuthConstants
{
    /// <summary>The OAuth client identifier sent on authorize + token requests.</summary>
    public const string ClientId = "modificus-curator";

    /// <summary>
    /// The OAuth/OIDC scope. <c>openid</c> for the id token, <c>profile</c> +
    /// <c>email</c> for the userinfo claims Nexus returns at <c>/oauth/userinfo</c>.
    /// </summary>
    public const string Scope = "openid profile email";

    /// <summary>
    /// The OAuth/OIDC protocol version sent in the <c>Protocol-Version</c> header
    /// on every API request (the MO2/NMA convention).
    /// </summary>
    public const string ProtocolVersion = "1.0.0";

    /// <summary>The application name sent in <c>Application-Name</c> + the
    /// <c>User-Agent</c> prefix on every API request.</summary>
    public const string ApplicationName = "Modificus-Curator";

    /// <summary>The application version sent in <c>Application-Version</c> +
    /// the <c>User-Agent</c> on every API request. Derived from the assembly;
    /// falls back to <c>"0.0.0"</c> when the version is unavailable (tests).</summary>
    public static string ApplicationVersion { get; } =
        typeof(NexusOAuthConstants).Assembly.GetName().Version?.ToString(fieldCount: 3) ?? "0.0.0";

    /// <summary>The path appended to the OAuth base URL for the loopback
    /// callback. The loopback listener binds the port; the URL fragment is
    /// fixed.</summary>
    public const string CallbackPath = "/callback";

    /// <summary>The default OAuth flow timeout (matches NMA's
    /// <c>OAuthJob</c>). The user has this long to complete the browser
    /// consent; on expiry the loopback listener stops + the service surfaces a
    /// "Login timed out" error.</summary>
    public static readonly TimeSpan DefaultFlowTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The User-Agent header value, combining the application name +
    /// version (the MO2/NMA convention).</summary>
    public static string UserAgent => $"{ApplicationName}/{ApplicationVersion}";
}
