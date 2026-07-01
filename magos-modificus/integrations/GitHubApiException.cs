using System.Net;

namespace Magos.Modificus.Integrations;

/// <summary>
/// Thrown when the GitHub REST API returns a non-success response (other than
/// the <c>404</c> cases the client maps to <c>null</c> / empty). Carries the
/// HTTP status and the API's <c>message</c> field when available.
/// </summary>
/// <remarks>
/// Unsealed so <see cref="GitHubRateLimitException"/> can specialize it; callers
/// can catch the base type to handle every GitHub API failure uniformly.
/// </remarks>
public class GitHubApiException : Exception
{
    /// <summary>The HTTP status code returned by the API.</summary>
    public int StatusCode { get; }

    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="message">The API error message (its <c>message</c> field when available).</param>
    public GitHubApiException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Thrown when the GitHub API refuses a request because the rate limit is
/// exhausted — detected via a <c>403</c>/<c>429</c> status carrying
/// <c>X-RateLimit-Remaining: 0</c>. Carries the reset time
/// (<c>X-RateLimit-Reset</c>) when GitHub reports it, so callers can advise the
/// user when to retry.
/// </summary>
public sealed class GitHubRateLimitException : GitHubApiException
{
    /// <summary>
    /// When the rate window resets (from <c>X-RateLimit-Reset</c>), or
    /// <c>null</c> if the header was absent.
    /// </summary>
    public DateTimeOffset? ResetAt { get; }

    internal GitHubRateLimitException(DateTimeOffset? resetAt)
        : base(
            (int)HttpStatusCode.Forbidden,
            "GitHub API rate limit exhausted"
                + (resetAt.HasValue ? $" — resets at {resetAt:O}." : "."))
    {
        ResetAt = resetAt;
    }
}
