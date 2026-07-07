using System.Net;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Integrations.Tests;

/// <summary>
/// Exercises <see cref="INexusClient"/> against canned HTTP responses (no real
/// network): v1 endpoint paths, response parsing, rate-limit header parsing,
/// rate-limit detection, error mapping, the apikey-auth gate, and the 401
/// retry-after-refresh path (via a fake auth factory).
/// </summary>
public sealed class NexusClientTests
{
    private const string ApiBase = "https://api.nexusmods.com/";

    private const string ValidateJson = @"
    {
      ""user_id"": 12345,
      ""key"": ""the-key"",
      ""name"": ""TestUser"",
      ""is_premium"": true,
      ""is_supporter"": false,
      ""email"": ""test@example.com"",
      ""profile_url"": ""https://www.nexusmods.com/users/12345""
    }";

    private const string OAuthUserInfoJson = @"
    {
      ""sub"": ""12345"",
      ""name"": ""OAuthUser"",
      ""avatar"": ""https://www.nexusmods.com/avatar.png"",
      ""membership_roles"": [""member"", ""premium""]
    }";

    private const string ModUpdatesJson = @"
    [
      { ""mod_id"": 100, ""latest_file_update"": 1717000000, ""latest_mod_activity"": 1717000100 },
      { ""mod_id"": 200, ""latest_file_update"": 1717000200, ""latest_mod_activity"": 1717000300 }
    ]";

    private const string DownloadLinksJson = @"
    [
      { ""name"": ""CDN-A"", ""short_name"": ""cdn-a"", ""URI"": ""https://cdn-a.example.com/file.zip"" },
      { ""name"": ""CDN-B"", ""short_name"": ""cdn-b"", ""URI"": ""https://cdn-b.example.com/file.zip"" }
    ]";

    private const string ModInfoJson = @"
    {
      ""name"": ""Test Mod"",
      ""summary"": ""A summary."",
      ""description"": ""A description."",
      ""mod_id"": 8,
      ""game_id"": 3333,
      ""domain_name"": ""warhammer40kdarktide"",
      ""version"": ""1.2.3"",
      ""endorsement_count"": 42
    }";

    private const string ModFilesJson = @"
    {
      ""files"": [
        { ""file_id"": 100, ""file_name"": ""mod_v1.zip"", ""name"": ""Mod v1"", ""version"": ""1.0"", ""size"": 1024 },
        { ""file_id"": 200, ""file_name"": ""mod_v2.zip"", ""name"": ""Mod v2"", ""version"": ""2.0"", ""size"": 2048 }
      ]
    }";

    // ---- client construction ----------------------------------------------

    /// <summary>
    /// Builds a NexusClient wired to a stub handler + a "fake auth factory" that
    /// applies a fixed bearer token + always reports authenticated. The default
    /// <see cref="FakeConfigLoader"/> sets <c>AuthMethod = OAuth</c> + a token so
    /// the client's pre-send gate passes.
    /// </summary>
    private static (NexusClient client, StubHttpMessageHandler handler) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        NexusAuthMethod method = NexusAuthMethod.OAuth,
        string? apiKey = null,
        NexusOAuthTokens? tokens = null,
        INexusAuthMessageFactory? authFactory = null,
        string apiBase = ApiBase,
        string oauthBase = "https://users.nexusmods.com")
    {
        var handler = new StubHttpMessageHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(apiBase) };

        var nexus = new NexusConfig
        {
            BaseUrl = apiBase.TrimEnd('/'),
            OAuthBaseUrl = oauthBase,
            AuthMethod = method,
            ApiKey = apiKey,
            OAuth = tokens ?? new NexusOAuthTokens("access-token", "refresh-token", "openid profile email", DateTimeOffset.UtcNow.AddHours(1)),
        };
        var config = new CuratorConfig();
        config.Integrations.Nexus = nexus;
        var loader = new FakeConfigLoader { Config = config };

        var auth = authFactory ?? new FakeAuthFactory(authenticated: true);
        var client = new NexusClient(http, auth, loader, NullLogger<NexusClient>.Instance);
        return (client, handler);
    }

    // ---- Validate ---------------------------------------------------------

