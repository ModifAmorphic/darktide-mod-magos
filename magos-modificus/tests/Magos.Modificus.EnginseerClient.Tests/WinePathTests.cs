namespace Magos.Modificus.EnginseerClient.Tests;

/// <summary>
/// Direct unit tests for the <c>Z:\</c> translation helper — the Linux launch
/// path's correctness hinge. Translation is pure (no I/O), so these are
/// exhaustive on the shape.
/// </summary>
public sealed class WinePathTests
{
    [Theory]
    [InlineData("/home/u/mods", @"Z:\home\u\mods")]
    [InlineData("/home/u/.local/share/Magos Modificus/profiles/abc/mods",
        @"Z:\home\u\.local\share\Magos Modificus\profiles\abc\mods")]
    [InlineData("/home/u/.steam/steam/steamapps/common/Warhammer 40,000 DARKTIDE/binaries/Darktide.exe",
        @"Z:\home\u\.steam\steam\steamapps\common\Warhammer 40,000 DARKTIDE\binaries\Darktide.exe")]
    [InlineData("/opt/enginseer", @"Z:\opt\enginseer")]
    public void ToWine_translates_absolute_posix_path(string posix, string expected)
    {
        Assert.Equal(expected, WinePath.ToWine(posix));
    }

    [Fact]
    public void ToWine_root_maps_to_z_root()
    {
        // The POSIX root maps to the Wine Z: drive root.
        Assert.Equal(@"Z:\", WinePath.ToWine("/"));
    }

    [Fact]
    public void ToWine_replaces_every_forward_slash()
    {
        // No forward slashes survive — the launcher (under Wine) needs backslashes.
        var result = WinePath.ToWine("/a/b/c/d");
        Assert.DoesNotContain('/', result);
        Assert.Equal(@"Z:\a\b\c\d", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToWine_rejects_blank(string value)
    {
        Assert.Throws<ArgumentException>(() => WinePath.ToWine(value));
    }
}
