namespace Magos.Modificus.Integrations;

/// <summary>
/// Build-time constants for the Nexus OAuth flow. The <c>client_id</c> is an
/// identifier we choose, not a registered secret: RFC 8252 loopback redirects
/// require no client registration with the OAuth provider. MO2 ships
/// <c>"modorganizer2"</c>, NMA ships <c>"nma"</c>; Magos ships
/// <c>"magos-modificus"</c>.
/// </summary>
/// <remarks>
/// <b>This is a build-time constant, not config and not an env var.</b> Magos
/// Modificus has no env-var pattern (config is file-based via
/// <see cref="General.IConfigLoader"/>); introducing one just for the client_id
/// is unjustified. MO2's <c>MO2_NEXUS_CLIENT_ID</c> env override is their
/// pattern, not ours; do not copy it. If the client_id ever needs to change,
/// that is a code change.
/// </remarks>
internal static class NexusOAuthConstants
{
    /// <summary>The OAuth client identifier sent on authorize + token requests.</summary>
    public const string ClientId = "magos-modificus";

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
    public const string ApplicationName = "Magos-Modificus";

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
