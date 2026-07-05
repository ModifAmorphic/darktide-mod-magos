using System.Globalization;
using System.Net;
using System.Text.Json;
using Magos.Modificus.Config;
using Magos.Modificus.General;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Integrations;

/// <summary>
/// The default <see cref="INexusClient"/>. A thin wrapper over the Nexus v1 REST
/// API via <see cref="HttpClient"/>, mirroring <see cref="GitHubClient"/>. Auth
/// + app-identification headers are applied per-request by the configured
/// <see cref="INexusAuthMessageFactory"/>; the rate-limit headers on every
/// response are parsed into <see cref="NexusRateLimits"/> + carried on the
/// returned <see cref="Response{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>HttpClient</c> is supplied by <c>IHttpClientFactory</c> (typed-client
/// pattern); the API base URL is the typed client's <c>BaseAddress</c>. The
/// OAuth userinfo endpoint lives on a different host
/// (<c>users.nexusmods.com</c>), so this client injects
/// <see cref="IConfigLoader"/> to read the OAuth base URL live for that one
/// endpoint (the same live-read pattern as the auth factory).</para>
/// <para>
/// <b>Auth.</b> Per-request auth is owned by the auth factory (selected live by
/// <see cref="NexusConfig.AuthMethod"/>); this client does not know which auth
/// method is in use.</para>
/// <para>
/// <b>401 handling.</b> On a 401, this client asks the auth factory to refresh
/// (OAuth) or give up (API key, None). On a successful refresh, the request is
/// retried once with the new credentials. The retry is bounded to one: a second
/// 401 surfaces as <see cref="NexusApiException"/> (avoids an infinite loop on a
/// persistently-invalid token).</para>
/// <para>
/// Registered as a transient (the <c>AddHttpClient&lt;T,TImpl&gt;</c> default);
/// holds no per-call state.</para>
/// </remarks>
internal sealed class NexusClient : INexusClient
{
    private readonly HttpClient _http;
    private readonly INexusAuthMessageFactory _auth;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<NexusClient> _logger;

