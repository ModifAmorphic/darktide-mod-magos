using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// The shell's app self-update notice: the dismissible status-strip pill appears
/// only when self-update is supported + a check found an update + the user has
/// not dismissed it this session; the notice-click flow (confirm -> download ->
/// apply); and the <see cref="IAppUpdateService.UpdateStateChanged"/> event
/// marshals safely to the UI thread. All against the recording fakes in
/// <see cref="TestDoubles"/>.
/// </summary>
/// <remarks>
/// <para>The VM's <c>OnAppUpdateStateChanged</c> handler marshals its refresh
/// through an injected <c>Action&lt;Action&gt;</c> seam; tests inject a
/// synchronous <c>action =&gt; action()</c>, so the refresh runs inline the
/// moment the event is raised. No Avalonia dispatcher is touched, so the suite
/// runs in parallel with the rest of the assembly.</para>
/// </remarks>
public sealed class ShellViewModelAppUpdateTests
{
    private static readonly LocalizationService Localization = new();

    private static ShellViewModel Build(
        FakeAppUpdateService? appUpdate = null,
        FakeDialogService? dialogs = null,
        FakeConfigLoader? configLoader = null)
    {
        appUpdate ??= new FakeAppUpdateService();
        dialogs ??= new FakeDialogService();
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        return new ShellViewModel(
            profiles,
            session,
            new FakeLaunchService(),
            dialogs,
            TestDoubles.BuildDmfPromptService(profiles, session),
            Localization,
            TestDoubles.BuildModList(profiles, session),
            appUpdate,
            invokeOnUi: static action => action(),
            NullLogger<ShellViewModel>.Instance,
            configLoader ?? new FakeConfigLoader());
    }

    // ---- ShowAppUpdateNotice gating ---------------------------------------

    [Fact]
    public void Notice_is_hidden_when_self_update_is_unsupported()
    {
        var appUpdate = new FakeAppUpdateService { IsUpdateSupported = false };
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);

        var vm = Build(appUpdate);

