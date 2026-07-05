using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.Nxm.Tests;

/// <summary>
/// <see cref="NxmRouter"/>: a mod-download URL routes to the mod-download
/// handler with parsed fields; an OAuth callback routes to the OAuth handler; a
/// collection URL routes to neither (logged only); an unparseable URL routes to
/// neither (logged only); a throwing handler does not propagate.
/// </summary>
public sealed class NxmRouterTests
{
    private static NxmRouter CreateRouter(
        FakeModDownloadHandler? mod = null, FakeOAuthHandler? oauth = null) =>
        new(mod ?? new FakeModDownloadHandler(),
            oauth ?? new FakeOAuthHandler(),
            NullLogger<NxmRouter>.Instance);

    [Fact]
    public async Task Mod_download_url_routes_to_mod_download_handler()
    {
        var mod = new FakeModDownloadHandler();
        var oauth = new FakeOAuthHandler();
        var router = CreateRouter(mod, oauth);

        await router.RouteAsync("nxm://warhammer40kdarktide/mods/8/files/5820?key=K&expires=1&user_id=2");

        Assert.Single(mod.Handled);
        Assert.Equal("warhammer40kdarktide", mod.Handled[0].Game);
        Assert.Equal(8, mod.Handled[0].ModId);
        Assert.Equal(5820, mod.Handled[0].FileId);
        Assert.Equal("K", mod.Handled[0].Key);
        Assert.Equal(1L, mod.Handled[0].Expires);
        Assert.Equal(2L, mod.Handled[0].UserId);
        Assert.Empty(oauth.Handled);
    }

    [Fact]
    public async Task Oauth_callback_routes_to_oauth_handler()
    {
        var mod = new FakeModDownloadHandler();
        var oauth = new FakeOAuthHandler();
        var router = CreateRouter(mod, oauth);

        await router.RouteAsync("nxm://oauth/callback?code=ABC&state=DEF");

        Assert.Empty(mod.Handled);
        Assert.Single(oauth.Handled);
        Assert.Equal("ABC", oauth.Handled[0].Code);
        Assert.Equal("DEF", oauth.Handled[0].State);
    }

    [Fact]
    public async Task Collection_url_routes_to_neither_handler()
    {
        var mod = new FakeModDownloadHandler();
        var oauth = new FakeOAuthHandler();
        var router = CreateRouter(mod, oauth);

        await router.RouteAsync("nxm://warhammer40kdarktide/collections/someid/revisions/5");

        Assert.Empty(mod.Handled);
        Assert.Empty(oauth.Handled);
    }

    [Fact]
    public async Task Unparseable_url_routes_to_neither_handler()
    {
        var mod = new FakeModDownloadHandler();
        var oauth = new FakeOAuthHandler();
        var router = CreateRouter(mod, oauth);

        await router.RouteAsync("nxm://garbage/path");
        await router.RouteAsync("not even a url");

        Assert.Empty(mod.Handled);
        Assert.Empty(oauth.Handled);
    }

    [Fact]
    public async Task Throwing_handler_does_not_propagate()
    {
        var mod = new FakeModDownloadHandler(throwOnFirst: true);
        var router = CreateRouter(mod);

        // The router catches the handler exception; the await does not throw.
        await router.RouteAsync("nxm://warhammer40kdarktide/mods/1/files/1");
        Assert.Single(mod.Handled);
    }

    [Fact]
    public async Task Null_url_throws_argument_null()
    {
        var router = CreateRouter();
        await Assert.ThrowsAsync<ArgumentNullException>(() => router.RouteAsync(null!));
    }

    // ---- fakes -----------------------------------------------------------

    private sealed class FakeModDownloadHandler : INxmModDownloadHandler
    {
        private readonly bool _throwOnFirst;
        private bool _hasThrown;
        public List<NxmModDownloadUrl> Handled { get; } = new();

        public FakeModDownloadHandler(bool throwOnFirst = false) => _throwOnFirst = throwOnFirst;

        public Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default)
        {
            Handled.Add(url);
            if (_throwOnFirst && !_hasThrown)
            {
                _hasThrown = true;
                throw new InvalidOperationException("test: mod handler throws");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOAuthHandler : INxmOAuthCallbackHandler
    {
        public List<NxmOAuthCallbackUrl> Handled { get; } = new();

        public Task HandleAsync(NxmOAuthCallbackUrl url, CancellationToken ct = default)
        {
            Handled.Add(url);
            return Task.CompletedTask;
        }
    }
}