    [Fact]
    public async Task ValidateAsync_hits_users_validate_endpoint_and_parses()
    {
        var (client, handler) = CreateClient(_ => HttpResponses.NexusOk(ValidateJson, daily: 1000, hourly: 100));

        var response = await client.ValidateAsync();

        Assert.Equal("TestUser", response.Data.Name);
        Assert.Equal(12345, response.Data.UserId);
        Assert.True(response.Data.IsPremium);
        Assert.Equal(1000, response.RateLimits.DailyLimit);
        Assert.Equal(100, response.RateLimits.HourlyRemaining);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(ApiBase + "v1/users/validate.json"), request.RequestUri);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    // ---- GetOAuthUserInfo -------------------------------------------------

    [Fact]
    public async Task GetOAuthUserInfoAsync_hits_oauth_userinfo_endpoint_and_parses_roles()
    {
        var (client, handler) = CreateClient(_ => HttpResponses.NexusOk(OAuthUserInfoJson, daily: 1000, hourly: 100));

        var response = await client.GetOAuthUserInfoAsync();

        Assert.Equal("OAuthUser", response.Data.Name);
        Assert.True(response.Data.IsPremium); // roles contain "premium"
        Assert.Contains(NexusMembershipRole.Premium, response.Data.MembershipRoles);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://users.nexusmods.com/oauth/userinfo"), request.RequestUri);
    }

    [Fact]
    public async Task GetOAuthUserInfoAsync_uses_configured_oauth_base_url()
    {
        // The userinfo endpoint hangs off the OAuth base URL, not the API base.
        // Tests override the OAuth base URL; the client must compose against it
        // (not hard-code the production URL).
        var (client, handler) = CreateClient(
            _ => HttpResponses.NexusOk(OAuthUserInfoJson),
            oauthBase: "https://users.test.local");

        await client.GetOAuthUserInfoAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://users.test.local/oauth/userinfo"), request.RequestUri);
    }

    // ---- ModUpdates -------------------------------------------------------

