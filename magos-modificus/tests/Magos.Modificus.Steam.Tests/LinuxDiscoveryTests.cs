namespace Magos.Modificus.Steam.Tests;

/// <summary>
/// Linux discovery contract against a synthetic Steam layout: Complete happy
/// path, Partial (missing compatdata / missing Darktide / missing Proton),
/// Failed (no Steam), and multi-library search.
/// </summary>
public sealed class LinuxDiscoveryTests
{
    [Fact]
    public void Complete_layout_returns_Complete_with_correct_paths()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), result.CompatdataPath);
        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental"), result.ProtonBinaryPath);
        Assert.Equal("Proton - Experimental", result.ProtonVersion);
        Assert.NotNull(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("Proton - Experimental", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_compatdata_returns_Partial_with_null_CompatdataPath()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");
        // No compatdata → the only gap.

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.NotNull(result.SteamInstallPath);
        Assert.NotNull(result.DarktideGameBinaryPath);
        Assert.Null(result.CompatdataPath);
        Assert.NotNull(result.ProtonBinaryPath);
    }

    [Fact]
    public void Missing_darktide_returns_Partial_with_null_DarktideGameBinaryPath()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.NotNull(result.SteamInstallPath);
        Assert.Null(result.DarktideGameBinaryPath);
    }

    [Fact]
    public void Missing_proton_returns_Partial_with_null_ProtonBinaryPath()
    {
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // No Proton anywhere.

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
        Assert.Null(result.ProtonVersion);
        // Escape-hatch warning surfaces so the UI knows to prompt.
        Assert.Contains(result.Warnings, w => w.Contains("No Proton build found", StringComparison.Ordinal));
    }

    [Fact]
    public void No_steam_at_all_returns_Failed_with_all_nulls()
    {
        using var fx = new SteamFixture();
        // Nothing scaffolded — no native root, no flatpak.

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Failed, result.Status);
        Assert.Null(result.SteamInstallPath);
        Assert.Null(result.DarktideGameBinaryPath);
        Assert.Null(result.CompatdataPath);
        Assert.Null(result.ProtonBinaryPath);
    }

    [Fact]
    public void Darktide_in_secondary_library_is_found()
    {
        // Two libraries: Darktide lives in the secondary one — proves the VDF is
        // parsed + each library is probed in order.
        using var fx = new SteamFixture();
        var secondary = Path.Combine(fx.TempRoot, "secondary-lib");
        Directory.CreateDirectory(secondary);
        fx.WithLibraryFoldersAtSteamRoot(fx.SteamRoot, secondary);
        fx.WithDarktide(secondary);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedDarktidePath(secondary), result.DarktideGameBinaryPath);
        // Multi-library search surfaces a non-fatal warning.
        Assert.Contains(result.Warnings, w => w.Contains("Searched 2 Steam libraries", StringComparison.Ordinal));
    }

    [Fact]
    public void Compatdata_in_secondary_library_is_found()
    {
        // The Proton prefix (compatdata) is created on whichever drive Steam
        // chose at install time — frequently a Steam *library* drive rather than
        // the main install (e.g. /games/steamapps/compatdata/<appid>/). Discovery
        // must probe each library, not just the main install, or it reports
        // CompatdataPath missing → DiscoveryIncomplete and blocks the launch.
        using var fx = new SteamFixture();
        var secondary = Path.Combine(fx.TempRoot, "secondary-lib");
        Directory.CreateDirectory(secondary);
        fx.WithLibraryFoldersAtSteamRoot(fx.SteamRoot, secondary);
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(secondary); // prefix only under the secondary library
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedCompatdataPath(secondary), result.CompatdataPath);
    }

    [Fact]
    public void Missing_steam_root_falls_back_to_flatpak_when_valid()
    {
        // Native root absent; flatpak root is a valid Steam install → resolves there
        // (and flags Flatpak — covered explicitly in FlatpakDiscoveryTests too).
        using var fx = new SteamFixture();
        fx.WithLibraryFoldersAtFlatpakRoot();
        fx.WithDarktide(fx.FlatpakRoot);
        fx.WithCompatdata(fx.FlatpakRoot);
        fx.WithProtonInCommon(fx.FlatpakRoot, "Proton - Experimental");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.FlatpakRoot, result.SteamInstallPath);
    }

    [Fact]
    public void Steam_root_still_searched_when_vdf_omits_it()
    {
        // The VDF lists only a secondary library (a malformed/incomplete VDF that
        // omits the Steam install root itself). Darktide lives in the Steam root's
        // own steamapps/common — discovery must still find it by treating the
        // resolved Steam root as an implicit library.
        using var fx = new SteamFixture();
        var secondary = Path.Combine(fx.TempRoot, "listed-lib");
        Directory.CreateDirectory(secondary);
        fx.WithLibraryFoldersAtSteamRoot(secondary); // VDF omits fx.SteamRoot
        fx.WithDarktide(fx.SteamRoot);               // Darktide is at the Steam root
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
    }
}
