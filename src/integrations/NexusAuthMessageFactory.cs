using System.Net.Http.Headers;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Integrations;

/// <summary>
/// The API-key auth factory. Adds <c>apikey: &lt;key&gt;</c> + the shared
/// app-identification headers. Static (no refresh); a 401 surfaces a clear
/// "API key invalid/expired" error to the caller via
/// <see cref="OnUnauthorizedAsync"/> returning <c>false</c> (no retry).
/// </summary>
/// <remarks>
/// Selected by the <see cref="NexusAuthMessageFactorySelector"/> when
/// <see cref="NexusConfig.AuthMethod"/> is <see cref="NexusAuthMethod.ApiKey"/>.
/// </remarks>
internal sealed class ApiKeyMessageFactory : INexusAuthMessageFactory
{
    private readonly IConfigLoader _configLoader;

    public ApiKeyMessageFactory(IConfigLoader configLoader)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
    }

    /// <inheritdoc />
    public ValueTask<HttpRequestMessage> CreateAsync(HttpMethod method, Uri uri, CancellationToken ct)
    {
        var key = _configLoader.Load().Integrations.Nexus.ApiKey ?? string.Empty;

        var request = new HttpRequestMessage(method, uri);
        ApplyAppHeaders(request);
        // The apikey header is a Nexus custom scheme (lowercase per the API docs);
        // not a standard auth scheme, so it goes through TryAddWithoutValidation.
        request.Headers.TryAddWithoutValidation("apikey", key);
        return ValueTask.FromResult(request);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The API-key flow has no refresh. Returning <c>false</c> tells the client
    /// not to retry; the original 401 propagates as a
    /// <see cref="NexusApiException"/> with status 401, and the Integrations
    /// dialog surfaces a re-prompt for the key.
    /// </remarks>
    public ValueTask<bool> OnUnauthorizedAsync(CancellationToken ct) => ValueTask.FromResult(false);

    /// <inheritdoc />
    public ValueTask<bool> IsAuthenticatedAsync(CancellationToken ct)
    {
        var key = _configLoader.Load().Integrations.Nexus.ApiKey;
        return ValueTask.FromResult(!string.IsNullOrWhiteSpace(key));
    }

    /// <summary>
    /// Applies the shared app-identification headers (<c>Application-Name</c>,
    /// <c>Application-Version</c>, <c>Protocol-Version</c>, <c>User-Agent</c>)
    /// that Nexus expects on every request. MO2 + NMA send the same set.
    /// </summary>
    internal static void ApplyAppHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Application-Name", NexusOAuthConstants.ApplicationName);
        request.Headers.TryAddWithoutValidation("Application-Version", NexusOAuthConstants.ApplicationVersion);
        request.Headers.TryAddWithoutValidation("Protocol-Version", NexusOAuthConstants.ProtocolVersion);
        // User-Agent must be a single product token; the default User-Agent
        // header parser rejects the slash in "Modificus-Curator/<ver>" only if it
        // cannot parse the version. Use TryAddWithoutValidation to avoid any
        // parser quirk on the version format.
        request.Headers.TryAddWithoutValidation("User-Agent", NexusOAuthConstants.UserAgent);
    }
}