        Assert.False(vm.ShowAppUpdateNotice);
    }

    [Fact]
    public void Notice_is_hidden_when_no_check_has_found_an_update()
    {
        var vm = Build();

        Assert.False(vm.ShowAppUpdateNotice);
    }

    [Fact]
    public void Notice_is_shown_when_supported_and_an_update_is_available()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);

        var vm = Build(appUpdate);

        Assert.True(vm.ShowAppUpdateNotice);
        Assert.Contains("2.0.0", vm.AppUpdateNoticeText);
    }

    [Fact]
    public void Dismiss_hides_the_notice_for_the_session_only()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var vm = Build(appUpdate);
        Assert.True(vm.ShowAppUpdateNotice);

        vm.DismissAppUpdateCommand.Execute(null);

        Assert.True(vm.IsAppUpdateDismissed);
        Assert.False(vm.ShowAppUpdateNotice);
    }

    // ---- startup-check toggle gating --------------------------------------

    [Fact]
    public async Task Notice_is_hidden_when_the_startup_check_toggle_is_off_even_with_an_update_available()
    {
        // The toggle (CuratorConfig.AppUpdates.CheckOnStartup) gates the notice:
        // when automatic checks are off, the notice NEVER shows, even when a
        // check has populated LastCheckResult (a manual check can still run, but
        // it stays self-contained in Settings with its own Download-and-Restart
        // button; it does not surface the status-strip pill).
        var configLoader = new FakeConfigLoader();
        configLoader.Config.AppUpdates.CheckOnStartup = false;
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var vm = Build(appUpdate, configLoader: configLoader);

        Assert.False(vm.ShowAppUpdateNotice);

        // Turning the toggle back on (simulating a Settings edit) + closing
        // Settings re-reads the config, so the notice now shows.
        configLoader.Config.AppUpdates.CheckOnStartup = true;
        await vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.True(vm.ShowAppUpdateNotice);
    }

    [Fact]
    public async Task OpenSettings_re_reads_the_toggle_so_turning_it_off_hides_a_showing_notice()
    {
        // The notice shows under an enabled toggle; after the user turns it off
        // in Settings + closes the dialog, the shell re-reads the config and the
        // notice is dismissed immediately (no restart, no dismiss click needed).
        // Settings is the sole place the toggle changes, so the on-close refresh
        // is sufficient + no config-change subscription is required.
        var configLoader = new FakeConfigLoader();
        configLoader.Config.AppUpdates.CheckOnStartup = true;
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var vm = Build(appUpdate, configLoader: configLoader);
        Assert.True(vm.ShowAppUpdateNotice);

        configLoader.Config.AppUpdates.CheckOnStartup = false;
        await vm.OpenSettingsCommand.ExecuteAsync(null);

        Assert.False(vm.ShowAppUpdateNotice);
    }

    // ---- notice-click flow ------------------------------------------------

    [Fact]
    public async Task Notice_click_with_confirm_cancel_dismisses_the_notice_for_the_session_and_does_not_download()
    {
        // Cancel on the confirm dismisses the notice for this session (cancel =
        // "dismiss for now"). The explicit dismiss button also dismisses for the
        // session. No download, no apply.
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var vm = Build(appUpdate, dialogs);

        await vm.CheckAppUpdateNowCommand.ExecuteAsync(null);

        Assert.True(vm.IsAppUpdateDismissed);
        Assert.False(vm.ShowAppUpdateNotice); // dismissed for the session
        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Equal(0, appUpdate.DownloadCallCount);
        Assert.Equal(0, appUpdate.ApplyCallCount);
    }

    [Fact]
    public async Task Notice_click_with_confirm_ok_downloads_under_spinner_then_applies()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(appUpdate, dialogs);

        await vm.CheckAppUpdateNowCommand.ExecuteAsync(null);

        // The confirm message carries the target version.
        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Contains("2.0.0", dialogs.LastConfirmMessage);
        // The download ran under the progress spinner.
        Assert.Single(dialogs.ProgressCalls);
        Assert.Equal(1, appUpdate.DownloadCallCount);
        // A successful download proceeds to apply (which restarts the process).
        Assert.Equal(1, appUpdate.ApplyCallCount);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task Notice_click_download_failure_surfaces_an_alert_and_does_not_apply()
    {
        var appUpdate = new FakeAppUpdateService();
        appUpdate.LastCheckResult = new AppUpdateInfo("2.0.0", Notes: null);
        appUpdate.ThrowOnDownload = new InvalidOperationException("checksum mismatch");
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(appUpdate, dialogs);

        await vm.CheckAppUpdateNowCommand.ExecuteAsync(null);

        Assert.Single(dialogs.AlertCalls);
        Assert.Equal(Localization["AppUpdate_DownloadFailedTitle"], dialogs.AlertCalls[0].Title);
        Assert.Contains("checksum mismatch", dialogs.AlertCalls[0].Message);
        // A failed download must NOT proceed to apply.
        Assert.Equal(0, appUpdate.ApplyCallCount);
    }

    [Fact]
    public async Task Notice_click_with_no_result_is_a_no_op()
    {
        // Defensive: if the check cleared the result between the notice showing
        // and the click, the command does nothing.
        var appUpdate = new FakeAppUpdateService(); // LastCheckResult = null
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(appUpdate, dialogs);

        await vm.CheckAppUpdateNowCommand.ExecuteAsync(null);

        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Equal(0, appUpdate.DownloadCallCount);
    }

    // ---- UpdateStateChanged wiring ----------------------------------------

    [Fact]
    public void UpdateStateChanged_refreshes_the_notice_so_a_newly_found_update_shows()
    {
        // The VM's handler marshals its refresh through the injected seam; the
        // test seam runs inline, so raising the event resolves the notice at
        // once. This verifies the wiring (event -> handler -> refresh) without
        // depending on the production dispatcher.
        var appUpdate = new FakeAppUpdateService();
        var vm = Build(appUpdate);
        Assert.False(vm.ShowAppUpdateNotice); // no result yet

        appUpdate.LastCheckResult = new AppUpdateInfo("3.0.0", Notes: null);
        appUpdate.RaiseUpdateStateChanged();

        Assert.True(vm.ShowAppUpdateNotice);
        Assert.Contains("3.0.0", vm.AppUpdateNoticeText);
    }
}

