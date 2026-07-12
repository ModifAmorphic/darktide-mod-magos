namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Unit tests for
/// <see cref="Modificus.Curator.Steam.SteamPathNormalizer.NormalizeWindowsPath"/>,
/// the pure normalizer that converts the Unix-style path Steam writes to the
/// Windows registry (lowercase drive letter + forward slashes) into native
/// Windows form (uppercase drive + backslashes). Pure string input/output; no
/// filesystem; runs on any OS.
/// </summary>
/// <remarks>
/// The helper lives on a platform-neutral type (not on
/// <c>SteamRegistryReader</c>, which is annotated
/// <c>[SupportedOSPlatform("windows")]</c>) so these tests build + run clean on
/// Linux CI without CA1416 analyzer friction.
/// </remarks>
public sealed class SteamPathNormalizationTests
{
    [Theory]
    // The canonical registry value Steam writes: lowercase drive + forward slashes.
    [InlineData("c:/program files (x86)/steam", @"C:\program files (x86)\steam")]
    // A different drive letter.
    [InlineData("d:/Games/Steam", @"D:\Games\Steam")]
    // Mixed separators: drive uppercase, the one forward slash swapped.
    [InlineData(@"c:\Program Files/Steam", @"C:\Program Files\Steam")]
    // Idempotent: already-normalized input is returned unchanged.
    [InlineData(@"C:\Program Files (x86)\Steam", @"C:\Program Files (x86)\Steam")]
    // No drive letter: slash swap only (harmless edge, never a real SteamPath).
    [InlineData("/foo/bar", @"\foo\bar")]
    // Uppercase drive already present: drive stays upper, slashes swap.
    [InlineData("D:/x", @"D:\x")]
    public void NormalizeWindowsPath_converts_unix_style_to_windows(string input, string expected)
    {
        Assert.Equal(expected, Modificus.Curator.Steam.SteamPathNormalizer.NormalizeWindowsPath(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeWindowsPath_returns_null_or_empty_asis(string? input)
    {
        Assert.Equal(input, Modificus.Curator.Steam.SteamPathNormalizer.NormalizeWindowsPath(input));
    }
}