    [Fact]
    public async Task ModUpdatesAsync_hits_updated_endpoint_with_period_query()
    {
        var (client, handler) = CreateClient(_ => HttpResponses.NexusOk(ModUpdatesJson));

        var response = await client.ModUpdatesAsync("warhammer40kdarktide", NexusPeriod.Month);

        Assert.Equal(2, response.Data.Length);
        Assert.Equal(100L, response.Data[0].ModId);
        Assert.Equal(200L, response.Data[1].ModId);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1717000100),
            response.Data[0].LatestModActivityUtc);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            new Uri(ApiBase + "v1/games/warhammer40kdarktide/mods/updated.json?period=1m"),
            request.RequestUri);
    }

    [Theory]
    [InlineData(NexusPeriod.Day, "1d")]
    [InlineData(NexusPeriod.Week, "1w")]
    [InlineData(NexusPeriod.Month, "1m")]
    public async Task ModUpdatesAsync_period_maps_to_query_string(NexusPeriod period, string expected)
    {
        // The period-string mapping is part of the contract; assert via the
        // outgoing URI on each call.
        var (client, handler) = CreateClient(_ => HttpResponses.NexusOk(ModUpdatesJson));

        await client.ModUpdatesAsync("skyrim", period);

        var request = Assert.Single(handler.Requests);
        Assert.Contains($"period={expected}", request.RequestUri!.Query);
    }

    // ---- DownloadLinks ----------------------------------------------------

    [Fact]
    public async Task DownloadLinksAsync_premium_hits_download_link_endpoint()
    {
        var (client, handler) = CreateClient(_ => HttpResponses.NexusOk(DownloadLinksJson));

        var response = await client.DownloadLinksAsync("warhammer40kdarktide", modId: 8, fileId: 5820);

        Assert.Equal(2, response.Data.Length);
        Assert.Equal(
            new Uri("https://cdn-a.example.com/file.zip"),
            response.Data[0].Uri);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            new Uri(ApiBase + "v1/games/warhammer40kdarktide/mods/8/files/5820/download_link.json"),
            request.RequestUri);
    }

    [Fact]
    public async Task DownloadLinksAsync_free_user_appends_key_and_expires_query()
    {
        var (client, handler) = CreateClient(_ => HttpResponses.NexusOk(DownloadLinksJson));

        await client.DownloadLinksAsync("warhammer40kdarktide", 8, 5820, nxmKey: "ABC", expiresEpoch: 12345L);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            new Uri(ApiBase + "v1/games/warhammer40kdarktide/mods/8/files/5820/download_link.json?key=ABC&expires=12345"),
            request.RequestUri);
    }

    [Fact]
    public async Task DownloadLinksAsync_free_user_url_encodes_the_key()
    {
        // An nxm key with reserved chars (e.g. & or =) must be encoded so the
        // query string parses cleanly on the server.
        var (client, handler) = CreateClient(_ => HttpResponses.NexusOk(DownloadLinksJson));

        await client.DownloadLinksAsync("warhammer40kdarktide", 8, 5820, nxmKey: "a&b=c", expiresEpoch: 1L);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("key=a%26b%3Dc", request.RequestUri!.Query);
    }

    // ---- ModInfo + ListModFiles ------------------------------------------

    [Fact]
    public async Task GetModInfoAsync_hits_mod_endpoint_and_parses()
    {
        var (client, handler) = CreateClient(_ => HttpResponses.NexusOk(ModInfoJson));

        var response = await client.GetModInfoAsync("warhammer40kdarktide", modId: 8);

        Assert.Equal("Test Mod", response.Data.Name);
        Assert.Equal("1.2.3", response.Data.Version);
        Assert.Equal(8, response.Data.ModId);
        Assert.Equal("warhammer40kdarktide", response.Data.DomainName);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            new Uri(ApiBase + "v1/games/warhammer40kdarktide/mods/8.json"),
            request.RequestUri);
    }

    [Fact]
    public async Task ListModFilesAsync_unwraps_files_envelope()
    {
        var (client, handler) = CreateClient(_ => HttpResponses.NexusOk(ModFilesJson));

        var response = await client.ListModFilesAsync("warhammer40kdarktide", modId: 8);

        Assert.Equal(2, response.Data.Length);
        Assert.Equal(100L, response.Data[0].FileId);
        Assert.Equal("mod_v1.zip", response.Data[0].FileName);
        Assert.Equal(2048L, response.Data[1].Size);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            new Uri(ApiBase + "v1/games/warhammer40kdarktide/mods/8/files.json"),
            request.RequestUri);
    }

    // ---- error mapping ----------------------------------------------------

    [Fact]
    public async Task Non_2xx_throws_NexusApiException_with_status_and_message()
    {
        var (client, _) = CreateClient(_ =>
            HttpResponses.Json(@"{""message"":""Forbidden""}", HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<NexusApiException>(
            () => client.GetModInfoAsync("warhammer40kdarktide", 8));
        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("Forbidden", ex.Message);
    }

    [Fact]
    public async Task Non_json_error_body_falls_back_to_reason_phrase()
    {
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream is down"),
        });

        var ex = await Assert.ThrowsAsync<NexusApiException>(
            () => client.GetModInfoAsync("warhammer40kdarktide", 8));
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("Bad Gateway", ex.Message);
    }

    [Fact]
    public async Task Rate_limit_429_throws_NexusRateLimitException_with_limits()
    {
        var (client, _) = CreateClient(_ => HttpResponses.NexusRateLimited());

        var ex = await Assert.ThrowsAsync<NexusRateLimitException>(
            () => client.GetModInfoAsync("warhammer40kdarktide", 8));
        Assert.Equal(429, ex.StatusCode);
        Assert.NotNull(ex.Limits);
        Assert.Equal(0, ex.Limits!.DailyRemaining);
        Assert.Equal(0, ex.Limits.HourlyRemaining);
    }

    [Fact]
    public async Task NexusRateLimitException_is_a_NexusApiException()
    {
        var (client, _) = CreateClient(_ => HttpResponses.NexusRateLimited());

        var ex = await Assert.ThrowsAsync<NexusRateLimitException>(
            () => client.GetModInfoAsync("warhammer40kdarktide", 8));
        Assert.IsAssignableFrom<NexusApiException>(ex);
    }

    // ---- auth gate --------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_client_throws_NexusNotAuthenticatedException()
    {
        // The auth factory reports not-authenticated (AuthMethod == None with no
        // credentials). The client must surface this BEFORE sending a request.
        var (client, handler) = CreateClient(
            _ => HttpResponses.NexusOk(ValidateJson),
            method: NexusAuthMethod.None,
            apiKey: null,
            tokens: null,
            authFactory: new FakeAuthFactory(authenticated: false));

        await Assert.ThrowsAsync<NexusNotAuthenticatedException>(
            () => client.ValidateAsync());
        Assert.Empty(handler.Requests);
    }

    // ---- 401 retry-after-refresh ------------------------------------------

    [Fact]
    public async Task On_401_with_successful_refresh_retries_once()
    {
        // First call: 401. The factory's OnUnauthorizedAsync returns true
        // (refresh succeeded); the client must retry the request. Second call:
        // 200 with the real payload. Total requests: 2.
        var calls = 0;
        var (client, handler) = CreateClient(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : HttpResponses.NexusOk(ValidateJson);
        }, authFactory: new FakeAuthFactory(authenticated: true, refreshSucceeds: true));

        var response = await client.ValidateAsync();

        Assert.Equal("TestUser", response.Data.Name);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task On_401_with_failed_refresh_throws_NexusApiException()
    {
        // The factory's OnUnauthorizedAsync returns false (no refresh possible).
        // The client must surface the original 401, not retry.
        var (client, handler) = CreateClient(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            authFactory: new FakeAuthFactory(authenticated: true, refreshSucceeds: false));

        var ex = await Assert.ThrowsAsync<NexusApiException>(() => client.ValidateAsync());
        Assert.Equal(401, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task On_second_401_after_refresh_does_not_retry_again()
    {
        // The retry must be bounded to one: a second 401 (the refreshed token is
        // also invalid) surfaces as NexusApiException, not an infinite loop.
        var (client, handler) = CreateClient(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            authFactory: new FakeAuthFactory(authenticated: true, refreshSucceeds: true));

        var ex = await Assert.ThrowsAsync<NexusApiException>(() => client.ValidateAsync());
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(2, handler.Requests.Count); // original + one retry
    }

    // ---- argument validation ---------------------------------------------

    [Fact]
    public async Task ModUpdatesAsync_null_game_throws()
    {
        var (client, _) = CreateClient(_ => HttpResponses.NexusOk(ModUpdatesJson));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.ModUpdatesAsync(null!, NexusPeriod.Day));
    }

    [Fact]
    public async Task DownloadLinksAsync_free_null_key_throws()
    {
        var (client, _) = CreateClient(_ => HttpResponses.NexusOk(DownloadLinksJson));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.DownloadLinksAsync("game", 1, 1, null!, 1L));
    }

    // ---- fake -------------------------------------------------------------

    /// <summary>
    /// A fake <see cref="INexusAuthMessageFactory"/> with a configurable
    /// authenticated flag + a configurable refresh outcome, used to drive the
    /// client's auth gate + 401-retry path deterministically.
    /// </summary>
    private sealed class FakeAuthFactory : INexusAuthMessageFactory
    {
        private readonly bool _authenticated;
        private readonly bool _refreshSucceeds;

        public FakeAuthFactory(bool authenticated, bool refreshSucceeds = false)
        {
            _authenticated = authenticated;
            _refreshSucceeds = refreshSucceeds;
        }

        public int RefreshCalls { get; private set; }

        public ValueTask<HttpRequestMessage> CreateAsync(HttpMethod method, Uri uri, CancellationToken ct)
        {
            var request = new HttpRequestMessage(method, uri);
            // Apply the same app-identification headers the real factories do, so
            // the stub handler can assert on them when needed.
            request.Headers.TryAddWithoutValidation("Application-Name", "Modificus-Curator");
            return ValueTask.FromResult(request);
        }

        public ValueTask<bool> OnUnauthorizedAsync(CancellationToken ct)
        {
            RefreshCalls++;
            return ValueTask.FromResult(_refreshSucceeds);
        }

        public ValueTask<bool> IsAuthenticatedAsync(CancellationToken ct) =>
            ValueTask.FromResult(_authenticated);
    }
}
