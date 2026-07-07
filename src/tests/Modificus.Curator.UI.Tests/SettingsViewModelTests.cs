using Modificus.Curator.Config;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Settings;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Tests for <see cref="SettingsViewModel"/>: discovery fields are pre-filled
/// from config + written back via read-modify-save, and a ModsFolder change
/// triggers the atomic <see cref="IModRepository.Relocate"/> (a single call
/// that owns the move + config save + rescan).
/// </summary>
public sealed class SettingsViewModelTests
{
    private static readonly ILogger<SettingsViewModel> Logger = NullLogger<SettingsViewModel>.Instance;
    private static readonly LocalizationService Localization = new();

    /// <summary>Builds a VM wired to the supplied (or default) fakes.</summary>
    private static (SettingsViewModel vm, FakeConfigLoader loader, FakeModRepository repo) Build(
        DiscoveryConfig? discovery = null,
        string? modsFolder = null,
        FakeModRepository? repo = null)
    {
        var config = CuratorConfig.CreateDefault();
        if (discovery is not null) config.Discovery = discovery;
        if (modsFolder is not null) config.ModsFolder = modsFolder;
        var loader = new FakeConfigLoader { Config = config };
        repo ??= new FakeModRepository();
        var vm = new SettingsViewModel(loader, repo, Localization, Logger);
        return (vm, loader, repo);
    }

    private static DiscoveryFieldRowViewModel Row(SettingsViewModel vm, string fieldName) =>
        vm.DiscoveryRows.First(r => r.Field.FieldName == fieldName);

    // ---- pre-fill from config --------------------------------------------

    [Fact]
    public void Discovery_rows_are_pre_filled_from_config()
    {
        var discovery = new DiscoveryConfig
        {
            UserSteamInstallPath = "/steam",
            UserDarktideGameBinaryPath = "/darktide.exe",
            UserCompatdataPath = "/compat",
            UserProtonBinaryPath = "/proton",
        };

        var (vm, _, _) = Build(discovery);

        // Steam + Darktide rows always render.
        Assert.Equal("/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal("/darktide.exe", Row(vm, "DarktideGameBinaryPath").Value);
        // Compatdata + Proton rows render on Linux only (platform-gated).
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/compat", Row(vm, "CompatdataPath").Value);
            Assert.Equal("/proton", Row(vm, "ProtonBinaryPath").Value);
        }
    }

