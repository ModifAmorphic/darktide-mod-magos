using Magos.Modificus.Config;
using Magos.Modificus.Mods;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.Settings;
using Magos.Modificus.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.UI.Tests;

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
        var config = MagosConfig.CreateDefault();
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

        Assert.Equal("/steam", Row(vm, "SteamInstallPath").Value);
        Assert.Equal("/darktide.exe", Row(vm, "DarktideGameBinaryPath").Value);
        Assert.Equal("/compat", Row(vm, "CompatdataPath").Value);
        Assert.Equal("/proton", Row(vm, "ProtonBinaryPath").Value);
    }

    [Fact]
    public void Discovery_rows_are_empty_when_overrides_are_unset()
    {
        // All-null discovery (the default): every row's TextBox starts empty.
        var (vm, _, _) = Build();

        Assert.Equal(string.Empty, Row(vm, "SteamInstallPath").Value);
        Assert.Equal(string.Empty, Row(vm, "DarktideGameBinaryPath").Value);
        Assert.Equal(string.Empty, Row(vm, "CompatdataPath").Value);
        Assert.Equal(string.Empty, Row(vm, "ProtonBinaryPath").Value);
    }

    [Fact]
    public void Discovery_rows_cover_all_four_fields_in_catalog_order()
    {
        var (vm, _, _) = Build();

        Assert.Equal(4, vm.DiscoveryRows.Count);
        Assert.Equal("SteamInstallPath", vm.DiscoveryRows[0].Field.FieldName);
        Assert.Equal("DarktideGameBinaryPath", vm.DiscoveryRows[1].Field.FieldName);
        Assert.Equal("CompatdataPath", vm.DiscoveryRows[2].Field.FieldName);
        Assert.Equal("ProtonBinaryPath", vm.DiscoveryRows[3].Field.FieldName);
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
        Row(vm, "CompatdataPath").Value = "/compat";

        Assert.Equal(2, loader.SaveCalls);
        Assert.Equal("/darktide.exe", loader.LastSaved!.Discovery.UserDarktideGameBinaryPath);
        Assert.Equal("/compat", loader.LastSaved.Discovery.UserCompatdataPath);
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
            Config = new MagosConfig { ModsFolder = "/old/mods" },
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