    public NexusClient(
        HttpClient http,
        INexusAuthMessageFactory auth,
        IConfigLoader configLoader,
        ILogger<NexusClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default)
    {
        var (response, _) = await SendAsync<ValidateInfo>(
            HttpMethod.Get,
            RelativeUri("v1/users/validate.json"),
            ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<OAuthUserInfo>> GetOAuthUserInfoAsync(CancellationToken ct = default)
    {
        // The userinfo endpoint hangs off the OAuth base URL, not the API base
        // URL. Resolve the absolute URI from the live config so the configured
        // OAuth base URL is honored (tests can override it).
        var oauthBase = NormalizeBaseUrl(_configLoader.Load().Integrations.Nexus.OAuthBaseUrl);
        var (response, _) = await SendAsync<OAuthUserInfo>(
            HttpMethod.Get,
            new Uri(oauthBase + "/oauth/userinfo", UriKind.Absolute),
            ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<ModUpdate[]>> ModUpdatesAsync(
        string gameDomain,
        NexusPeriod period,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        var periodString = period switch
        {
            NexusPeriod.Day => "1d",
            NexusPeriod.Week => "1w",
            NexusPeriod.Month => "1m",
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, null),
        };

        // This endpoint returns a top-level JSON array, not an object.
        var uri = RelativeUri($"v1/games/{gameDomain}/mods/updated.json?period={periodString}");
        var (response, _) = await SendArrayAsync<ModUpdate>(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<DownloadLink[]>> DownloadLinksAsync(
        string gameDomain,
        int modId,
        int fileId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        var uri = RelativeUri(
            $"v1/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json");
        var (response, _) = await SendArrayAsync<DownloadLink>(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<DownloadLink[]>> DownloadLinksAsync(
        string gameDomain,
        int modId,
        int fileId,
        string nxmKey,
        long expiresEpoch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(nxmKey);
        var uri = RelativeUri(
            $"v1/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link.json"
            + $"?key={Uri.EscapeDataString(nxmKey)}&expires={expiresEpoch}");
        var (response, _) = await SendArrayAsync<DownloadLink>(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<ModInfo>> GetModInfoAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        var uri = RelativeUri($"v1/games/{gameDomain}/mods/{modId}.json");
        var (response, _) = await SendAsync<ModInfo>(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc />
    public async Task<Response<ModFile[]>> ListModFilesAsync(
        string gameDomain,
        int modId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDomain);
        var uri = RelativeUri($"v1/games/{gameDomain}/mods/{modId}/files.json");

        // files.json wraps its array in {"files":[...]}; unwrap before returning.
        var (wrapped, _) = await SendAsync<ModFilesResponse>(HttpMethod.Get, uri, ct).ConfigureAwait(false);
        var files = wrapped.Data.Files ?? Array.Empty<ModFile>();
        return new Response<ModFile[]>(files, wrapped.RateLimits);
    }

    // ---- core send (with 401-reactive refresh + retry once) ----------------

    /// <summary>
    /// Sends a request that deserializes to a single object. Applies auth via
    /// the factory; on 401 asks the factory to refresh + retries once. Parses
    /// the rate-limit headers onto the returned <see cref="Response{T}"/>.
    /// </summary>
    private async Task<(Response<T> Response, bool WasRetry)> SendAsync<T>(
        HttpMethod method,
        Uri uri,
        CancellationToken ct,
        bool isRetry = false)
    {
        using var response = await SendRawAsync(method, uri, ct, isRetry).ConfigureAwait(false);
        var payload = await ReadAsync<T>(response, ct).ConfigureAwait(false);
        var limits = NexusRateLimitsParser.Parse(response);
        LogRateLimits(uri, limits);
        return (new Response<T>(payload, limits), isRetry);
    }

    /// <summary>
    /// Sends a request that deserializes to a top-level JSON array (the shape of
    /// <c>updated.json</c> + <c>download_link.json</c>). Mirrors
    /// <see cref="SendAsync{T}"/> for auth + retry.
    /// </summary>
    private async Task<(Response<T[]> Response, bool WasRetry)> SendArrayAsync<T>(
        HttpMethod method,
        Uri uri,
        CancellationToken ct,
        bool isRetry = false)
    {
        using var response = await SendRawAsync(method, uri, ct, isRetry).ConfigureAwait(false);
        var payload = await ReadArrayAsync<T>(response, ct).ConfigureAwait(false);
        var limits = NexusRateLimitsParser.Parse(response);
        LogRateLimits(uri, limits);
        return (new Response<T[]>(payload, limits), isRetry);
    }

    /// <summary>
    /// Sends the request via the underlying <c>HttpClient</c>. On 401, asks the
    /// auth factory to refresh; on success, retries once with a fresh request
    /// (built by the factory with the now-current credentials). Disposes the
    /// 401 response + the original request before retrying.
    /// </summary>
    /// <remarks>
    /// <b>Auth gate.</b> If <see cref="INexusAuthMessageFactory.IsAuthenticatedAsync"/>
    /// returns <c>false</c> before the first send, this throws
    /// <see cref="NexusNotAuthenticatedException"/> (callers gate on it; this is
    /// the defensive backstop). The retry path skips the gate (the refresh just
    /// produced fresh credentials; if the factory reports not-authenticated
    /// again, the 401 will resurface).
    /// </remarks>
    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        Uri uri,
        CancellationToken ct,
        bool isRetry)
    {
        if (!isRetry && !await _auth.IsAuthenticatedAsync(ct).ConfigureAwait(false))
        {
            throw new NexusNotAuthenticatedException();
        }

        // Build + send the request. The factory owns auth + app headers.
        var request = await _auth.CreateAsync(method, uri, ct).ConfigureAwait(false);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }
        request.Dispose();

        // Retry once on 401 if the factory reports a successful refresh.
        if (response.StatusCode == HttpStatusCode.Unauthorized && !isRetry)
        {
            response.Dispose();

            if (await _auth.OnUnauthorizedAsync(ct).ConfigureAwait(false))
            {
                // Refresh succeeded: recurse once with isRetry=true. A second 401
                // surfaces as NexusApiException (the recursive call no longer
                // hits this branch).
                return await SendRawAsync(method, uri, ct, isRetry: true).ConfigureAwait(false);
            }

            // Refresh not possible / failed. The original 401 propagates as a
            // NexusApiException.
            throw new NexusApiException(
                (int)HttpStatusCode.Unauthorized,
                "Nexus auth rejected (HTTP 401). Re-login required.");
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Throws <see cref="NexusRateLimitException"/> / <see cref="NexusApiException"/>
    /// for a failed response. Returns silently on success. Detection mirrors
    /// <see cref="GitHubClient"/>: HTTP 429, or HTTP 403 with one of the
    /// <c>x-rl-*-remaining</c> headers reporting zero, is the rate-limit signal.
    /// </summary>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var limits = NexusRateLimitsParser.Parse(response);
        if (IsRateLimited(response, limits))
        {
            _logger.LogWarning(
                "Nexus API rate limit exhausted (status {Status}; daily remaining {Daily}; hourly remaining {Hourly}).",
                (int)response.StatusCode,
                limits.DailyRemaining,
                limits.HourlyRemaining);
            throw new NexusRateLimitException((int)response.StatusCode, limits);
        }

        var message = await ReadErrorMessageAsync(response, ct).ConfigureAwait(false);
        _logger.LogError("Nexus API request failed: status {Status}, message {Message}.",
            (int)response.StatusCode, message);
        throw new NexusApiException((int)response.StatusCode, message);
    }

    /// <summary>
    /// The rate-limit signal: HTTP 429 always; HTTP 403 only when the limit
    /// headers are present (<c>x-rl-*-limit &gt; 0</c>) AND at least one
    /// remaining counter is zero. A 403 with no rate-limit headers, or with a
    /// non-zero remaining, is a permissions error, not rate-limiting.
    /// </summary>
    private static bool IsRateLimited(HttpResponseMessage response, NexusRateLimits limits)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        // 403: rate-limit only when limit headers are present and a remaining
        // counter is exhausted. Mirrors the GitHub client's two-condition rule.
        var hasLimitHeaders = limits.DailyLimit > 0 || limits.HourlyLimit > 0;
        if (!hasLimitHeaders)
        {
            return false;
        }

        return limits.DailyRemaining <= 0 || limits.HourlyRemaining <= 0;
    }

    private void LogRateLimits(Uri uri, NexusRateLimits limits)
    {
        // Match NMA's pattern: log the remaining counters on every successful
        // call so the operator can watch the rate window drain.
        if (limits.DailyLimit > 0 || limits.HourlyLimit > 0)
        {
            _logger.LogInformation(
                "Nexus API call to {Uri} ok; remaining: daily={Daily}, hourly={Hourly}.",
                uri,
                limits.DailyRemaining,
                limits.HourlyRemaining);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false)
            ?? throw new NexusApiException(
                (int)response.StatusCode,
                $"Nexus API returned an empty {typeof(T).Name} response.");
    }

    private static async Task<T[]> ReadArrayAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var dto = await JsonSerializer.DeserializeAsync<List<T>>(stream, cancellationToken: ct)
            .ConfigureAwait(false);
        return dto?.ToArray() ?? Array.Empty<T>();
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Nexus errors are JSON with a "message" field. Fall back to the reason
        // phrase for non-JSON bodies so the exception always carries something.
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? FallbackReason(response);
            }
        }
        catch
        {
            // Non-JSON or unreadable body.
        }

        return FallbackReason(response);
    }

    private static string FallbackReason(HttpResponseMessage response) =>
        response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";

    // ---- URI helpers -------------------------------------------------------

    /// <summary>
    /// Builds a relative URI against the typed client's <c>BaseAddress</c> (the
    /// API base URL, normalized in <see cref="ServiceCollectionExtensions.AddIntegrations"/>
    /// to end with a trailing slash so relative URIs resolve predictably).
    /// </summary>
    private static Uri RelativeUri(string relative) => new(relative, UriKind.Relative);

    /// <summary>
    /// Normalizes a base URL: trims whitespace + trailing slashes, and strips a
    /// trailing <c>/oauth</c> so a user (reasonably) pointing <c>OAuthBaseUrl</c>
    /// at <c>https://users.nexusmods.com/oauth</c> (the OAuth endpoint-path
    /// prefix, not the issuer root) doesn't double up to
    /// <c>/oauth/oauth/userinfo</c>. Does not re-append a
    /// slash (callers compose with their own <c>/...</c> suffix). Falls back to
    /// the public OAuth root when blank.
    /// </summary>
    private static string NormalizeBaseUrl(string? baseUrl)
    {
        var trimmed = StripOAuthSuffix((baseUrl ?? string.Empty).Trim().TrimEnd('/'));
        return trimmed.Length == 0 ? "https://users.nexusmods.com" : trimmed;
    }

    /// <summary>
    /// Strips a trailing <c>/oauth</c> (case-insensitive) so the
    /// <c>/oauth/&lt;endpoint&gt;</c> composition below doesn't double up when
    /// the configured base URL is the OAuth issuer root.
    /// </summary>
    private static string StripOAuthSuffix(string trimmed)
    {
        const string OauthSuffix = "/oauth";
        return trimmed.EndsWith(OauthSuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^OauthSuffix.Length]
            : trimmed;
    }
}

/// <summary>
/// Parses the Nexus rate-limit headers (<c>x-rl-*</c>) from an HTTP response
/// into a <see cref="NexusRateLimits"/>. Missing or unparseable headers yield
/// <c>0</c> / <c>null</c> for that field (never throws). Header names + parsing
/// mirror NMA's <c>ResponseMetadata.FromHttpHeaders</c>.
/// </summary>
internal static class NexusRateLimitsParser
{
    public static NexusRateLimits Parse(HttpResponseMessage response)
    {
        ParseInt(response, "x-rl-daily-limit", out var dailyLimit);
        ParseInt(response, "x-rl-daily-remaining", out var dailyRemaining);
        ParseDate(response, "x-rl-daily-reset", out var dailyReset);
        ParseInt(response, "x-rl-hourly-limit", out var hourlyLimit);
        ParseInt(response, "x-rl-hourly-remaining", out var hourlyRemaining);
        ParseDate(response, "x-rl-hourly-reset", out var hourlyReset);

        return new NexusRateLimits(
            dailyLimit,
            dailyRemaining,
            dailyReset,
            hourlyLimit,
            hourlyRemaining,
            hourlyReset);
    }

    private static void ParseInt(HttpResponseMessage response, string header, out int value)
    {
        value = 0;
        if (response.Headers.TryGetValues(header, out var values))
        {
            foreach (var v in values)
            {
                if (int.TryParse(v, out var parsed))
                {
                    value = parsed;
                    return;
                }
            }
        }
    }

    private static void ParseDate(HttpResponseMessage response, string header, out DateTimeOffset? value)
    {
        value = null;
        if (response.Headers.TryGetValues(header, out var values))
        {
            foreach (var v in values)
            {
                if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    value = parsed;
                    return;
                }
            }
        }
    }
}
