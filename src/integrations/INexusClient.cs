namespace Modificus.Curator.Integrations;

/// <summary>
/// The Nexus Mods v1 REST API client. Surface: auth validation (one method per
/// auth mode) + the endpoints the rest of the app calls through (mod updates,
/// download links, mod-page metadata). Mirrors the existing
/// <see cref="IGitHubClient"/> shape: typed <c>HttpClient</c> via
/// <c>AddHttpClient&lt;INexusClient, NexusClient&gt;</c>, auth applied per-request
/// by the configured <see cref="INexusAuthMessageFactory"/>, and the parsed
/// rate-limit headers carried on every response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Auth is per-request and explicit.</b> The auth factory selection reads
/// <c>NexusConfig.AuthMethod</c> live: <see cref="NexusAuthMethod.OAuth"/> uses
/// the OAuth bearer factory (with 401-reactive refresh);
/// <see cref="NexusAuthMethod.ApiKey"/> uses the apikey factory (static, no
/// refresh); <see cref="NexusAuthMethod.None"/> throws
/// <see cref="NexusNotAuthenticatedException"/>. There is <b>no fallback</b>:
/// the user's explicit choice in the Integrations dialog is the single source of
/// truth for which method is active.</para>
/// <para>
/// <b>v1, not v3.</b> Endpoints are the stable production paths (grounded against
/// NMA's <c>NexusApiClient.cs</c>). The v3 openapi surfaces the mod endpoints as
/// Experimental; this client does not use v3.</para>
/// <para>
/// <b>Rate limits.</b> Every response carries the parsed <c>x-rl-*</c> headers
/// in its <see cref="Response{T}.RateLimits"/>. The update-check service consumes
/// them to back off; the auth flow just parses + logs them.</para>
/// </remarks>
public interface INexusClient
{
    /// <summary>
    /// Validates the configured API key + returns the user's identity. Hits
    /// <c>GET /v1/users/validate.json</c>. Used by the Integrations dialog's
    /// API-key Validate button. Throws <see cref="NexusApiException"/> on a
    /// non-2xx (the caller surfaces "API key invalid/expired"); throws
    /// <see cref="NexusNotAuthenticatedException"/> when auth is <c>None</c> or
    /// not <c>ApiKey</c>.
    /// </summary>
    Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches the OAuth user info (identity + membership roles). Hits
    /// <c>GET /oauth/userinfo</c> on the OAuth base URL. Used by the
    /// Integrations dialog after a successful OAuth login. Throws
    /// <see cref="NexusApiException"/> on a non-2xx; throws
    /// <see cref="NexusNotAuthenticatedException"/> when auth is not <c>OAuth</c>.
    /// </summary>
    Task<Response<OAuthUserInfo>> GetOAuthUserInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists mods updated in the past <paramref name="period"/> for
    /// <paramref name="gameDomain"/>. Hits
    /// <c>GET /v1/games/{domain}/mods/updated.json?period={1d|1w|1m}</c>. The
    /// one-call-per-game update check the update-check service builds on.
    /// </summary>
    Task<Response<ModUpdate[]>> ModUpdatesAsync(
        string gameDomain,
        NexusPeriod period,
        CancellationToken ct = default);

    /// <summary>
    /// Premium-user download links for the given file. Hits
    /// <c>GET /v1/games/{domain}/mods/{modId}/files/{fileId}/download_link.json</c>
    /// (premium-only endpoint; the acquisition service consumes the returned CDN
    /// URLs).
    /// </summary>
    Task<Response<DownloadLink[]>> DownloadLinksAsync(
        string gameDomain,
        int modId,
        int fileId,
        CancellationToken ct = default);

    /// <summary>
    /// Free-user download links for the given file, keyed by the per-file token
    /// from the <c>nxm://</c> URL. Hits the same endpoint as the premium overload
    /// + <c>?key={nxmKey}&amp;expires={epoch}</c>. The acquisition service consumes
    /// the returned CDN URLs.
    /// </summary>
    Task<Response<DownloadLink[]>> DownloadLinksAsync(
        string gameDomain,
        int modId,
        int fileId,
        string nxmKey,
        long expiresEpoch,
        CancellationToken ct = default);

    /// <summary>
    /// The mod-page metadata. Hits
    /// <c>GET /v1/games/{domain}/mods/{modId}.json</c>. The acquisition service uses
    /// it to surface the canonical name + version to the user before downloading.
    /// </summary>
    Task<Response<ModInfo>> GetModInfoAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default);

    /// <summary>
    /// The files attached to a mod. Hits
    /// <c>GET /v1/games/{domain}/mods/{modId}/files.json</c> and unwraps the
    /// <c>{"files":[...]}</c> envelope to the array. The acquisition service uses it
    /// to let the user pick the file (or resolve the latest by timestamp).
    /// </summary>
    Task<Response<ModFile[]>> ListModFilesAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default);
}