/// <summary>
/// The OAuth 2.0 bearer auth factory. Adds
/// <c>Authorization: Bearer &lt;access_token&gt;</c> + the shared
/// app-identification headers. Does 401-reactive refresh: on a 401, calls
/// <see cref="INexusTokenStore.RefreshAsync"/> (which refreshes via OidcClient's
/// refresh API + persists the new tokens); on success returns <c>true</c> so the
/// client retries the request once with the new access token.
/// </summary>
/// <remarks>
/// Selected by the <see cref="NexusAuthMessageFactorySelector"/> when
/// <see cref="NexusConfig.AuthMethod"/> is <see cref="NexusAuthMethod.OAuth"/>.
/// </remarks>
internal sealed class OAuth2MessageFactory : INexusAuthMessageFactory
{
    private readonly INexusTokenStore _tokens;
    private readonly ILogger<OAuth2MessageFactory> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public OAuth2MessageFactory(INexusTokenStore tokens, ILogger<OAuth2MessageFactory> logger)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ValueTask<HttpRequestMessage> CreateAsync(HttpMethod method, Uri uri, CancellationToken ct)
    {
        var tokens = _tokens.GetOAuthTokens();
        var request = new HttpRequestMessage(method, uri);
        ApiKeyMessageFactory.ApplyAppHeaders(request);
        if (tokens is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        }
        return ValueTask.FromResult(request);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Refreshes the access token via the token store (which owns the OidcClient
    /// refresh call + the persistence). Refresh is serialized through a
    /// semaphore so concurrent 401s on in-flight requests coalesce into a single
    /// refresh: the first caller refreshes, subsequent callers see the freshly
    /// persisted token via <see cref="INexusTokenStore.GetOAuthTokens"/> + skip
    /// the refresh network call.</para>
    /// <para>
    /// On a refresh failure (refresh token revoked / expired / network error),
    /// the store returns <c>null</c> and this method returns <c>false</c>: the
    /// client surfaces the original 401, and the Integrations dialog prompts for
    /// re-login. There is no fallback to API key.</para>
    /// </remarks>
    public async ValueTask<bool> OnUnauthorizedAsync(CancellationToken ct)
    {
        // Coalesce concurrent refreshes: the first 401 wins the semaphore,
        // performs the refresh, and persists. When the next caller acquires the
        // semaphore, it re-reads the (now-current) tokens + skips the network
        // call if a fresh token is already in place. The "fresh" check is the
        // access-token string changing; that is sufficient because refresh
        // always issues a new access token (per RFC 6749 §5.1).
        var stale = _tokens.GetOAuthTokens()?.AccessToken;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _tokens.GetOAuthTokens();
            if (current is not null && current.AccessToken != stale)
            {
                // Another caller already refreshed between our read + the gate
                // acquisition. The retry will use the new token.
                return true;
            }

            var refreshed = await _tokens.RefreshAsync(ct).ConfigureAwait(false);
            if (refreshed is null)
            {
                _logger.LogWarning(
                    "Nexus OAuth refresh failed; user re-login required (refresh token revoked or expired).");
                return false;
            }

            _logger.LogInformation("Nexus OAuth access token refreshed; retrying the failed request.");
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> IsAuthenticatedAsync(CancellationToken ct) =>
        ValueTask.FromResult(_tokens.GetOAuthTokens() is not null);
}

/// <summary>
/// The "no auth" factory. <see cref="IsAuthenticatedAsync"/> always returns
/// <c>false</c>; <see cref="CreateAsync"/> returns an unauthenticated request
/// (only the app-identification headers applied). Selected by the
/// <see cref="NexusAuthMessageFactorySelector"/> when
/// <see cref="NexusConfig.AuthMethod"/> is <see cref="NexusAuthMethod.None"/>;
/// the client throws <see cref="NexusNotAuthenticatedException"/> before
/// sending, so the request this builds never reaches the wire.
/// </summary>
internal sealed class NoneMessageFactory : INexusAuthMessageFactory
{
    public ValueTask<HttpRequestMessage> CreateAsync(HttpMethod method, Uri uri, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, uri);
        ApiKeyMessageFactory.ApplyAppHeaders(request);
        return ValueTask.FromResult(request);
    }

    public ValueTask<bool> OnUnauthorizedAsync(CancellationToken ct) => ValueTask.FromResult(false);

    public ValueTask<bool> IsAuthenticatedAsync(CancellationToken ct) => ValueTask.FromResult(false);
}

/// <summary>
/// Selects the right inner factory based on the live
/// <see cref="NexusConfig.AuthMethod"/>. Read once per request: an OAuth login
/// mid-session takes effect on the next call (the Integrations dialog sets the
/// method, the next client call re-reads it). No fallback: each method's
/// credentials are required, and the matching inner factory surfaces a clear
/// error when they are missing.
/// </summary>
internal sealed class NexusAuthMessageFactorySelector : INexusAuthMessageFactory
{
    private readonly IConfigLoader _configLoader;
    private readonly ApiKeyMessageFactory _apiKey;
    private readonly OAuth2MessageFactory _oauth;
    private readonly NoneMessageFactory _none;

    public NexusAuthMessageFactorySelector(
        IConfigLoader configLoader,
        ApiKeyMessageFactory apiKey,
        OAuth2MessageFactory oauth,
        NoneMessageFactory none)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _none = none ?? throw new ArgumentNullException(nameof(none));
    }

    private INexusAuthMessageFactory Select() =>
        _configLoader.Load().Integrations.Nexus.AuthMethod switch
        {
            NexusAuthMethod.OAuth => _oauth,
            NexusAuthMethod.ApiKey => _apiKey,
            _ => _none,
        };

    /// <inheritdoc />
    public ValueTask<HttpRequestMessage> CreateAsync(HttpMethod method, Uri uri, CancellationToken ct) =>
        Select().CreateAsync(method, uri, ct);

    /// <inheritdoc />
    public ValueTask<bool> OnUnauthorizedAsync(CancellationToken ct) => Select().OnUnauthorizedAsync(ct);

    /// <inheritdoc />
    public ValueTask<bool> IsAuthenticatedAsync(CancellationToken ct) => Select().IsAuthenticatedAsync(ct);
}
