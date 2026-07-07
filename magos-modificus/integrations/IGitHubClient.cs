namespace Magos.Modificus.Integrations;

/// <summary>
/// A read-only GitHub Releases client — the minimum mod-source surface needed
/// for the DMF new-profile prompt and GitHub-hosted mod sources: list
/// a repository's releases, fetch the latest, and download a release asset.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ListReleases"/> / <see cref="GetLatestRelease"/> are synchronous
/// wrappers that return fully-materialized results — the simple surface UI
/// callers want. <see cref="DownloadAssetAsync"/> is async (it's a file
/// download).</para>
/// <para>
/// Implemented over the GitHub REST API via <see cref="System.Net.Http.HttpClient"/>
/// provided by <c>IHttpClientFactory</c>; registered through
/// <see cref="ServiceCollectionExtensions.AddIntegrations"/>.</para>
/// </remarks>
public interface IGitHubClient
{
    /// <summary>
    /// Lists a repository's published releases (newest first, per the GitHub API).
    /// A <c>404</c> (unknown repo) yields an empty list rather than an exception.
    /// Returns up to GitHub's default page size (~30); pagination is out of v1.
    /// </summary>
    /// <exception cref="GitHubApiException">A non-2xx response other than 404.</exception>
    /// <exception cref="GitHubRateLimitException">The API rate limit is exhausted.</exception>
    IReadOnlyList<GitHubRelease> ListReleases(GitHubRepo repo, CancellationToken ct = default);

    /// <summary>
    /// The latest published release, or <c>null</c> if the repo has no releases
    /// or is unknown (both surface as <c>404</c> from the API).
    /// </summary>
    /// <exception cref="GitHubApiException">A non-2xx response other than 404.</exception>
    /// <exception cref="GitHubRateLimitException">The API rate limit is exhausted.</exception>
    GitHubRelease? GetLatestRelease(GitHubRepo repo, CancellationToken ct = default);

    /// <summary>
    /// Downloads <paramref name="asset"/>'s bytes to
    /// <paramref name="destinationPath"/>, reporting cumulative byte count to
    /// <paramref name="progress"/> when provided. The destination's parent
    /// directory is created if missing.
    /// </summary>
    /// <exception cref="GitHubApiException">A non-2xx response (e.g. a stale asset URL).</exception>
    /// <exception cref="GitHubRateLimitException">The API rate limit is exhausted.</exception>
    Task DownloadAssetAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        IProgress<long>? progress = null,
        CancellationToken ct = default);
}
