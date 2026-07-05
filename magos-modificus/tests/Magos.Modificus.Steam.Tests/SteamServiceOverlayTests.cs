using Magos.Modificus.Config;

namespace Magos.Modificus.Steam.Tests;

/// <summary>
/// <see cref="ISteamService.Discover"/> user-override overlay (Phase 3 Track C,
/// Phase 1): a user-supplied path (in <see cref="MagosConfig.Discovery"/>)
/// replaces the auto-discovered value as-is; null/whitespace keeps the auto
/// value; mixed (some set, some auto) overlays only the set ones; and
/// <see cref="DiscoveryResult.Status"/> is recomputed from the final field
/// values against the platform's completeness rule (the same rule the
/// discoverer used).
/// </summary>
/// <remarks>
/// These tests build a real <see cref="ISteamService"/> through <see cref="SteamFixture"/>
/// (so the path is identical to production) and exercise the overlay through the
/// public <see cref="ISteamService.Discover"/> surface. The auto-discovered
/// baseline comes from the fixture's synthetic Steam layout; the
/// <see cref="SteamFixture.Config"/> exposes the live <see cref="MagosConfig"/>
/// so each test sets the overrides it needs.
/// </remarks>
public sealed class SteamServiceOverlayTests
{
    // ---- overlay replaces / keeps ------------------------------------------

