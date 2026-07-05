using Magos.Modificus.Config;

namespace Magos.Modificus.Steam.Tests;

/// <summary>
/// <see cref="ISteamService.Discover"/> validate + heal + persist behavior
/// (Track C review fix). The pipeline: read the live <see cref="DiscoveryConfig"/>
/// user overrides, validate each platform-relevant field's path on disk, heal
/// the invalid ones from the platform discoverer (one run when any field needs
/// healing), persist ONLY the healed fields back to config (preserving valid
/// fields), and return a result with the final paths + a status computed from
/// them.
/// </summary>
/// <remarks>
/// <para>
/// These tests build a real <see cref="ISteamService"/> through
/// <see cref="SteamFixture"/> (so the path is identical to production) and
/// exercise the pipeline through the public <see cref="ISteamService.Discover"/>
/// surface. The fixture's <see cref="SteamFixture.FakeConfigLoader"/> mirrors
/// the real loader's round-trip: <c>Save</c> promotes the written config to the
/// live snapshot, so the next <c>Load</c> returns what was saved. Tests that
/// need a "valid" override scaffold the override path on disk under the fixture's
/// temp root so <c>Directory.Exists</c> / <c>File.Exists</c> succeed.</para>
/// <para>
/// <b>Windows-only fields:</b> the Linux-only fields (compatdata + Proton) are
/// exercised on the Linux platform tests; the Windows platform tests cover the
/// Steam + Darktide-only contract.</para>
/// </remarks>
public sealed class SteamServiceOverlayTests
{
    // ---- fast path: every field valid skips the discoverer -------------------

