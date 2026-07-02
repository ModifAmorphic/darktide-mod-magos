using Magos.Modificus.General;
using Magos.Modificus.Profiles;
using Magos.Modificus.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Shell-VM profile controls: active-profile restore on construction, dropdown
/// switch + persistence, switch-blocked-while-running, and dialog-driven
/// refresh. All against in-memory fakes — no Avalonia window is needed because
/// the dialog is behind the <see cref="IDialogService"/> seam.
/// </summary>
public sealed class ShellViewModelTests
{
    private static readonly ILogger<ShellViewModel> Logger = NullLogger<ShellViewModel>.Instance;

    private static ShellViewModel Build(
        FakeProfileService? profiles = null,
        FakeAppStateStore? appState = null,
        FakeDialogService? dialogs = null,
        FakeSteamService? steam = null)
    {
        profiles ??= TestDoubles.Profiles();
        return new ShellViewModel(
            profiles,
            steam ?? new FakeSteamService(),
            new FakeLaunchService(),
            appState ?? new FakeAppStateStore(),
            dialogs ?? new FakeDialogService(),
            Logger);
    }

    // ---- active-profile restore on construction ----------------------------

    [Fact]
    public void Constructor_restores_the_persisted_active_profile()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var appState = new FakeAppStateStore { ActiveProfileId = b.Id };

        var vm = Build(profiles, appState);

        Assert.NotNull(vm.SelectedProfile);
        Assert.Equal(b.Id, vm.SelectedProfile!.Id);
    }

    [Fact]
    public void Constructor_leaves_selection_null_when_no_active_profile_recorded()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var appState = new FakeAppStateStore { ActiveProfileId = null };

        var vm = Build(profiles, appState);

        Assert.Null(vm.SelectedProfile);
    }

    [Fact]
    public void Constructor_does_not_persist_when_restoring_the_active_profile()
    {
        // The suppress guard must keep the initial restore from writing back.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var appState = new FakeAppStateStore { ActiveProfileId = null };

        var vm = Build(profiles, appState);

        Assert.Equal(0, appState.SetCount);
    }

    [Fact]
    public void Constructor_falls_back_to_null_when_the_recorded_active_profile_is_absent()
    {
        // Stale state pointing at a deleted profile — don't select garbage.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var appState = new FakeAppStateStore { ActiveProfileId = Guid.NewGuid() };

        var vm = Build(profiles, appState);

        Assert.Null(vm.SelectedProfile);
    }

    // ---- dropdown switch + persistence -------------------------------------

    [Fact]
    public void Setting_SelectedProfile_persists_the_active_id()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var appState = new FakeAppStateStore();
        var vm = Build(profiles, appState);

        vm.SelectedProfile = b;

        Assert.Equal(b.Id, appState.ActiveProfileId);
        Assert.Equal(1, appState.SetCount);
    }

    [Fact]
    public void Clearing_SelectedProfile_persists_null()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var vm = Build(profiles, appState);

        vm.SelectedProfile = null;

        Assert.Null(appState.ActiveProfileId);
    }

    // ---- switch-blocked-while-running --------------------------------------

    [Fact]
    public void CanSwitchProfile_is_true_when_not_running_and_profiles_exist()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var steam = new FakeSteamService { Running = false };

        var vm = Build(profiles, steam: steam);

        Assert.True(vm.CanSwitchProfile);
    }

    [Fact]
    public void CanSwitchProfile_is_false_when_the_game_is_running()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var steam = new FakeSteamService { Running = true };

        var vm = Build(profiles, steam: steam);

        Assert.False(vm.CanSwitchProfile);
        Assert.Contains("Darktide is running", vm.ProfileSwitchTooltip);
    }

    [Fact]
    public void CanSwitchProfile_is_false_when_no_profiles_exist()
    {
        var vm = Build(TestDoubles.Profiles());

        Assert.False(vm.CanSwitchProfile);
        Assert.NotEmpty(vm.ProfileSwitchTooltip);
    }

    [Fact]
    public void IsGameRunning_changes_flip_CanSwitchProfile_and_the_tooltip()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var steam = new FakeSteamService { Running = false };
        var vm = Build(profiles, steam: steam);
        Assert.True(vm.CanSwitchProfile);

        vm.IsGameRunning = true;

        Assert.False(vm.CanSwitchProfile);
        Assert.Contains("Darktide is running", vm.ProfileSwitchTooltip);
    }

    // ---- manage dialog coordination ----------------------------------------

    [Fact]
    public async Task ManageProfiles_opens_the_dialog_with_the_current_active_id()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService();
        var vm = Build(profiles, appState, dialogs);

        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ManageProfilesCalls);
        Assert.Equal(a.Id, dialogs.LastCurrentActiveId);
    }

    [Fact]
    public async Task ManageProfiles_applies_the_dialog_reported_active_id()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        // Simulate the dialog creating a new profile that should become active.
        var dialogs = new FakeDialogService { ManageProfilesResult = b.Id };

        var vm = Build(profiles, appState, dialogs);
        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Equal(b.Id, vm.SelectedProfile?.Id);
        Assert.Equal(b.Id, appState.ActiveProfileId);
    }

    [Fact]
    public async Task ManageProfiles_keeps_current_selection_when_dialog_reports_null()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var appState = new FakeAppStateStore { ActiveProfileId = a.Id };
        // Dialog did nothing (rename only / no-op) → null → keep current.
        var dialogs = new FakeDialogService { ManageProfilesResult = null };

        var vm = Build(profiles, appState, dialogs);
        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Equal(a.Id, vm.SelectedProfile?.Id);
    }
}
