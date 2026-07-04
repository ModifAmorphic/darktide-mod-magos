namespace Magos.Modificus.SharedMods.Tests;

/// <summary>
/// <see cref="ModSourceParser"/>: URL → canonical <see cref="ModSource"/>
/// parsing for the import modal. Covers the accepted shapes (Nexus URL/id,
/// GitHub URL with/without <c>.git</c> + trailing slash) + the malformed
/// rejections (wrong host, too few segments, non-numeric id). Never throws:
/// every malformed case is <c>false</c>.
/// </summary>
public sealed class ModSourceParserTests
{
    // ---- Nexus parsing -----------------------------------------------------

    [Theory]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/12345", 12345)]
    [InlineData("https://nexusmods.com/warhammer40kdarktide/mods/12345", 12345)]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/12345/", 12345)]      // trailing slash
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/12345?tab=files", 12345)] // query string
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/12345/#content", 12345)] // fragment
    [InlineData("HTTPS://WWW.NEXUSMODS.COM/Warhammer40kDarktide/mods/12345", 12345)]        // host + slug case
    public void TryParseNexus_accepts_valid_url_variants(string url, int expectedModId)
    {
        Assert.True(ModSourceParser.TryParseNexus(url, out var source));
        Assert.Equal(expectedModId, source.ModId);
    }

    [Theory]
    [InlineData("12345", 12345)]
    [InlineData("  12345  ", 12345)]     // whitespace trimmed
    [InlineData("999999999", 999999999)]
    public void TryParseNexus_accepts_plain_integer_id(string input, int expectedModId)
    {
        Assert.True(ModSourceParser.TryParseNexus(input, out var source));
        Assert.Equal(expectedModId, source.ModId);
    }

    [Theory]
    [InlineData("https://www.nexusmods.com/skyrim/mods/12345", "wrong game slug")]
    [InlineData("https://example.com/warhammer40kdarktide/mods/12345", "wrong host")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/", "missing id segment")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/12345", "missing mods segment")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/abc", "non-numeric id")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/0", "zero id")]
    [InlineData("https://www.nexusmods.com/warhammer40kdarktide/mods/-1", "negative id")]
    [InlineData("not a url", "garbage")]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace only")]
    [InlineData("42abc", "integer prefix then garbage")]
    public void TryParseNexus_rejects_malformed_input(string input, string reason)
    {
        Assert.False(ModSourceParser.TryParseNexus(input, out _), $"should reject ({reason}): {input}");
    }

    [Fact]
    public void TryParseNexus_does_not_throw_on_garbage()
    {
        // The contract is "never throws"; a malformed input returns false.
        foreach (var malformed in new[] { "://", "ht!tp://x", null!, "\0\0\0" })
        {
            Assert.False(ModSourceParser.TryParseNexus(malformed ?? "", out _));
        }
    }

    // ---- GitHub parsing ----------------------------------------------------

    [Theory]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    [InlineData("https://github.com/owner/repo/", "owner", "repo")]              // trailing slash
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]           // .git suffix
    [InlineData("https://github.com/owner/repo.git/", "owner", "repo")]          // .git + trailing slash
    [InlineData("https://github.com/owner/repo/tree/main", "owner", "repo")]     // sub-path ignored
    [InlineData("https://github.com/Owner-Name/Repo.Name.git", "Owner-Name", "Repo.Name")] // names with hyphens/dots
    [InlineData("HTTPS://GITHUB.COM/Owner/Repo", "Owner", "Repo")]               // host case-insensitive
    public void TryParseGitHub_accepts_valid_url_variants(string url, string expectedOwner, string expectedRepo)
    {
        Assert.True(ModSourceParser.TryParseGitHub(url, out var source));
        Assert.Equal(expectedOwner, source.Owner);
        Assert.Equal(expectedRepo, source.Repo);
    }

    [Theory]
    [InlineData("https://github.com/owner", "only one path segment (no repo)")]
    [InlineData("https://github.com/", "no path segments")]
    [InlineData("https://github.com", "bare host")]
    [InlineData("https://example.com/owner/repo", "wrong host")]
    [InlineData("https://github.com//repo", "empty owner segment")]
    [InlineData("not a url", "garbage")]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace only")]
    public void TryParseGitHub_rejects_malformed_input(string input, string reason)
    {
        Assert.False(ModSourceParser.TryParseGitHub(input, out _), $"should reject ({reason}): {input}");
    }

    [Fact]
    public void TryParseGitHub_does_not_throw_on_garbage()
    {
        foreach (var malformed in new[] { "://", "ht!tp://x", null!, "\0\0\0" })
        {
            Assert.False(ModSourceParser.TryParseGitHub(malformed ?? "", out _));
        }
    }
}
