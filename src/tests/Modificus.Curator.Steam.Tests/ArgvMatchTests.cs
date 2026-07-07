namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Unit tests for <see cref="Modificus.Curator.Steam.LinuxProcessLookup.MatchesArgv0"/>.
/// This is the pure matching kernel of the Linux <c>/proc</c> <c>argv[0]</c>
/// scan; the live signal under Proton is
/// <c>S:\...\Darktide.exe</c>, whose stem <c>Darktide</c> matches
/// <see cref="SteamDiscoveryOptions.GameProcessName"/>. Direct unit coverage
/// avoids depending on a real process tree.
/// </summary>
public sealed class ArgvMatchTests
{
    [Theory]
    // The live Proton signal: wine passes the launched exe path as argv[0].
    [InlineData(@"S:\common\Warhammer 40,000 DARKTIDE\binaries\Darktide.exe", "Darktide", true)]
    // The wine steam wrapper -- must NOT match (argv[0] only, not the whole cmdline).
    [InlineData(@"c:\windows\system32\steam.exe", "Darktide", false)]
    // A self/script process -- must NOT match.
    [InlineData("/usr/bin/bash", "Darktide", false)]
    // Bare exe name.
    [InlineData("Darktide.exe", "Darktide", true)]
    // Case-insensitive stem comparison.
    [InlineData("darktide.EXE", "Darktide", true)]
    // Empty argv0 guard.
    [InlineData("", "Darktide", false)]
    // Null argv0 guard.
    [InlineData(null, "Darktide", false)]
    // Differing stem.
    [InlineData(@"C:\games\Other.exe", "Darktide", false)]
    public void MatchesArgv0_returns_expected(string? argv0, string processName, bool expected)
    {
        Assert.Equal(expected, Modificus.Curator.Steam.LinuxProcessLookup.MatchesArgv0(argv0, processName));
    }
}
