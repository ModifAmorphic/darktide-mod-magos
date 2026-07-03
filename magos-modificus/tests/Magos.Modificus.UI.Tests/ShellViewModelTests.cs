using Magos.Modificus.Profiles;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Shell-VM profile controls: mirroring the session's active id + running-state,
/// dropdown switch (routed through the session gate), switch-blocked-while-running,
/// and dialog-driven list refresh. All against in-memory fakes; the session is
/// behind the <see cref="UI.Session.IProfileSession"/> seam (a <see cref="FakeProfileSession"/>).
/// </summary>
public sealed class ShellViewModelTests
{
    private static readonly ILogger<ShellViewModel> Logger = NullLogger<ShellViewModel>.Instance;
    private static readonly LocalizationService Localization = new();

    private static ShellViewModel Build(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeDialogService? dialogs = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        return new ShellViewModel(
            profiles,
            session,
            new FakeLaunchService(),
            dialogs ?? new FakeDialogService(),
            Localization,
            Logger);
    }

    // ---- active-profile restore on construction ----------------------------

    [Fact]
    public void Constructor_restores_the_persisted_active_profile()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = b.Id };

        var vm = Build(profiles, session);

        Assert.NotNull(vm.SelectedProfile);
        Assert.Equal(b.Id, vm.SelectedProfile!.Id);
    }

    [Fact]
    public void Constructor_leaves_selection_null_when_no_active_profile_recorded()
    {
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var session = new FakeProfileSession { ActiveProfileId = null };

        var vm = Build(profiles, session);

        Assert.Null(vm.SelectedProfile);
    }

    [Fact]
    public void Constructor_does_not_request_an_active_change_during_restore()
    {
        // Restoring the saved active reads it from the session; it never asks the
        // session to change active (no voluntary gate invocation on startup).
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };

        var vm = Build(profiles, session);

        Assert.Equal(0, session.RequestActiveCalls);
    }

    [Fact]
    public void Constructor_falls_back_to_null_when_the_recorded_active_profile_is_absent()
    {
        // Stale state pointing at a deleted profile resolves to no selection.
        var profiles = TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha"));
        var session = new FakeProfileSession { ActiveProfileId = Guid.NewGuid() };

        var vm = Build(profiles, session);

        Assert.Null(vm.SelectedProfile);
    }

    // ---- dropdown switch routes through the session ------------------------

    [Fact]
    public void Setting_SelectedProfile_requests_the_id_active_through_the_session()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var vm = Build(profiles, session);

        vm.SelectedProfile = b;

        Assert.Equal(b.Id, session.ActiveProfileId); // session applied it
        Assert.Equal(1, session.RequestActiveCalls);
        Assert.Equal(b.Id, vm.SelectedProfile?.Id);  // selection follows the session
    }

    [Fact]
    public void Setting_SelectedProfile_reverts_when_the_session_gate_rejects()
    {
        // The session is the authority: when it blocks the change (game running),
        // the dropdown snaps back to the real active instead of lying.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true };
        var vm = Build(profiles, session);

        vm.SelectedProfile = b; // programmatically (the dropdown is disabled while running)

        Assert.Equal(a.Id, vm.SelectedProfile?.Id); // reverted to the real active
        Assert.Equal(a.Id, session.ActiveProfileId); // session never moved
        Assert.Equal(1, session.RequestActiveCalls);  // but it was asked
    }

    // ---- switch-blocked-while-running --------------------------------------

    [Fact]
    public void CanSwitchProfile_is_true_when_not_running_and_profiles_exist()
    {
        var vm = Build(TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha")),
            new FakeProfileSession { IsRunning = false });

        Assert.True(vm.CanSwitchProfile);
    }

    [Fact]
    public void CanSwitchProfile_is_false_when_the_game_is_running()
    {
        var session = new FakeProfileSession { IsRunning = true };
        var vm = Build(TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha")), session);

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
    public void Live_IsRunning_change_flips_CanSwitchProfile_and_the_tooltip()
    {
        // The status strip + dropdown-enable react to the session's live running-state
        // (the polling timer drives this in production).
        var session = new FakeProfileSession { IsRunning = false };
        var vm = Build(TestDoubles.Profiles(new ProfileSummary(Guid.NewGuid(), "Alpha")), session);
        Assert.True(vm.CanSwitchProfile);

        session.IsRunning = true; // the timer flipped it

        Assert.False(vm.CanSwitchProfile);
        Assert.Contains("Darktide is running", vm.ProfileSwitchTooltip);
    }

    // ---- manage dialog coordination ----------------------------------------

    [Fact]
    public async Task ManageProfiles_opens_the_dialog_once()
    {
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService();
        var vm = Build(profiles, session, dialogs);

        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ManageProfilesCalls);
    }

    [Fact]
    public async Task ManageProfiles_refreshes_the_profile_list_after_close()
    {
        // The dialog (simulated) creates a profile during its session; the shell
        // reloads its list snapshot when the dialog closes.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService
        {
            OnManageProfiles = () => profiles.CreateProfile("Bravo"),
        };
        var vm = Build(profiles, session, dialogs);

        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Contains(vm.Profiles, p => p.Name == "Bravo");
    }

    [Fact]
    public async Task ManageProfiles_re_syncs_selection_to_the_session_active_after_close()
    {
        // The dialog applies active changes live through the session; on close the
        // shell follows the session's authoritative active id.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var b = new ProfileSummary(Guid.NewGuid(), "Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var dialogs = new FakeDialogService
        {
            OnManageProfiles = () => session.RequestActive(b.Id),
        };
        var vm = Build(profiles, session, dialogs);

        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Equal(b.Id, session.ActiveProfileId);
        Assert.Equal(b.Id, vm.SelectedProfile?.Id);
    }

    [Fact]
    public async Task ManageProfiles_deleting_the_active_clears_selection_and_blocks_launch()
    {
        // Belt-and-suspenders: delete-of-active (not running) clears the active id;
        // the shell's selection mirrors it to null, so CanLaunch (unchanged) keeps
        // Launch blocked because no profile is selected.
        var a = new ProfileSummary(Guid.NewGuid(), "Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = a.Id,
            IsRunning = false,
        };
        var dialogs = new FakeDialogService
        {
            ConfirmResult = true,
            OnManageProfiles = () =>
            {
                // Simulate the dialog deleting the active profile: profile gone,
                // session reconciles (clears the active id, per the fix).
                profiles.DeleteProfile(a.Id);
                session.ReconcileActive();
            },
        };
        var vm = Build(profiles, session, dialogs);
        Assert.NotNull(vm.SelectedProfile);

        await vm.ManageProfilesCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedProfile);                  // active cleared
        Assert.False(vm.LaunchCommand.CanExecute(null)); // Launch blocked (no selection)
    }
}