    [Fact]
    public void Discovery_rows_are_empty_when_overrides_are_unset()
    {
        // All-null discovery (the default): every rendered row's TextBox starts
        // empty.
        var (vm, _, _) = Build();

        Assert.Equal(string.Empty, Row(vm, "SteamInstallPath").Value);
        Assert.Equal(string.Empty, Row(vm, "DarktideGameBinaryPath").Value);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(string.Empty, Row(vm, "CompatdataPath").Value);
            Assert.Equal(string.Empty, Row(vm, "ProtonBinaryPath").Value);
        }
    }

    [Fact]
    public void Discovery_rows_are_pre_filled_after_startup_Discover_populates_config()
    {
        // End-to-end (no real Steam layout, just the persistence contract): the
        // startup Discover populates the persisted overrides (simulating the
        // composition root's startup call writing the healed values), and the
        // Settings VM, which reads the live config, shows them rather than
        // blanks. This is the "Settings shows non-blank fields" guarantee from
        // the validate + heal + persist pipeline (Track C review fix).
        var loader = new FakeConfigLoader { Config = CuratorConfig.CreateDefault() };

        // Simulate the startup Discover having healed every field + persisted it
        // (a single Save carrying the four writes). The persisted values are
        // what the Settings VM should show.
        var healed = new DiscoveryConfig
        {
            UserSteamInstallPath = "/resolved/steam",
            UserDarktideGameBinaryPath = "/resolved/darktide.exe",
            UserCompatdataPath = "/resolved/compatdata",
            UserProtonBinaryPath = "/resolved/proton",
        };
        var persisted = CuratorConfig.CreateDefault();
        persisted.Discovery = healed;
        loader.Save(persisted);

        var vm = new SettingsViewModel(loader, new FakeModRepository(), Localization, Logger);

        Assert.Equal("/resolved/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal("/resolved/darktide.exe", Row(vm, "DarktideGameBinaryPath").Value);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/resolved/compatdata", Row(vm, "CompatdataPath").Value);
            Assert.Equal("/resolved/proton", Row(vm, "ProtonBinaryPath").Value);
        }
    }

    [Fact]
    public void Discovery_rows_match_the_platforms_expected_fields_in_catalog_order()
    {
        // Platform-gated: Windows renders only the Steam install + Darktide
        // binary rows (the compatdata + Proton overrides are Linux-only:
        // WindowsLaunchStrategy ignores them, so they would be silently
        // ineffective rows). Linux renders all four, in catalog order.
        var (vm, _, _) = Build();

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(4, vm.DiscoveryRows.Count);
            Assert.Equal("SteamInstallPath", vm.DiscoveryRows[0].Field.FieldName);
            Assert.Equal("DarktideGameBinaryPath", vm.DiscoveryRows[1].Field.FieldName);
            Assert.Equal("CompatdataPath", vm.DiscoveryRows[2].Field.FieldName);
            Assert.Equal("ProtonBinaryPath", vm.DiscoveryRows[3].Field.FieldName);
        }
        else
        {
            Assert.Equal(2, vm.DiscoveryRows.Count);
            Assert.Equal("SteamInstallPath", vm.DiscoveryRows[0].Field.FieldName);
            Assert.Equal("DarktideGameBinaryPath", vm.DiscoveryRows[1].Field.FieldName);
        }
    }

    [Fact]
    public void ModsFolder_is_pre_filled_from_config()
    {
        var (vm, _, _) = Build(modsFolder: "/my/mods");

        Assert.Equal("/my/mods", vm.ModsFolder);
    }

    // ---- write-through (discovery fields) --------------------------------

    [Fact]
    public void Editing_a_discovery_field_writes_the_override_via_read_modify_save()
    {
        var (vm, loader, _) = Build();

        Row(vm, "SteamInstallPath").Value = "/new/steam";

        Assert.Equal(1, loader.SaveCalls);
        Assert.Equal("/new/steam", loader.LastSaved!.Discovery.UserSteamInstallPath);
    }

    [Fact]
    public void Clearing_a_discovery_field_writes_null_so_it_falls_back_to_auto()
    {
        var discovery = new DiscoveryConfig { UserSteamInstallPath = "/old" };
        var (vm, loader, _) = Build(discovery);

        Row(vm, "SteamInstallPath").Value = "";

        Assert.Equal(1, loader.SaveCalls);
        Assert.Null(loader.LastSaved!.Discovery.UserSteamInstallPath);
    }

    [Fact]
    public void Initial_restore_does_not_save_config()
    {
        // Pre-filling the rows must NOT trigger a write (each value already
        // matches what is persisted; re-writing would be a noisy no-op).
        var (_, loader, _) = Build(new DiscoveryConfig { UserSteamInstallPath = "/x" });

        Assert.Equal(0, loader.SaveCalls);
    }

    [Fact]
    public void Editing_each_field_persists_progressively_via_read_modify_save()
    {
        // Each edit is its own read-modify-save: the second save picks up the
        // first edit's persisted value (FakeConfigLoader mirrors the real
        // loader's round-trip), so both overrides land.
        var (vm, loader, _) = Build();

        Row(vm, "DarktideGameBinaryPath").Value = "/darktide.exe";
        Row(vm, "SteamInstallPath").Value = "/new/steam";

        Assert.Equal(2, loader.SaveCalls);
        Assert.Equal("/darktide.exe", loader.LastSaved!.Discovery.UserDarktideGameBinaryPath);
        Assert.Equal("/new/steam", loader.LastSaved.Discovery.UserSteamInstallPath);
    }

    // ---- ModsFolder relocate flow ----------------------------------------

    [Fact]
    public void ModsFolder_change_runs_the_atomic_relocate_in_a_single_call()
    {
        // Atomic contract: the Settings VM makes a single Relocate call; the
        // repo owns the move + config save + rescan. The VM does NOT save the
        // config or rescan separately (those are the repo's responsibility now).
        var (vm, loader, repo) = Build(modsFolder: "/old/mods");

        vm.ApplyModsFolderCommand.Execute("/new/mods");

        // Relocate happened exactly once with the new path.
        Assert.Equal(new[] { "/new/mods" }, repo.RelocateArgs);
        // The VM did not save the config or rescan itself (Relocate owns both).
        Assert.Equal(0, loader.SaveCalls);
        Assert.Equal(0, repo.RescanCalls);
        // The TextBox was updated to the new path.
        Assert.Equal("/new/mods", vm.ModsFolder);
        // No status message on success.
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public void ModsFolder_change_to_the_same_path_is_a_no_op()
    {
        var (vm, loader, repo) = Build(modsFolder: "/same/mods");

        vm.ApplyModsFolderCommand.Execute("/same/mods");

        Assert.Empty(repo.RelocateArgs);
        Assert.Equal(0, loader.SaveCalls);
        Assert.Equal(0, repo.RescanCalls);
    }

    [Fact]
    public void ModsFolder_relocate_failure_surfaces_status_message_and_keeps_old_path()
    {
        // Configure the repo to throw on Relocate (invalid path / conflict).
        var repo = new ThrowingRelocateRepo();
        var loader = new FakeConfigLoader
        {
            Config = new CuratorConfig { ModsFolder = "/old/mods" },
        };
        var vm = new SettingsViewModel(loader, repo, Localization, Logger);

        vm.ApplyModsFolderCommand.Execute("/bad/path");

        Assert.NotNull(vm.StatusMessage);
        Assert.NotEmpty(vm.StatusMessage);
        Assert.Equal("/old/mods", vm.ModsFolder); // unchanged
        Assert.Equal(0, repo.RescanCalls);       // Rescan did not run
    }

    /// <summary>
    /// A fake repo whose <see cref="Relocate"/> throws (simulating an invalid
    /// path or conflicting UUID, which the real repo surfaces as
    /// <see cref="ArgumentException"/> / <see cref="InvalidOperationException"/>).
    /// </summary>
    private sealed class ThrowingRelocateRepo : FakeModRepository
    {
        public override void Relocate(string newBasePath) =>
            throw new InvalidOperationException("simulated conflict");
    }
}