    [Fact]
    public void User_override_replaces_the_auto_discovered_value()
    {
        // Complete Linux layout; the user overrides just the Steam install path.
        // The override replaces the auto value verbatim (no re-verify).
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        fx.Config.Discovery.UserSteamInstallPath = "/overridden/steam";

        var result = fx.Service.Discover();

        Assert.Equal("/overridden/steam", result.SteamInstallPath);
        // The non-overridden fields keep their auto-discovered values.
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), result.CompatdataPath);
        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental"), result.ProtonBinaryPath);
    }

    [Fact]
    public void Null_or_whitespace_override_keeps_the_auto_value()
    {
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        // Explicit nulls + whitespace: all four fall back to auto.
        fx.Config.Discovery.UserSteamInstallPath = null;
        fx.Config.Discovery.UserDarktideGameBinaryPath = "   ";
        fx.Config.Discovery.UserCompatdataPath = null;
        fx.Config.Discovery.UserProtonBinaryPath = "";

        var result = fx.Service.Discover();

        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), result.CompatdataPath);
        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental"), result.ProtonBinaryPath);
        Assert.Equal(DiscoveryStatus.Complete, result.Status);
    }

    [Fact]
    public void Mixed_overlays_apply_only_the_set_fields()
    {
        // Override two of four; the other two keep their auto values.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        fx.Config.Discovery.UserSteamInstallPath = "/o/steam";
        fx.Config.Discovery.UserCompatdataPath = "/o/compatdata";

        var result = fx.Service.Discover();

        Assert.Equal("/o/steam", result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath); // auto
        Assert.Equal("/o/compatdata", result.CompatdataPath);
        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental"), result.ProtonBinaryPath); // auto
    }

    // ---- Status recomputation ----------------------------------------------

    [Fact]
    public void Status_recomputes_to_Complete_when_an_override_fills_the_last_gap()
    {
        // Auto-discovery is Partial (missing Proton). The user supplies a Proton
        // binary path; status is recomputed against the Linux rule (which
        // requires Proton) and becomes Complete.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // No Proton scaffolded: auto-discovery leaves it null.

        // Sanity: without overrides, the result is Partial.
        var auto = fx.Service.Discover();
        Assert.Equal(DiscoveryStatus.Partial, auto.Status);
        Assert.Null(auto.ProtonBinaryPath);

        // Override fills the gap; the status recomputes to Complete.
        fx.Config.Discovery.UserProtonBinaryPath = "/user/supplied/proton";

        var result = fx.Service.Discover();

        Assert.Equal("/user/supplied/proton", result.ProtonBinaryPath);
        Assert.Equal(DiscoveryStatus.Complete, result.Status);
    }

    [Fact]
    public void Status_recomputes_to_Complete_from_Failed_when_user_supplies_every_field()
    {
        // Auto-discovery yields Failed (no Steam at all). The user supplies all
        // four paths; the Linux rule is satisfied and status becomes Complete.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        // Nothing scaffolded.

        var auto = fx.Service.Discover();
        Assert.Equal(DiscoveryStatus.Failed, auto.Status);

        fx.Config.Discovery.UserSteamInstallPath = "/u/steam";
        fx.Config.Discovery.UserDarktideGameBinaryPath = "/u/darktide.exe";
        fx.Config.Discovery.UserCompatdataPath = "/u/compatdata";
        fx.Config.Discovery.UserProtonBinaryPath = "/u/proton";

        var result = fx.Service.Discover();

        Assert.Equal("/u/steam", result.SteamInstallPath);
        Assert.Equal("/u/darktide.exe", result.DarktideGameBinaryPath);
        Assert.Equal("/u/compatdata", result.CompatdataPath);
        Assert.Equal("/u/proton", result.ProtonBinaryPath);
        Assert.Equal(DiscoveryStatus.Complete, result.Status);
    }

    [Fact]
    public void Status_stays_Partial_when_an_override_does_not_close_every_gap()
    {
        // Auto-discovery is Partial (missing Proton + missing compatdata). The
        // user supplies only Proton; compatdata is still missing, so the Linux
        // rule keeps status Partial.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        // No compatdata, no Proton.

        fx.Config.Discovery.UserProtonBinaryPath = "/u/proton";

        var result = fx.Service.Discover();

        Assert.Equal("/u/proton", result.ProtonBinaryPath);
        Assert.Null(result.CompatdataPath);
        Assert.Equal(DiscoveryStatus.Partial, result.Status);
    }

    [Fact]
    public void Windows_recompute_ignores_compatdata_and_proton()
    {
        // On Windows the completeness rule is Steam + Darktide only. A Failed
        // result (no Steam) becomes Complete when the user supplies Steam +
        // Darktide, even though compatdata + Proton are left null.
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        // Nothing scaffolded.

        var auto = fx.Service.Discover();
        Assert.Equal(DiscoveryStatus.Failed, auto.Status);

        fx.Config.Discovery.UserSteamInstallPath = @"C:\Steam";
        fx.Config.Discovery.UserDarktideGameBinaryPath = @"C:\Darktide\Binaries\Darktide.exe";

        var result = fx.Service.Discover();

        Assert.Equal(@"C:\Steam", result.SteamInstallPath);
        Assert.Equal(@"C:\Darktide\Binaries\Darktide.exe", result.DarktideGameBinaryPath);
        Assert.Null(result.CompatdataPath);
        Assert.Null(result.ProtonBinaryPath);
        Assert.Equal(DiscoveryStatus.Complete, result.Status);
    }

    // ---- ProtonVersion side effect -----------------------------------------

    [Fact]
    public void Overriding_proton_binary_nuls_the_stale_proton_version_label()
    {
        // ProtonVersion is a derived description of the auto-discovered Proton
        // dir (e.g. "Proton - Experimental"). When the user overrides the
        // Proton binary path, the auto label no longer describes the path in
        // use, so it is nulled (informational field; launch uses the binary
        // path, not the label).
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        // Sanity: the auto result carries the derived label.
        var auto = fx.Service.Discover();
        Assert.Equal("Proton - Experimental", auto.ProtonVersion);

        fx.Config.Discovery.UserProtonBinaryPath = "/user/proton";

        var result = fx.Service.Discover();

        Assert.Equal("/user/proton", result.ProtonBinaryPath);
        Assert.Null(result.ProtonVersion);
    }

    // ---- overlay is config-live -------------------------------------------

    [Fact]
    public void Overlay_reads_the_live_config_so_a_write_between_calls_is_visible()
    {
        // Proves the live-read contract for the overlay: the same ISteamService
        // instance re-reads Discovery on each Discover() call, so an external
        // config change (the upcoming Settings / escape-hatch write) takes
        // effect on the next Discover() without re-constructing the service.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        // First call: no overrides; auto values in effect.
        var before = fx.Service.Discover();
        Assert.Equal(fx.SteamRoot, before.SteamInstallPath);

        // A Settings-style write lands in the live config.
        fx.Config.Discovery.UserSteamInstallPath = "/late/write/steam";

        // Second call sees the late write.
        var after = fx.Service.Discover();
        Assert.Equal("/late/write/steam", after.SteamInstallPath);
    }
}
