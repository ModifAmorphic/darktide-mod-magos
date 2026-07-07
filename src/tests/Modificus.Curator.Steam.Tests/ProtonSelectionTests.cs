namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// The Proton selection heuristic, step by step:
/// 1. <c>Proton - Experimental</c> in steamapps/common wins when present.
/// 2. Else the highest-versioned <c>Proton X.Y</c> in steamapps/common.
/// 3. Else a build under compatibilitytools.d (ProtonUp-GE).
/// 4. Else null (escape hatch).
/// Each chosen source is recorded in <see cref="DiscoveryResult.Warnings"/>.
/// </summary>
public sealed class ProtonSelectionTests
{
    [Fact]
    public void Experimental_present_is_chosen_over_numbered_versions()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");
        fx.WithProtonInCommon(fx.SteamRoot, "Proton 9.0"); // would be "highest version"
        fx.WithProtonInCommon(fx.SteamRoot, "Proton 8.0");

        var result = fx.Service.Discover();

        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental"), result.ProtonBinaryPath);
        Assert.Equal("Proton - Experimental", result.ProtonVersion);
        Assert.Contains(result.Warnings, w => w.Contains("Experimental", StringComparison.Ordinal));
    }

    [Fact]
    public void Absent_experimental_picks_highest_versioned_proton()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton 5.13");
        fx.WithProtonInCommon(fx.SteamRoot, "Proton 9.0");
        fx.WithProtonInCommon(fx.SteamRoot, "Proton 8.0");

        var result = fx.Service.Discover();

        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton 9.0"), result.ProtonBinaryPath);
        Assert.Equal("Proton 9.0", result.ProtonVersion);
        Assert.Contains(result.Warnings, w => w.Contains("highest-versioned", StringComparison.Ordinal));
    }

    [Fact]
    public void Version_ranking_is_numeric_not_lexicographic()
    {
        // Lexicographic would put "Proton 5.13" above "Proton 9.0" ("5" < "9"
        // but "5.13" > "9.0" string-compared). Numeric: 9.0 > 5.13.
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton 5.13");
        fx.WithProtonInCommon(fx.SteamRoot, "Proton 9.0");

        var result = fx.Service.Discover();

        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton 9.0"), result.ProtonBinaryPath);
    }

    [Fact]
    public void No_common_proton_falls_back_to_compatibility_tools()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // No Proton in steamapps/common; a GE build in compatibilitytools.d.
        fx.WithProtonInCompatTools("GE-Proton9-3");

        var result = fx.Service.Discover();

        Assert.Equal(fx.ExpectedCompatToolsProtonPath("GE-Proton9-3"), result.ProtonBinaryPath);
        Assert.Equal("GE-Proton9-3", result.ProtonVersion);
        Assert.Contains(result.Warnings, w => w.Contains("compatibilitytools.d", StringComparison.Ordinal));
    }

    [Fact]
    public void Compatibility_tools_ranked_by_parsed_version()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCompatTools("GE-Proton8-26");
        fx.WithProtonInCompatTools("GE-Proton9-3");

        var result = fx.Service.Discover();

        Assert.Equal(fx.ExpectedCompatToolsProtonPath("GE-Proton9-3"), result.ProtonBinaryPath);
    }

    [Fact]
    public void No_proton_anywhere_yields_null_and_escape_hatch_warning()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // No Proton in common, none in compat tools.

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
        Assert.Null(result.ProtonVersion);
        Assert.Contains(result.Warnings, w => w.Contains("No Proton build found", StringComparison.Ordinal));
    }

    [Fact]
    public void Darktide_game_dir_is_not_mistaken_for_a_proton_build()
    {
        // Regression guard: "Warhammer 40,000 DARKTIDE" sits in steamapps/common
        // and contains digits ("40"), so a naive version-parse would rank it
        // (as 40.0) above every real Proton. Proton selection must require a
        // `proton` script and so ignore the game dir.
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton 9.0");

        var result = fx.Service.Discover();

        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton 9.0"), result.ProtonBinaryPath);
        Assert.DoesNotContain(result.ProtonBinaryPath!, "Warhammer", StringComparison.Ordinal);
    }
}
