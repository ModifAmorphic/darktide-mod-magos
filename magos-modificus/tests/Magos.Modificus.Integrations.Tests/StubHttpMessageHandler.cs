using System.Net;
using System.Net.Http.Headers;

namespace Magos.Modificus.Integrations.Tests;

/// <summary>
/// A scriptable <see cref="HttpMessageHandler"/> for offline GitHub + Nexus
/// client tests. Returns canned responses via a callback and records a lightweight
/// snapshot of each request so tests can assert on the outgoing URI + headers.
/// </summary>
/// <remarks>
/// No real network calls are made. The callback receives the request and returns
/// the response to send back, letting each test shape the JSON/status it needs.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    /// <summary>Snapshots of each request seen, in arrival order.</summary>
    public List<RequestSnapshot> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(new RequestSnapshot
        {
            Method = request.Method,
            RequestUri = request.RequestUri,
            Authorization = request.Headers.Authorization?.ToString(),
            UserAgent = request.Headers.UserAgent.ToString(),
            Accept = string.Join(", ", request.Headers.Accept.Select(a => a.MediaType)),
            ApplicationName = TryGetHeader(request, "Application-Name"),
            ApiKey = TryGetHeader(request, "apikey"),
            ProtocolVersion = TryGetHeader(request, "Protocol-Version"),
        });

        return Task.FromResult(_respond(request));
    }

    private static string? TryGetHeader(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}

/// <summary>
/// A minimal, header-only snapshot of a sent request — enough for assertions
/// without retaining the (single-use) <see cref="HttpRequestMessage"/> itself.
/// </summary>
internal sealed class RequestSnapshot
{
    public HttpMethod Method { get; init; } = HttpMethod.Get;
    public Uri? RequestUri { get; init; }
    public string? Authorization { get; init; }
    public string? UserAgent { get; init; }
    public string? Accept { get; init; }
    /// <summary>The <c>Application-Name</c> header value (Nexus app-id), or null when absent.</summary>
    public string? ApplicationName { get; init; }
    /// <summary>The Nexus <c>apikey</c> header value, or null when absent.</summary>
    public string? ApiKey { get; init; }
    /// <summary>The <c>Protocol-Version</c> header value (Nexus app-id), or null when absent.</summary>
    public string? ProtocolVersion { get; init; }
}

/// <summary>Helpers for building canned HTTP responses in tests.</summary>
internal static class HttpResponses
{
    /// <summary>A <c>200 OK</c> JSON response carrying <paramref name="json"/>.</summary>
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    /// <summary>A response carrying the <c>X-RateLimit-Remaining: 0</c> signal.</summary>
    public static HttpResponseMessage RateLimited(
        long resetEpochSeconds,
        HttpStatusCode status = HttpStatusCode.Forbidden)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", resetEpochSeconds.ToString());
        response.Content = new StringContent(@"{""message"":""API rate limit exceeded""}");
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    /// <summary>
    /// A 200 OK JSON response carrying the Nexus rate-limit headers
    /// (<c>x-rl-*</c>) populated from <paramref name="daily"/> /
    /// <paramref name="hourly"/>. For Nexus client tests asserting on the parsed
    /// <see cref="Response{T}.RateLimits"/>. Defaults to 0 (no headers) so tests
    /// that do not care about rate limits can call <c>NexusOk(json)</c>.
    /// </summary>
    public static HttpResponseMessage NexusOk(string json, int daily = 0, int hourly = 0)
    {
        var response = Json(json);
        if (daily > 0)
        {
            response.Headers.Add("x-rl-daily-limit", daily.ToString());
            response.Headers.Add("x-rl-daily-remaining", daily.ToString());
        }
        if (hourly > 0)
        {
            response.Headers.Add("x-rl-hourly-limit", hourly.ToString());
            response.Headers.Add("x-rl-hourly-remaining", hourly.ToString());
        }
        return response;
    }

    /// <summary>
    /// A Nexus rate-limited response: HTTP 429 with the <c>x-rl-*</c> headers
    /// reporting zero remaining. For Nexus client tests asserting on
    /// <see cref="Integrations.NexusRateLimitException"/>.
    /// </summary>
    public static HttpResponseMessage NexusRateLimited(
        HttpStatusCode status = HttpStatusCode.TooManyRequests,
        int limit = 1000)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Add("x-rl-daily-limit", limit.ToString());
        response.Headers.Add("x-rl-daily-remaining", "0");
        response.Headers.Add("x-rl-hourly-limit", "100");
        response.Headers.Add("x-rl-hourly-remaining", "0");
        response.Content = new StringContent(@"{""message"":""Rate limit exceeded""}");
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }
}
