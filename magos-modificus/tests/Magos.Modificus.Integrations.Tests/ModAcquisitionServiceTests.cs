using System.Net;
using System.Text.Json;
using Magos.Modificus.Mods;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.Integrations.Tests;

/// <summary>
/// Exercises <see cref="ModAcquisitionService"/> against canned Nexus client
/// responses + a stub HTTP handler for the CDN archive download (no real
/// network): the premium vs free-user overload selection, the metadata
/// resolution (name + version), the download-to-temp + progress reporting, the
/// Import handoff, temp-file cleanup on success and failure, and the no-fallback
/// error policy (metadata failure surfaces a clear error, nothing partial lands).
/// </summary>
public sealed class ModAcquisitionServiceTests
{
    private const string GameDomain = "warhammer40kdarktide";
    private const int ModId = 8;
    private const int FileId = 5820;

    private const string DownloadLinksJson = @"
    [
      { ""name"": ""CDN-A"", ""short_name"": ""cdn-a"", ""URI"": ""https://cdn.example.com/file.zip"" },
      { ""name"": ""CDN-B"", ""short_name"": ""cdn-b"", ""URI"": ""https://cdn-b.example.com/file.zip"" }
    ]";

    private const string ModInfoJson = @"
    {
      ""name"": ""Test Mod"",
      ""mod_id"": 8,
      ""game_id"": 3333,
      ""domain_name"": ""warhammer40kdarktide"",
      ""version"": ""1.2.3""
    }";

    private const string ModFilesJson = @"
    {
      ""files"": [
        { ""file_id"": 100, ""file_name"": ""mod_v1.zip"", ""name"": ""Mod v1"", ""version"": ""1.0"", ""size"": 1024 },
        { ""file_id"": 5820, ""file_name"": ""mod_v2.zip"", ""name"": ""Mod v2"", ""version"": ""2.0"", ""size"": 2048 }
      ]
    }";

    private const string CdnUrl = "https://cdn.example.com/file.zip";

    // ---- happy path --------------------------------------------------------

    [Fact]
    public async Task AcquireFromNexusAsync_premium_path_downloads_and_imports_with_right_metadata()
    {
        // No nxmKey/nxmExpires -> the premium (auth-only) DownloadLinksAsync
        // overload is used. The CDN serves a small payload; Import records the
        // call; the temp file is cleaned up after Import returns.
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoResponse = () => Ok(ParseInfo(ModInfoJson)),
            ModFilesResponse = ParseFiles(ModFilesJson),
        };

        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var handler = new StubHttpMessageHandler(req =>
            req.RequestUri!.AbsoluteUri == CdnUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler);
        var import = new RecordingImportService
        {
            NextResult = (Guid.NewGuid(), "deadbeef"),
        };
        var service = new ModAcquisitionService(
            nexus, import, new SingleClientFactory(http), NullLogger<ModAcquisitionService>.Instance);

        var progress = new CapturingProgress();
        var result = await service.AcquireFromNexusAsync(
            GameDomain, ModId, FileId, progress: progress);

        Assert.Equal(import.NextResult, result);

        // Import was called with the resolved name + version + NexusSource.
        var single = Assert.Single(import.Calls);
        Assert.Equal("Test Mod", single.ModName);
        Assert.Equal("2.0", single.Version);
        Assert.IsType<NexusSource>(single.Source);
        Assert.Equal(ModId, ((NexusSource)single.Source).ModId);
        Assert.NotEmpty(single.SourcePath);
        // The temp file is gone (cleaned up after Import).
        Assert.False(File.Exists(single.SourcePath));

        // Progress was reported, ending at the payload length.
        Assert.NotEmpty(progress.Reports);
        Assert.Equal((long)payload.Length, progress.Reports[^1]);

        // Premium overload used: the free-user key/expires were NOT recorded.
        Assert.True(nexus.PremiumDownloadLinksCalled);
        Assert.Null(nexus.FreeUserDownloadLinksKey);
    }

    [Fact]
    public async Task AcquireFromNexusAsync_free_user_path_uses_keyed_overload()
    {
        // With nxmKey + nxmExpires both set, the free-user overload is used and
        // the key + expiry are forwarded.
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoResponse = () => Ok(ParseInfo(ModInfoJson)),
            ModFilesResponse = ParseFiles(ModFilesJson),
        };
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0xAA }),
        });
        var http = new HttpClient(handler);
        var service = new ModAcquisitionService(
            nexus, new RecordingImportService(), new SingleClientFactory(http),
            NullLogger<ModAcquisitionService>.Instance);

        await service.AcquireFromNexusAsync(
            GameDomain, ModId, FileId, nxmKey: "ABC", nxmExpires: 12345L);

        Assert.Equal("ABC", nexus.FreeUserDownloadLinksKey);
        Assert.Equal(12345L, nexus.FreeUserDownloadLinksExpires);
        Assert.False(nexus.PremiumDownloadLinksCalled);
    }

    [Fact]
    public async Task AcquireFromNexusAsync_partial_key_does_not_use_free_user_overload()
    {
        // Only one of key/expires set -> the premium overload is used (both must
        // be present for the free-user path). Guards a half-populated nxm URL.
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoResponse = () => Ok(ParseInfo(ModInfoJson)),
            ModFilesResponse = ParseFiles(ModFilesJson),
        };
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0xAA }),
        });
        var http = new HttpClient(handler);
        var service = new ModAcquisitionService(
            nexus, new RecordingImportService(), new SingleClientFactory(http),
            NullLogger<ModAcquisitionService>.Instance);

        await service.AcquireFromNexusAsync(
            GameDomain, ModId, FileId, nxmKey: "ABC", nxmExpires: null);

        Assert.True(nexus.PremiumDownloadLinksCalled);
        Assert.Null(nexus.FreeUserDownloadLinksKey);
    }

    [Fact]
    public async Task AcquireFromNexusAsync_uses_first_cdn_link()
    {
        // Nexus returns CDN URLs in priority order; the first is used. The stub
        // handler records what was fetched; only the first CDN URL is hit.
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoResponse = () => Ok(ParseInfo(ModInfoJson)),
            ModFilesResponse = ParseFiles(ModFilesJson),
        };
        var seen = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            seen.Add(req.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x01 }),
            };
        });
        var http = new HttpClient(handler);
        var service = new ModAcquisitionService(
            nexus, new RecordingImportService(), new SingleClientFactory(http),
            NullLogger<ModAcquisitionService>.Instance);

        await service.AcquireFromNexusAsync(GameDomain, ModId, FileId);

        // Exactly one CDN GET, to the first link.
        var cdnGet = Assert.Single(seen);
        Assert.Equal(CdnUrl, cdnGet);
    }

    // ---- no degraded fallback ---------------------------------------------

    [Fact]
    public async Task AcquireFromNexusAsync_mod_info_failure_throws_no_fallback()
    {
        // GetModInfoAsync throws (NexusApiException). The service must propagate
        // it (no degraded name from the id); Import is never reached.
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoThrows = new NexusApiException(500, "mod info gone"),
        };
        var import = new RecordingImportService();
        var service = new ModAcquisitionService(
            nexus, import,
            new SingleClientFactory(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))),
            NullLogger<ModAcquisitionService>.Instance);

        var ex = await Assert.ThrowsAsync<NexusApiException>(
            () => service.AcquireFromNexusAsync(GameDomain, ModId, FileId));
        Assert.Equal(500, ex.StatusCode);
        Assert.Empty(import.Calls);
    }

    [Fact]
    public async Task AcquireFromNexusAsync_file_not_listed_throws_no_fallback()
    {
        // The requested fileId is not among the mod's listed files. The service
        // throws InvalidOperationException (no guessing the version).
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoResponse = () => Ok(ParseInfo(ModInfoJson)),
            ModFilesResponse = ParseFiles(@"{ ""files"": [ { ""file_id"": 100, ""version"": ""1.0"" } ] }"),
        };
        var import = new RecordingImportService();
        var service = new ModAcquisitionService(
            nexus, import,
            new SingleClientFactory(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))),
            NullLogger<ModAcquisitionService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcquireFromNexusAsync(GameDomain, ModId, fileId: 999));
        Assert.Contains("999", ex.Message);
        Assert.Empty(import.Calls);
    }

    [Fact]
    public async Task AcquireFromNexusAsync_empty_mod_name_throws()
    {
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoResponse = () => Ok(new ModInfo { Name = "" }),
        };
        var import = new RecordingImportService();
        var service = new ModAcquisitionService(
            nexus, import,
            new SingleClientFactory(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))),
            NullLogger<ModAcquisitionService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AcquireFromNexusAsync(GameDomain, ModId, FileId));
        Assert.Contains("empty name", ex.Message);
        Assert.Empty(import.Calls);
    }

    // ---- download failure + temp cleanup ----------------------------------

    [Fact]
    public async Task AcquireFromNexusAsync_download_failure_throws_and_skips_import()
    {
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoResponse = () => Ok(ParseInfo(ModInfoJson)),
            ModFilesResponse = ParseFiles(ModFilesJson),
        };
        // The CDN returns 500.
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var http = new HttpClient(handler);
        var import = new RecordingImportService();
        var service = new ModAcquisitionService(
            nexus, import, new SingleClientFactory(http),
            NullLogger<ModAcquisitionService>.Instance);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.AcquireFromNexusAsync(GameDomain, ModId, FileId));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Empty(import.Calls);
    }

    [Fact]
    public async Task AcquireFromNexusAsync_import_failure_propagates_and_temp_cleaned()
    {
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoResponse = () => Ok(ParseInfo(ModInfoJson)),
            ModFilesResponse = ParseFiles(ModFilesJson),
        };
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x00 }),
        });
        var http = new HttpClient(handler);
        var import = new RecordingImportService
        {
            Throw = new InvalidDataException("bad zip structure"),
        };
        var service = new ModAcquisitionService(
            nexus, import, new SingleClientFactory(http),
            NullLogger<ModAcquisitionService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.AcquireFromNexusAsync(GameDomain, ModId, FileId));
        Assert.Equal("bad zip structure", ex.Message);
        // The temp file was cleaned up despite the import failure.
        Assert.Single(import.Calls);
        Assert.False(File.Exists(import.Calls[0].SourcePath));
    }

    [Fact]
    public async Task AcquireFromNexusAsync_cancellation_propagates()
    {
        var nexus = new FakeNexusClient
        {
            DownloadLinks = ParseLinks(DownloadLinksJson),
            ModInfoResponse = () => Ok(ParseInfo(ModInfoJson)),
            ModFilesResponse = ParseFiles(ModFilesJson),
        };
        // A handler that never completes until cancelled.
        var handler = new CancellableHandler();
        var http = new HttpClient(handler);
        var service = new ModAcquisitionService(
            nexus, new RecordingImportService(), new SingleClientFactory(http),
            NullLogger<ModAcquisitionService>.Instance);

        using var cts = new CancellationTokenSource();
        var task = service.AcquireFromNexusAsync(GameDomain, ModId, FileId, ct: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task AcquireFromNexusAsync_null_game_domain_throws()
    {
        var service = new ModAcquisitionService(
            new FakeNexusClient(), new RecordingImportService(),
            new SingleClientFactory(new HttpClient()),
            NullLogger<ModAcquisitionService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.AcquireFromNexusAsync("", ModId, FileId));
    }

    // ---- JSON parse helpers ------------------------------------------------

    private static Response<ModInfo> Ok(ModInfo info) => new(info, NexusRateLimits.Unknown);

    private static DownloadLink[] ParseLinks(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Select(e => new DownloadLink
            {
                Name = e.GetProperty("name").GetString() ?? "",
                ShortName = e.GetProperty("short_name").GetString() ?? "",
                Uri = new Uri(e.GetProperty("URI").GetString()!, UriKind.Absolute),
            })
            .ToArray();
    }

    private static ModInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new ModInfo
        {
            Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            ModId = root.TryGetProperty("mod_id", out var m) && m.TryGetInt32(out var mi) ? mi : 0,
            DomainName = root.TryGetProperty("domain_name", out var d) ? d.GetString() ?? "" : "",
        };
    }

    private static ModFile[] ParseFiles(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("files", out var files))
        {
            return Array.Empty<ModFile>();
        }
        return files.EnumerateArray()
            .Select(f => new ModFile
            {
                FileId = f.TryGetProperty("file_id", out var id) && id.TryGetInt64(out var idv) ? idv : 0,
                Version = f.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                FileName = f.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? "" : "",
            })
            .ToArray();
    }

    // ---- fakes -------------------------------------------------------------

    /// <summary>
    /// A configurable <see cref="INexusClient"/> stub. Each method is backed by
    /// a settable field/func so each test shapes the responses it needs without
    /// a real HTTP round-trip. Records which <c>DownloadLinksAsync</c> overload
    /// was called + the args.
    /// </summary>
    private sealed class FakeNexusClient : INexusClient
    {
        public DownloadLink[]? DownloadLinks { get; set; }
        public NexusApiException? DownloadLinksThrows { get; set; }
        public NexusApiException? ModInfoThrows { get; set; }
        public Func<Response<ModInfo>>? ModInfoResponse { get; set; }
        public ModFile[]? ModFilesResponse { get; set; }

        // Records which overload was called.
        public bool PremiumDownloadLinksCalled { get; private set; }
        public string? FreeUserDownloadLinksKey { get; private set; }
        public long? FreeUserDownloadLinksExpires { get; private set; }

        public Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<OAuthUserInfo>> GetOAuthUserInfoAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<Response<ModUpdate[]>> ModUpdatesAsync(string gameDomain, NexusPeriod period, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<Response<DownloadLink[]>> DownloadLinksAsync(
            string gameDomain, int modId, int fileId, CancellationToken ct = default)
        {
            PremiumDownloadLinksCalled = true;
            return ServeLinks();
        }

        public Task<Response<DownloadLink[]>> DownloadLinksAsync(
            string gameDomain, int modId, int fileId, string nxmKey, long expiresEpoch, CancellationToken ct = default)
        {
            FreeUserDownloadLinksKey = nxmKey;
            FreeUserDownloadLinksExpires = expiresEpoch;
            return ServeLinks();
        }

        private Task<Response<DownloadLink[]>> ServeLinks()
        {
            if (DownloadLinksThrows is not null)
            {
                return Task.FromException<Response<DownloadLink[]>>(DownloadLinksThrows);
            }
            var data = DownloadLinks ?? Array.Empty<DownloadLink>();
            return Task.FromResult(new Response<DownloadLink[]>(data, NexusRateLimits.Unknown));
        }

        public Task<Response<ModInfo>> GetModInfoAsync(string gameDomain, int modId, CancellationToken ct = default)
        {
            if (ModInfoThrows is not null)
            {
                return Task.FromException<Response<ModInfo>>(ModInfoThrows);
            }
            return Task.FromResult(ModInfoResponse?.Invoke() ?? Ok(new ModInfo()));
        }

        public Task<Response<ModFile[]>> ListModFilesAsync(string gameDomain, int modId, CancellationToken ct = default)
        {
            var data = ModFilesResponse ?? Array.Empty<ModFile>();
            return Task.FromResult(new Response<ModFile[]>(data, NexusRateLimits.Unknown));
        }
    }

    /// <summary>
    /// A recording <see cref="IModImportService"/>. Each Import call captures
    /// the args; an optional <see cref="Throw"/> simulates a failed import.
    /// </summary>
    private sealed class RecordingImportService : IModImportService
    {
        public (Guid ContainerId, string VersionId) NextResult { get; set; } =
            (Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        public Exception? Throw { get; set; }
        public List<(string SourcePath, string ModName, ModSource Source, string Version)> Calls { get; } = new();

        public (Guid ContainerId, string VersionId) Import(string sourcePath, string modName, ModSource source, string version)
        {
            Calls.Add((sourcePath, modName, source, version));
            if (Throw is not null)
            {
                throw Throw;
            }
            return NextResult;
        }

        public string GetBaseName(string sourcePath) => throw new NotImplementedException();
        public ModContainer? FindExistingContainer(ModSource source, string modName) => null;
    }

    /// <summary>An <see cref="IHttpClientFactory"/> that always returns the same client.</summary>
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name = "") => _client;
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that captures reports synchronously for
    /// deterministic assertions (mirrors the GitHub client test pattern).
    /// </summary>
    private sealed class CapturingProgress : IProgress<long>
    {
        public List<long> Reports { get; } = new();
        public void Report(long value) => Reports.Add(value);
    }

    /// <summary>
    /// A handler whose response never completes until cancelled: proves the
    /// service honors its cancellation token during the CDN download.
    /// </summary>
    private sealed class CancellableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }
}