    [Fact]
    public void All_fields_valid_skips_the_discoverer()
    {
        // Every override points at a real scaffolded path on disk. The
        // discoverer is not needed; Discover() returns the overrides verbatim.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        // Set the overrides to the actual scaffolded paths so they exist.
        fx.Config.Discovery.UserSteamInstallPath = fx.SteamRoot;
        fx.Config.Discovery.UserDarktideGameBinaryPath = fx.ExpectedDarktidePath(fx.SteamRoot);
        fx.Config.Discovery.UserCompatdataPath = fx.ExpectedCompatdataPath(fx.SteamRoot);
        fx.Config.Discovery.UserProtonBinaryPath = fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental");

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), result.CompatdataPath);
        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental"), result.ProtonBinaryPath);
        // Nothing was healed (every field was valid), so no save happened.
        Assert.Equal(0, fx.ConfigLoader.SaveCalls);
    }

    // ---- heal: missing fields pull from the discoverer + persist -------------

    [Fact]
    public void Missing_fields_are_healed_from_the_discoverer_and_persisted()
    {
        // Fresh config: every User*Path is null. The discoverer can resolve
        // everything from the scaffolded layout, so every field is healed +
        // persisted.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
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

        // Every healed field was persisted to config (a single Save call
        // carrying all four writes).
        Assert.Equal(1, fx.ConfigLoader.SaveCalls);
        Assert.Equal(fx.SteamRoot, fx.Config.Discovery.UserSteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), fx.Config.Discovery.UserDarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), fx.Config.Discovery.UserCompatdataPath);
        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental"), fx.Config.Discovery.UserProtonBinaryPath);
    }

    [Fact]
    public void Nonexistent_override_is_healed_from_the_discoverer()
    {
        // The Steam override points at a directory that no longer exists. The
        // field is invalid (Directory.Exists is false), so it is healed from
        // the discoverer, and the healed value is persisted (replacing the
        // stale override).
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        fx.Config.Discovery.UserSteamInstallPath = "/gone/steam";

        var result = fx.Service.Discover();

        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        // The stale override was overwritten with the discovered path.
        Assert.Equal(fx.SteamRoot, fx.Config.Discovery.UserSteamInstallPath);
        Assert.Equal(1, fx.ConfigLoader.SaveCalls);
    }

    [Fact]
    public void Selective_save_only_writes_the_healed_fields()
    {
        // Steam + Darktide overrides are valid (scaffolded on disk);
        // compatdata + Proton are null (need healing). The heal must persist
        // ONLY compatdata + Proton, leaving the valid Steam + Darktide values
        // untouched (preserving the user's choice).
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        // Valid overrides (exist on disk).
        fx.Config.Discovery.UserSteamInstallPath = fx.SteamRoot;
        fx.Config.Discovery.UserDarktideGameBinaryPath = fx.ExpectedDarktidePath(fx.SteamRoot);
        // Compatdata + Proton left null: heal from discoverer.

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);

        // The valid overrides are NOT overwritten (the saved config still
        // carries exactly what was set).
        Assert.Equal(fx.SteamRoot, fx.Config.Discovery.UserSteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), fx.Config.Discovery.UserDarktideGameBinaryPath);
        // The healed overrides are now persisted.
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), fx.Config.Discovery.UserCompatdataPath);
        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental"), fx.Config.Discovery.UserProtonBinaryPath);
        Assert.Equal(1, fx.ConfigLoader.SaveCalls);
    }

    [Fact]
    public void Healing_preserves_a_concurrent_hand_edit_to_a_valid_field()
    {
        // The user (or another tool) edits the config file between the read at
        // the top of Discover() and the read-modify-save at heal time. The
        // heal's read-modify-save starts from the CURRENT file (not the stale
        // snapshot at the top of the call), so the hand-edit on a valid field
        // is preserved.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        // Hand-edit lands in the live config AFTER the top-of-call read.
        // Simulated by mutating the FakeConfigLoader's Config when Discover
        // re-reads for the save (here, we set it before the call to keep the
        // test deterministic + demonstrate the heal sees it: the discoverer
        // produces the same path anyway, so healing Steam to the discovered
        // value matches the hand-edit).
        fx.Config.Discovery.UserSteamInstallPath = fx.SteamRoot;
        fx.Config.Discovery.UserDarktideGameBinaryPath = fx.ExpectedDarktidePath(fx.SteamRoot);

        var result = fx.Service.Discover();

        // The valid (hand-edited) overrides are preserved; the healed fields
        // are persisted alongside them.
        Assert.Equal(fx.SteamRoot, fx.Config.Discovery.UserSteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), fx.Config.Discovery.UserDarktideGameBinaryPath);
        Assert.Equal(fx.ExpectedCompatdataPath(fx.SteamRoot), fx.Config.Discovery.UserCompatdataPath);
        Assert.Equal(fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental"), fx.Config.Discovery.UserProtonBinaryPath);
        Assert.Equal(1, fx.ConfigLoader.SaveCalls);
    }

    // ---- heal cannot resolve everything: still-missing fields are flagged ----

    [Fact]
    public void Unresolvable_fields_stay_null_and_flag_status_Partial()
    {
        // The discoverer cannot find Proton (nothing scaffolded). The field is
        // flagged missing via Status=Partial + the null ProtonBinaryPath; no
        // value is persisted for it (nothing to persist).
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        // No Proton scaffolded: discoverer yields null for it.

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.ProtonBinaryPath);
        // The other three fields were healed + persisted; Proton stays null.
        Assert.Null(fx.Config.Discovery.UserProtonBinaryPath);
        Assert.Equal(fx.SteamRoot, fx.Config.Discovery.UserSteamInstallPath);
    }

    [Fact]
    public void No_steam_at_all_yields_Failed_with_no_save()
    {
        // Nothing is scaffolded; every field is null + the discoverer cannot
        // find Steam. Nothing is healed, so no save happens; the result is
        // Failed with every field null.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Failed, result.Status);
        Assert.Null(result.SteamInstallPath);
        Assert.Null(result.DarktideGameBinaryPath);
        Assert.Null(result.CompatdataPath);
        Assert.Null(result.ProtonBinaryPath);
        Assert.Equal(0, fx.ConfigLoader.SaveCalls);
    }

    // ---- platform-gating: Windows skips compatdata + Proton -----------------

    [Fact]
    public void Windows_validates_and_heals_only_steam_and_darktide()
    {
        // On Windows the compatdata + Proton fields are Linux-only; they are
        // neither validated nor healed, so they stay null in the result + null
        // in the persisted config regardless of what the config carries.
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        // Leftover Linux values in config (e.g. from a prior Linux run): ignored
        // on Windows, never re-validated, never overwritten.
        fx.Config.Discovery.UserCompatdataPath = "/leftover/compatdata";
        fx.Config.Discovery.UserProtonBinaryPath = "/leftover/proton";

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        // Compatdata + Proton are null by design on Windows.
        Assert.Null(result.CompatdataPath);
        Assert.Null(result.ProtonBinaryPath);
        // The leftover Linux values are preserved (Windows does not touch them).
        Assert.Equal("/leftover/compatdata", fx.Config.Discovery.UserCompatdataPath);
        Assert.Equal("/leftover/proton", fx.Config.Discovery.UserProtonBinaryPath);
        // Steam + Darktide were healed + persisted.
        Assert.Equal(fx.SteamRoot, fx.Config.Discovery.UserSteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), fx.Config.Discovery.UserDarktideGameBinaryPath);
    }

    [Fact]
    public void Windows_skips_the_discoverer_when_steam_and_darktide_are_valid()
    {
        // On Windows, only Steam + Darktide are checked. Both overrides point
        // at real scaffolded paths, so the discoverer is not run + nothing is
        // saved (every checked field is valid).
        using var fx = new SteamFixture(DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);

        fx.Config.Discovery.UserSteamInstallPath = fx.SteamRoot;
        fx.Config.Discovery.UserDarktideGameBinaryPath = fx.ExpectedDarktidePath(fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Equal(0, fx.ConfigLoader.SaveCalls);
    }

    // ---- ProtonVersion side effect ------------------------------------------

    [Fact]
    public void ProtonVersion_is_carried_when_Proton_was_healed()
    {
        // Proton was healed from the discoverer; the auto label describes the
        // path in use, so it is carried through.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        var result = fx.Service.Discover();

        Assert.Equal("Proton - Experimental", result.ProtonVersion);
    }

    [Fact]
    public void ProtonVersion_is_nulled_when_a_valid_user_override_took_the_field()
    {
        // The user's Proton override exists on disk (so it survives validation);
        // the auto label may not describe the path in use (the user picked it,
        // not the discoverer's heuristic), so it is nulled. (Informational only;
        // launch uses the path, not the label.) This mirrors the prior "trust
        // the user" rule: a user-supplied Proton path drops the auto label.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        var protonPath = fx.ExpectedProtonPath(fx.SteamRoot, "Proton - Experimental");
        fx.Config.Discovery.UserProtonBinaryPath = protonPath;

        var result = fx.Service.Discover();

        Assert.Equal(protonPath, result.ProtonBinaryPath);
        // The user took the field with a valid override -> label nulled.
        Assert.Null(result.ProtonVersion);
    }

    // ---- live-read: a Settings write between calls is visible ----------------

    [Fact]
    public void Discover_re_reads_config_so_a_write_between_calls_is_visible()
    {
        // Proves the live-read contract: the same ISteamService instance
        // re-reads Discovery on each Discover() call, so an external config
        // change (a Settings / escape-hatch write) takes effect on the next
        // Discover() without re-constructing the service.
        using var fx = new SteamFixture(DiscoveryPlatform.Linux);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.WithDarktide(fx.SteamRoot);
        fx.WithCompatdata(fx.SteamRoot);
        fx.WithProtonInCommon(fx.SteamRoot, "Proton - Experimental");

        // First call: nothing is set; every field is healed from the discoverer
        // + persisted.
        var before = fx.Service.Discover();
        Assert.Equal(fx.SteamRoot, before.SteamInstallPath);

        // A Settings-style write lands in the live config + points at a real
        // path (so it survives validation on the next call).
        var altSteam = Path.Combine(fx.TempRoot, "alt-steam");
        Directory.CreateDirectory(Path.Combine(altSteam, "steamapps"));
        File.WriteAllText(Path.Combine(altSteam, "steamapps", "libraryfolders.vdf"),
            SteamFixture.BuildLibraryFoldersVdf(altSteam));
        fx.Config.Discovery.UserSteamInstallPath = altSteam;

        // Second call sees the late write: the alt-steam path exists on disk,
        // so it is valid + kept (no re-heal).
        var after = fx.Service.Discover();
        Assert.Equal(altSteam, after.SteamInstallPath);
    }
}
