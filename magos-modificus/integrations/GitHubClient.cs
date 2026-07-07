using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Integrations;

/// <summary>
/// The default <see cref="IGitHubClient"/> — a thin wrapper over the GitHub REST
/// API (/repos/{owner}/{name}/releases) via <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>HttpClient</c> is supplied by <c>IHttpClientFactory</c> (typed-client
/// pattern); the base URL, <c>User-Agent</c>, <c>Accept</c>, and optional
/// <c>Bearer</c> auth are applied in <see cref="ServiceCollectionExtensions.AddIntegrations"/>.</para>
/// <para>
/// <see cref="ListReleases"/> / <see cref="GetLatestRelease"/> are synchronous
/// wrappers over the async HTTP work (block with <c>GetAwaiter().GetResult()</c>).
/// Acceptable for the UI callers; the fake-handler test path completes
/// synchronously and the runtime has no legacy sync context to deadlock against.</para>
/// <para>
/// Registered as a transient (the <c>AddHttpClient&lt;T,TImpl&gt;</c> default); it
/// holds no per-call state — the only field is the factory-provided
/// <c>HttpClient</c>, which is reused across requests.</para>
/// </remarks>
internal sealed class GitHubClient : IGitHubClient
{
    private const int DownloadBufferSize = 81920;

    private const string UserAgent = "Magos-Modificus";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubClient> _logger;

    public GitHubClient(HttpClient httpClient, ILogger<GitHubClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Guarantee a User-Agent — GitHub rejects requests without one. DI (via
        // AddIntegrations) already sets it on the typed client, so only add one
        // when none is present (avoids a duplicated User-Agent in production).
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GitHubRelease> ListReleases(GitHubRepo repo, CancellationToken ct = default) =>
        ListReleasesAsync(repo, ct).GetAwaiter().GetResult();

    /// <inheritdoc />
    public GitHubRelease? GetLatestRelease(GitHubRepo repo, CancellationToken ct = default) =>
        GetLatestReleaseAsync(repo, ct).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task DownloadAssetAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // The asset URL is absolute (on github.com / the CDN), so it ignores the
        // API BaseAddress while still picking up the client's default headers
        // (User-Agent + auth) — auth raises the rate limit for asset downloads too.
        using var response = await _httpClient
            .GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var file = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize,
            useAsync: true);

        try
        {
            var buffer = new byte[DownloadBufferSize];
            long total = 0;
            int read;
            while ((read = await network.ReadAsync(buffer.AsMemory(0, DownloadBufferSize), ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
        }
        catch
        {
            // A failure mid-copy (network drop, cancellation, etc.) leaves a
            // partial file at destinationPath — and we created it, so we own the
            // cleanup. Dispose (release the Windows file handle) before deleting;
            // File.Delete throws on an open handle. Best-effort: swallow cleanup
            // failures so the original exception propagates unmasked.
            file.Dispose();
            TryDelete(destinationPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — the original error is the one callers need to see.
        }
    }

    private async Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(GitHubRepo repo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var uri = $"repos/{repo.Owner}/{repo.Name}/releases";
        using var response = await _httpClient.GetAsync(uri, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Unknown repo — treat as "no releases" rather than an error, so a
            // mistyped repo id doesn't crash a profile-creation prompt.
            _logger.LogDebug("GET {Uri} -> 404; returning empty release list.", uri);
            return Array.Empty<GitHubRelease>();
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var dtos = await JsonSerializer
            .DeserializeAsync<List<ReleaseDto>>(stream, cancellationToken: ct)
            .ConfigureAwait(false)
            ?? new List<ReleaseDto>();

        return dtos.Select(Map).ToList();
    }

    private async Task<GitHubRelease?> GetLatestReleaseAsync(GitHubRepo repo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var uri = $"repos/{repo.Owner}/{repo.Name}/releases/latest";
        using var response = await _httpClient.GetAsync(uri, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // GitHub returns 404 for "no latest release" (a repo with zero
            // releases) — map to null rather than an exception.
            _logger.LogDebug("GET {Uri} -> 404; no latest release.", uri);
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var dto = await JsonSerializer
            .DeserializeAsync<ReleaseDto>(stream, cancellationToken: ct)
            .ConfigureAwait(false);

        return dto is null ? null : Map(dto);
    }

    /// <summary>
    /// Throws <see cref="GitHubRateLimitException"/> / <see cref="GitHubApiException"/>
    /// for a failed response; returns silently on success. Callers handle the
    /// endpoint-specific <c>404</c> semantics before invoking this.
    /// </summary>
    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (IsRateLimited(response))
        {
            var reset = ParseReset(response);
            _logger.LogWarning(
                "GitHub API rate limit exhausted (status {Status}, reset at {Reset}).",
                (int)response.StatusCode,
                reset);
            throw new GitHubRateLimitException((int)response.StatusCode, reset);
        }

        var message = await ReadErrorMessageAsync(response, ct).ConfigureAwait(false);
        _logger.LogError("GitHub API request failed: status {Status}, message {Message}.", (int)response.StatusCode, message);
        throw new GitHubApiException((int)response.StatusCode, message);
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        // GitHub returns 403 (sometimes 429) when the rate window is empty, and
        // sets X-RateLimit-Remaining: 0. A 403 for other reasons (permissions)
        // carries a non-zero remaining — so both conditions must hold.
        if (response.StatusCode != HttpStatusCode.Forbidden &&
            response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (int.TryParse(value, out var remaining) && remaining <= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset? ParseReset(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
        {
            foreach (var value in values)
            {
                if (long.TryParse(value, out var epochSeconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
                }
            }
        }

        return null;
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // GitHub errors are JSON with a "message" field. Fall back to the reason
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
            // Non-JSON or unreadable body — fall through to the reason phrase.
        }

        return FallbackReason(response);
    }

    private static string FallbackReason(HttpResponseMessage response) =>
        response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";

    private static GitHubRelease Map(ReleaseDto dto)
    {
        var assets = (dto.Assets ?? new List<AssetDto>())
            .Select(MapAsset)
            .ToList();

        return new GitHubRelease(
            dto.TagName ?? string.Empty,
            dto.Name ?? string.Empty,
            dto.PublishedAt ?? DateTimeOffset.UnixEpoch,
            assets);
    }

    private static GitHubReleaseAsset MapAsset(AssetDto dto) => new(
        dto.Name ?? string.Empty,
        dto.Size ?? 0,
        new Uri(dto.BrowserDownloadUrl!));

    // ---- wire DTOs (snake_case ↔ the GitHub REST schema) ------------------

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<AssetDto>? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
