using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Exercises <see cref="IGitHubClient"/> against canned HTTP responses (no real
/// network): parsing, latest/404 handling, download + progress, rate-limit
/// detection, and error mapping.
/// </summary>
public sealed class GitHubClientTests
{
    private const string ApiBase = "https://api.github.com/";

    private const string TwoReleasesJson = @"
    [
      {
        ""tag_name"": ""v1.2.0"",
        ""name"": ""DMF 1.2"",
        ""published_at"": ""2024-05-01T12:00:00Z"",
        ""assets"": [
          { ""name"": ""dmf.zip"", ""size"": 2048, ""browser_download_url"": ""https://github.com/o/r/releases/download/v1.2.0/dmf.zip"" },
          { ""name"": ""dmf.tar.gz"", ""size"": 1800, ""browser_download_url"": ""https://github.com/o/r/releases/download/v1.2.0/dmf.tar.gz"" }
        ]
      },
      {
        ""tag_name"": ""v1.1.0"",
        ""name"": ""DMF 1.1"",
        ""published_at"": ""2024-04-01T12:00:00Z"",
        ""assets"": []
      }
    ]";

    private static GitHubClient CreateClient(HttpMessageHandler handler, string baseAddress = ApiBase)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        return new GitHubClient(http, NullLogger<GitHubClient>.Instance);
    }

    // ---- ListReleases -------------------------------------------------------

    [Fact]
    public void ListReleases_parses_tag_name_published_and_assets()
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.Json(TwoReleasesJson));
        var client = CreateClient(handler);

        var releases = client.ListReleases(new GitHubRepo("o", "r"));

        Assert.Equal(2, releases.Count);

        var latest = releases[0];
        Assert.Equal("v1.2.0", latest.TagName);
        Assert.Equal("DMF 1.2", latest.Name);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero), latest.PublishedAt);
        Assert.Equal(2, latest.Assets.Count);
        Assert.Equal("dmf.zip", latest.Assets[0].Name);
        Assert.Equal(2048, latest.Assets[0].Size);
        Assert.Equal(
            new Uri("https://github.com/o/r/releases/download/v1.2.0/dmf.zip"),
            latest.Assets[0].BrowserDownloadUrl);

        Assert.Empty(releases[1].Assets);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/releases"), request.RequestUri);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Fact]
    public void ListReleases_404_returns_empty()
    {
        var handler = new StubHttpMessageHandler(_ =>
            HttpResponses.Json(@"{""message"":""Not Found""}", HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var releases = client.ListReleases(new GitHubRepo("o", "missing"));

        Assert.Empty(releases);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ListReleases_empty_array_yields_empty_list()
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.Json("[]"));
        var client = CreateClient(handler);

        Assert.Empty(client.ListReleases(new GitHubRepo("o", "r")));
    }

    [Fact]
    public void ListReleases_500_throws_GitHubApiException_with_status_and_message()
    {
        var handler = new StubHttpMessageHandler(_ =>
            HttpResponses.Json(@"{""message"":""server boom""}", HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        var ex = Assert.Throws<GitHubApiException>(() => client.ListReleases(new GitHubRepo("o", "r")));
        Assert.Equal(500, ex.StatusCode);
        Assert.Contains("server boom", ex.Message);
    }

    [Fact]
    public void ListReleases_non_json_error_body_falls_back_to_reason()
    {
        // A non-JSON error body shouldn't crash the error mapper -- the status is
        // still surfaced via the HTTP fallback.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream is down, not json"),
        });
        var client = CreateClient(handler);

        var ex = Assert.Throws<GitHubApiException>(() => client.ListReleases(new GitHubRepo("o", "r")));
        Assert.Equal(502, ex.StatusCode);
        // Non-JSON body → fall back to the response reason phrase.
        Assert.Equal("Bad Gateway", ex.Message);
    }

    [Fact]
    public void ListReleases_handles_release_with_missing_optional_fields()
    {
        // A release with no name / published_at / assets must still map cleanly
        // to defaults rather than NRE -- GitHub occasionally omits fields.
        var handler = new StubHttpMessageHandler(_ => HttpResponses.Json(@"[{ ""tag_name"": ""v0.1"" }]"));
        var client = CreateClient(handler);

        var release = Assert.Single(client.ListReleases(new GitHubRepo("o", "r")));

        Assert.Equal("v0.1", release.TagName);
        Assert.Equal(string.Empty, release.Name);
        Assert.Equal(DateTimeOffset.UnixEpoch, release.PublishedAt);
        Assert.Empty(release.Assets);
    }

    // ---- GetLatestRelease ---------------------------------------------------

    [Fact]
    public void GetLatestRelease_returns_release_when_present()
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.Json(@"
        {
          ""tag_name"": ""v2.0.0"",
          ""name"": ""Latest"",
          ""published_at"": ""2024-06-01T00:00:00Z"",
          ""assets"": [
            { ""name"": ""bin.zip"", ""size"": 10, ""browser_download_url"": ""https://github.com/o/r/releases/download/v2.0.0/bin.zip"" }
          ]
        }"));
        var client = CreateClient(handler);

        var latest = client.GetLatestRelease(new GitHubRepo("o", "r"));

        Assert.NotNull(latest);
        Assert.Equal("v2.0.0", latest.TagName);
        Assert.Equal("Latest", latest.Name);
        Assert.Single(latest.Assets);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/releases/latest"), request.RequestUri);
    }

    [Fact]
    public void GetLatestRelease_404_returns_null()
    {
        var handler = new StubHttpMessageHandler(_ =>
            HttpResponses.Json(@"{""message"":""Not Found""}", HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        Assert.Null(client.GetLatestRelease(new GitHubRepo("o", "r")));
    }

    [Fact]
    public void GetLatestRelease_403_maps_to_GitHubApiException_when_not_rate_limited()
    {
        // 403 with a non-zero remaining is a permissions error, not rate-limit.
        var handler = new StubHttpMessageHandler(_ =>
        {
            var r = HttpResponses.Json(@"{""message"":""forbidden""}", HttpStatusCode.Forbidden);
            r.Headers.Add("X-RateLimit-Remaining", "42");
            return r;
        });
        var client = CreateClient(handler);

        var ex = Assert.Throws<GitHubApiException>(() => client.GetLatestRelease(new GitHubRepo("o", "r")));
        Assert.Equal(403, ex.StatusCode);
        Assert.DoesNotContain("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Rate limiting ------------------------------------------------------

    [Fact]
    public void GetLatestRelease_rate_limited_throws_GitHubRateLimitException_with_reset()
    {
        const long reset = 1_716_000_000L;
        var handler = new StubHttpMessageHandler(_ => HttpResponses.RateLimited(reset));
        var client = CreateClient(handler);

        var ex = Assert.Throws<GitHubRateLimitException>(() => client.GetLatestRelease(new GitHubRepo("o", "r")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), ex.ResetAt);
    }

    [Fact]
    public void ListReleases_rate_limited_throws_GitHubRateLimitException()
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.RateLimited(1_700_000_000L));
        var client = CreateClient(handler);

        Assert.Throws<GitHubRateLimitException>(() => client.ListReleases(new GitHubRepo("o", "r")));
    }

    [Fact]
    public void GitHubRateLimitException_is_a_GitHubApiException()
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.RateLimited(1_700_000_000L));
        var client = CreateClient(handler);

        // Callers can catch the base type to handle every API failure uniformly,
        // so GitHubRateLimitException must be assignable to GitHubApiException.
        var ex = Assert.Throws<GitHubRateLimitException>(() => client.ListReleases(new GitHubRepo("o", "r")));
        Assert.IsAssignableFrom<GitHubApiException>(ex);
    }

    [Fact]
    public void GetLatestRelease_429_rate_limited_carries_actual_status()
    {
        // GitHub occasionally signals rate limiting with 429 Too Many Requests;
        // the exception must surface that status (not a hardcoded 403).
        var handler = new StubHttpMessageHandler(_ =>
            HttpResponses.RateLimited(1_700_000_000L, HttpStatusCode.TooManyRequests));
        var client = CreateClient(handler);

        var ex = Assert.Throws<GitHubRateLimitException>(() => client.GetLatestRelease(new GitHubRepo("o", "r")));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000L), ex.ResetAt);
    }

    // ---- DownloadAssetAsync -------------------------------------------------

    [Fact]
    public async Task DownloadAssetAsync_writes_bytes_and_reports_progress()
    {
        var payload = Enumerable.Range(1, 10).Select(i => (byte)i).ToArray();
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var client = CreateClient(handler);

        var dest = Path.Combine(Path.GetTempPath(), "curator-integrations-" + Guid.NewGuid() + ".bin");
        var progress = new CapturingProgress();
        try
        {
            await client.DownloadAssetAsync(
                new GitHubReleaseAsset("dmf.zip", payload.Length, new Uri("https://github.com/o/r/releases/download/v1.2.0/dmf.zip")),
                dest,
                progress);

            Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
            Assert.NotEmpty(progress.Reports);
            Assert.Equal((long)payload.Length, progress.Reports[^1]);
        }
        finally
        {
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            new Uri("https://github.com/o/r/releases/download/v1.2.0/dmf.zip"),
            request.RequestUri);
    }

    [Fact]
    public async Task DownloadAssetAsync_creates_missing_destination_directory()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        var client = CreateClient(handler);

        var tempDir = Path.Combine(Path.GetTempPath(), "curator-integrations-" + Guid.NewGuid());
        var dest = Path.Combine(tempDir, "nested", "asset.zip");
        try
        {
            await client.DownloadAssetAsync(
                new GitHubReleaseAsset("asset.zip", payload.Length, new Uri("https://github.com/o/r/releases/download/v1/asset.zip")),
                dest);

            Assert.True(File.Exists(dest));
            Assert.Equal(payload, await File.ReadAllBytesAsync(dest));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAssetAsync_404_throws_GitHubApiException()
    {
        var handler = new StubHttpMessageHandler(_ =>
            HttpResponses.Json(@"{""message"":""Not Found""}", HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var dest = Path.Combine(Path.GetTempPath(), "curator-integrations-" + Guid.NewGuid() + ".bin");
        try
        {
            var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.DownloadAssetAsync(
                new GitHubReleaseAsset("missing.zip", 0, new Uri("https://github.com/o/r/releases/download/v1/missing.zip")),
                dest));
            Assert.Equal(404, ex.StatusCode);
            Assert.False(File.Exists(dest));
        }
        finally
        {
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }
        }
    }

    [Fact]
    public async Task DownloadAssetAsync_failed_mid_stream_deletes_partial_file()
    {
        // Simulate a network drop: a stream that emits a few bytes, then throws.
        // The client created the destination file, so it must clean up the
        // partial write rather than leave a corrupt file behind.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HalfwayFailingStream(bytesBeforeFailure: 5)),
        });
        var client = CreateClient(handler);

        var dest = Path.Combine(Path.GetTempPath(), "curator-integrations-" + Guid.NewGuid() + ".bin");
        var progress = new CapturingProgress();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => client.DownloadAssetAsync(
                new GitHubReleaseAsset("a.zip", 1024, new Uri("https://github.com/o/r/releases/download/v1/a.zip")),
                dest,
                progress));

            Assert.False(File.Exists(dest));
            Assert.NotEmpty(progress.Reports); // proves the copy started before failing
        }
        finally
        {
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }
        }
    }

    [Fact]
    public void ListReleases_null_repo_throws()
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.Json("[]"));
        var client = CreateClient(handler);
        Assert.Throws<ArgumentNullException>(() => client.ListReleases(null!));
    }

    [Fact]
    public void GetLatestRelease_null_repo_throws()
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.Json("[]"));
        var client = CreateClient(handler);
        Assert.Throws<ArgumentNullException>(() => client.GetLatestRelease(null!));
    }

    [Fact]
    public async Task DownloadAssetAsync_cancellation_aborts()
    {
        // An async handler that never completes on its own -- only the token cancels it.
        var handler = new CancellableHandler();
        var client = CreateClient(handler);

        using var cts = new CancellationTokenSource();
        var dest = Path.Combine(Path.GetTempPath(), "curator-integrations-" + Guid.NewGuid() + ".bin");

        // Start the download -- the GET response never arrives until cancelled.
        var task = client.DownloadAssetAsync(
            new GitHubReleaseAsset("a.zip", 1, new Uri("https://github.com/o/r/releases/download/v1/a.zip")),
            dest,
            progress: null,
            ct: cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        if (File.Exists(dest))
        {
            File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadAssetAsync_null_asset_throws()
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.Json("[]"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.DownloadAssetAsync(null!, Path.GetTempFileName()));
    }

    [Fact]
    public async Task DownloadAssetAsync_empty_path_throws()
    {
        var handler = new StubHttpMessageHandler(_ => HttpResponses.Json("[]"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.DownloadAssetAsync(
                new GitHubReleaseAsset("a", 1, new Uri("https://github.com/o/r/x")),
                ""));
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that captures reports synchronously (no
    /// SynchronizationContext hopping) for deterministic test assertions.
    /// </summary>
    private sealed class CapturingProgress : IProgress<long>
    {
        public List<long> Reports { get; } = new();
        public void Report(long value) => Reports.Add(value);
    }

    /// <summary>
    /// A handler whose response never completes until cancelled -- used to prove
    /// <see cref="IGitHubClient.DownloadAssetAsync"/> honors its cancellation token.
    /// </summary>
    private sealed class CancellableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }

    /// <summary>
    /// A read-only stream that emits <paramref name="bytesBeforeFailure"/> bytes
    /// then throws <see cref="IOException"/> -- simulates a mid-download network
    /// drop so partial-file cleanup can be asserted.
    /// </summary>
    private sealed class HalfwayFailingStream : Stream
    {
        private int _remaining;

        public HalfwayFailingStream(int bytesBeforeFailure) => _remaining = bytesBeforeFailure;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                throw new IOException("simulated network drop mid-download");
            }

            buffer[offset] = 0xAB;
            _remaining--;
            return 1;
        }
    }
}
