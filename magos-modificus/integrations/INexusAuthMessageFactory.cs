using Magos.Modificus.Config;

namespace Magos.Modificus.Integrations;

/// <summary>
/// Builds a fully-authenticated <see cref="HttpRequestMessage"/> for the Nexus
/// v1 client. Each factory implementation owns one auth method: the API-key
/// factory adds <c>apikey: &lt;key&gt;</c>; the OAuth factory adds
/// <c>Authorization: Bearer &lt;token&gt;</c> and handles 401-reactive refresh.
/// A selector implementation picks the right one based on the live
/// <see cref="NexusConfig.AuthMethod"/>.
/// </summary>
/// <remarks>
/// The factory does NOT own the request body or the relative path (those come
/// from <see cref="NexusClient"/>'s per-call code). It owns the per-request auth
/// state only: which headers to apply, whether credentials are available, and
/// how to react when the server rejects the credentials (refresh, or surface).
/// </remarks>
public interface INexusAuthMessageFactory
{
    /// <summary>
    /// Builds a fresh <see cref="HttpRequestMessage"/> for
    /// <paramref name="method"/> + <paramref name="uri"/>, with the auth headers
    /// + the app-identification headers (<c>Application-Name</c>,
    /// <c>User-Agent</c>, etc.) applied. Called once per request; called again
    /// when the client retries after a successful
    /// <see cref="OnUnauthorizedAsync"/>.
    /// </summary>
    /// <remarks>
    /// The caller (<see cref="NexusClient"/>) owns disposing the returned
    /// message. A factory MUST NOT cache or reuse the message.
    /// </remarks>
    ValueTask<HttpRequestMessage> CreateAsync(HttpMethod method, Uri uri, CancellationToken ct);

    /// <summary>
    /// Called by the client on a 401 response. The OAuth factory refreshes the
    /// access token via the persisted refresh token + persists the new tokens,
    /// returning <c>true</c> to request a single retry. The API-key factory
    /// returns <c>false</c> (no refresh is possible; the caller surfaces a
    /// re-login prompt). Returning <c>false</c> means "give up the retry";
    /// the client then throws <see cref="NexusApiException"/> for the original
    /// 401.
    /// </summary>
    /// <returns><c>true</c> if the credentials were refreshed and the request
    /// should be retried; <c>false</c> if no refresh was possible.</returns>
    ValueTask<bool> OnUnauthorizedAsync(CancellationToken ct);

    /// <summary>
    /// Whether this factory currently has usable credentials to apply (an API
    /// key in the config for the API-key factory; OAuth tokens for the OAuth
    /// factory). The selector's <c>None</c> implementation returns
    /// <c>false</c>; the client throws
    /// <see cref="NexusNotAuthenticatedException"/> before sending a request
    /// when this is <c>false</c>.
    /// </summary>
    ValueTask<bool> IsAuthenticatedAsync(CancellationToken ct);
}

/// <summary>
/// Read-only access to the persisted OAuth tokens + the ability to refresh them.
/// The OAuth message factory depends on this (rather than the full
/// <see cref="INexusAuthService"/>) to break the DI cycle (the auth service
/// depends on <see cref="INexusClient"/>, which depends on the factory).
/// </summary>
/// <remarks>
/// <b>Live-read.</b> <see cref="GetOAuthTokens"/> reads the current state from
/// the config file on each call (via <see cref="General.IConfigLoader"/>), so a
/// refresh that persisted new tokens + a concurrent request that started with
/// the old token both observe the latest write. Magos is single-instance; the
/// only writer is the auth flow itself.
/// </remarks>
public interface INexusTokenStore
{
    /// <summary>
    /// The current persisted OAuth tokens (live read from config), or
    /// <c>null</c> when OAuth is not configured (no tokens yet, or signed out).
    /// </summary>
    NexusOAuthTokens? GetOAuthTokens();

    /// <summary>
    /// Refreshes the access token via OidcClient's refresh API using the
    /// persisted refresh token, persists the new tokens to config, and returns
    /// them. Returns <c>null</c> on failure (refresh token revoked / expired /
    /// network error); the caller surfaces a re-login prompt rather than
    /// falling back to API key.
    /// </summary>
    /// <param name="ct">Cancellation token (the refresh network call honors
    /// it).</param>
    /// <returns>The refreshed tokens (now persisted), or <c>null</c> when the
    /// refresh failed.</returns>
    Task<NexusOAuthTokens?> RefreshAsync(CancellationToken ct);
}
