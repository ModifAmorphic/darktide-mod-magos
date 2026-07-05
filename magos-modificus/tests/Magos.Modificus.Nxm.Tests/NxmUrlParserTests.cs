namespace Magos.Modificus.Nxm.Tests;

/// <summary>
/// <see cref="NxmUrlParser"/>: valid <c>nxm://</c> URL shapes (mod download,
/// OAuth callback, collection) + the malformed rejections (wrong scheme,
/// missing / non-numeric / non-positive ids, OAuth missing code/state, garbage).
/// Mirrors the <c>ModSourceParserTests</c> style. Never throws: every malformed
/// case is <c>false</c>.
/// </summary>
public sealed class NxmUrlParserTests
{
    // ---- mod-download parsing --------------------------------------------

    [Theory]
    [InlineData("nxm://warhammer40kdarktide/mods/8/files/5820?key=K&expires=1&user_id=2",
        "warhammer40kdarktide", 8, 5820, "K", 1L, 2L)]
    [InlineData("nxm://warhammer40kdarktide/mods/8/files/5820",
        "warhammer40kdarktide", 8, 5820, null, null, null)]                // no query
    [InlineData("nxm://skyrim/mods/12345/files/67890",
        "skyrim", 12345, 67890, null, null, null)]                          // any game domain
    [InlineData("nxm://warhammer40kdarktide/mods/8/files/5820?user_id=2",
        "warhammer40kdarktide", 8, 5820, null, null, 2L)]                   // partial query
    [InlineData("nxm://warhammer40kdarktide/mods/8/files/5820?key=K",
        "warhammer40kdarktide", 8, 5820, "K", null, null)]                  // key only
    [InlineData("NXM://warhammer40kdarktide/Mods/8/Files/5820?key=K",
        "warhammer40kdarktide", 8, 5820, "K", null, null)]                  // scheme + path case-insensitive
    public void TryParse_accepts_valid_mod_download_urls(
        string raw, string game, int modId, int fileId,
        string? key, long? expires, long? userId)
    {
        Assert.True(NxmUrlParser.TryParse(raw, out var url));
        var mod = Assert.IsType<NxmModDownloadUrl>(url);
        Assert.Equal(game, mod.Game);
        Assert.Equal(modId, mod.ModId);
        Assert.Equal(fileId, mod.FileId);
        Assert.Equal(key, mod.Key);
        Assert.Equal(expires, mod.Expires);
        Assert.Equal(userId, mod.UserId);
        Assert.Equal(raw, mod.Raw);
    }

    [Theory]
    [InlineData("nxm://warhammer40kdarktide/mods/8/files/5820?expires=abc&user_id=xyz",
        null, null)]                                                       // non-numeric expires/user_id -> null (not rejected)
    [InlineData("nxm://warhammer40kdarktide/mods/8/files/5820?key=",
        "", null)]                                                         // empty key -> null
    public void TryParse_tolerates_bad_query_values(string raw, string? expectedKey, long? _)
    {
        // Bad query values parse to null rather than rejecting the whole URL.
        Assert.True(NxmUrlParser.TryParse(raw, out var url));
        var mod = Assert.IsType<NxmModDownloadUrl>(url);
        Assert.Equal(string.IsNullOrEmpty(expectedKey) ? null : expectedKey, mod.Key);
    }

    // ---- OAuth callback parsing ------------------------------------------

    [Theory]
    [InlineData("nxm://oauth/callback?code=ABC&state=DEF", "ABC", "DEF")]
    [InlineData("nxm://oauth/callback?state=DEF&code=ABC", "ABC", "DEF")]   // param order swapped
    [InlineData("nxm://OAUTH/Callback?code=ABC&state=DEF", "ABC", "DEF")]   // host + path case
    public void TryParse_accepts_valid_oauth_callbacks(string raw, string code, string state)
    {
        Assert.True(NxmUrlParser.TryParse(raw, out var url));
        var oauth = Assert.IsType<NxmOAuthCallbackUrl>(url);
        Assert.Equal(code, oauth.Code);
        Assert.Equal(state, oauth.State);
        Assert.Equal(raw, oauth.Raw);
    }

    // ---- collection parsing ----------------------------------------------

    [Theory]
    [InlineData("nxm://warhammer40kdarktide/collections/someid/revisions/5",
        "warhammer40kdarktide", "someid", 5)]
    [InlineData("nxm://skyrim/collections/abc-123/revisions/42", "skyrim", "abc-123", 42)]
    public void TryParse_accepts_valid_collection_urls(string raw, string game, string id, int rev)
    {
        Assert.True(NxmUrlParser.TryParse(raw, out var url));
        var coll = Assert.IsType<NxmCollectionUrl>(url);
        Assert.Equal(game, coll.Game);
        Assert.Equal(id, coll.CollectionId);
        Assert.Equal(rev, coll.Revision);
        Assert.Equal(raw, coll.Raw);
    }

    // ---- rejections -------------------------------------------------------

    [Theory]
    [InlineData("nxm://warhammer40kdarktide/mods/8", "mod url missing files segment")]
    [InlineData("nxm://warhammer40kdarktide/mods/8/files", "mod url missing file id")]
    [InlineData("nxm://warhammer40kdarktide/mods/abc/files/5820", "non-numeric mod id")]
    [InlineData("nxm://warhammer40kdarktide/mods/8/files/xyz", "non-numeric file id")]
    [InlineData("nxm://warhammer40kdarktide/mods/0/files/5820", "zero mod id")]
    [InlineData("nxm://warhammer40kdarktide/mods/8/files/0", "zero file id")]
    [InlineData("nxm://warhammer40kdarktide/mods/-1/files/5820", "negative mod id")]
    [InlineData("nxm://oauth/callback?code=ABC", "oauth missing state")]
    [InlineData("nxm://oauth/callback?state=DEF", "oauth missing code")]
    [InlineData("nxm://oauth/callback?code=&state=DEF", "oauth empty code")]
    [InlineData("nxm://oauth/callback?code=ABC&state=", "oauth empty state")]
    [InlineData("nxm://oauth/notcallback?code=ABC&state=DEF", "oauth wrong path")]
    [InlineData("nxm://warhammer40kdarktide/collections/x/revisions/0", "zero revision")]
    [InlineData("nxm://warhammer40kdarktide/collections/x/revisions/abc", "non-numeric revision")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/8/files/5820", "wrong scheme")]
    [InlineData("nxm://garbage/path", "unknown path")]
    [InlineData("nxm://", "no host or path")]
    [InlineData("not a url", "garbage")]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace only")]
    public void TryParse_rejects_malformed_input(string raw, string reason)
    {
        Assert.False(NxmUrlParser.TryParse(raw, out _),
            $"should reject ({reason}): {raw}");
    }

    [Fact]
    public void TryParse_does_not_throw_on_garbage()
    {
        // The contract is "never throws"; a malformed input returns false.
        foreach (var malformed in new[] { "://", "nxm://\0\0", null!, "  nxm://x  " })
        {
            Assert.False(NxmUrlParser.TryParse(malformed ?? string.Empty, out _));
        }
    }
}
