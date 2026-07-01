using System.Net;
using System.Net.Http.Headers;

namespace Magos.Modificus.Integrations.Tests;

/// <summary>
/// A scriptable <see cref="HttpMessageHandler"/> for offline GitHub client
/// tests. Returns canned responses via a callback and records a lightweight
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
        });

        return Task.FromResult(_respond(request));
    }
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
}
