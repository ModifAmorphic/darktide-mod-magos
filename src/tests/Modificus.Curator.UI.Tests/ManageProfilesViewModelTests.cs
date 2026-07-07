using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Manage-profiles dialog VM, the editable-list mechanics: inline rename
/// (commit / cancel / empty-rejected / no-op-same-name), delete-confirm
/// (yes deletes / no aborts / delete-of-active clears the active id), the delete
/// gate (active row's trash disabled while Darktide runs via
/// <see cref="UI.Session.IProfileSession.CanDeleteProfile"/>), "+ New profile" add
/// row (create requests active through the session / empty cancels), and the active
/// marker reading the session's authoritative active id. Delete confirmation is
/// exercised through the <see cref="IDialogService"/> seam; the active state is
/// exercised through the <see cref="UI.Session.IProfileSession"/> seam.
/// </summary>
public sealed class ManageProfilesViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static ManageProfilesViewModel Build(
        FakeProfileService profiles,
        FakeDialogService? dialogs = null,
        FakeProfileSession? session = null)
    {
        dialogs ??= new FakeDialogService();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        return new ManageProfilesViewModel(profiles, dialogs, session, Localization);
    }

    private static ProfileSummary Profile(string name) =>
        new(Guid.NewGuid(), name);

    private static ProfileItemViewModel Row(ManageProfilesViewModel vm, string name) =>
        vm.Items.Single(i => i.Name == name);

    // ---- construction / seeding --------------------------------------------

    [Fact]
    public void Construction_loads_the_rows_and_marks_the_active_profile()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var session = new FakeProfileSession { ActiveProfileId = b.Id };
        var vm = Build(TestDoubles.Profiles(a, b), session: session);

        Assert.Equal(2, vm.Items.Count);
        Assert.True(Row(vm, "Bravo").IsActive);
        Assert.False(Row(vm, "Alpha").IsActive);
    }

    [Fact]
    public void Construction_marks_no_row_active_when_the_active_id_is_unknown()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = Guid.NewGuid() };
        var vm = Build(TestDoubles.Profiles(a), session: session);

        Assert.False(Row(vm, "Alpha").IsActive);
        Assert.Single(vm.Items);
    }

    [Fact]
    public void Construction_marks_no_row_active_when_none_is_active()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = null };
        var vm = Build(TestDoubles.Profiles(a), session: session);

        Assert.False(Row(vm, "Alpha").IsActive);
    }

    // ---- rename ------------------------------------------------------------

    [Fact]
    public void StartRename_prefills_the_edit_field_and_enters_edit_mode()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(TestDoubles.Profiles(a), session: session);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);

        Assert.True(row.IsEditing);
        Assert.Equal("Alpha", row.EditText);
    }

    [Fact]
    public void StartRename_cancels_any_other_row_in_flight()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(TestDoubles.Profiles(a, b), session: session);
        var rowA = Row(vm, "Alpha");
        var rowB = Row(vm, "Bravo");

        vm.StartRenameCommand.Execute(rowA);
        vm.StartRenameCommand.Execute(rowB);

        Assert.False(rowA.IsEditing);
        Assert.True(rowB.IsEditing);
    }

    [Fact]
    public void CommitRename_renames_when_the_entry_is_non_empty_and_changed()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session: session);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        row.EditText = "Alpha Renamed";
        vm.CommitRenameCommand.Execute(row); // Enter

        Assert.Equal((a.Id, "Alpha Renamed"), Assert.Single(profiles.Renames));
        Assert.False(row.IsEditing);
        Assert.Equal("Alpha Renamed", row.Name);
    }

    [Fact]
    public void CommitRename_does_not_request_or_reconcile_active()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(TestDoubles.Profiles(a), session: session);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        row.EditText = "Beta";
        vm.CommitRenameCommand.Execute(row);

        Assert.Equal(0, session.RequestActiveCalls);
        Assert.Equal(0, session.ReconcileCalls);
    }

    [Fact]
    public void CommitRename_rejects_an_empty_entry_and_reverts()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session: session);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        row.EditText = "   ";
        vm.CommitRenameCommand.Execute(row);

        Assert.Empty(profiles.Renames);
        Assert.False(row.IsEditing);
        Assert.Equal("Alpha", row.Name); // reverted
    }

    [Fact]
    public void CommitRename_is_a_noop_when_the_entry_equals_the_current_name()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session: session);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        // EditText pre-filled with "Alpha", commit should not call RenameProfile.
        vm.CommitRenameCommand.Execute(row);

        Assert.Empty(profiles.Renames);
        Assert.False(row.IsEditing);
    }

    [Fact]
    public void CommitRename_is_idempotent_after_exit_so_blur_does_not_double_rename()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session: session);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        row.EditText = "Bravo";
        vm.CommitRenameCommand.Execute(row); // Enter
        vm.CommitRenameCommand.Execute(row); // LostFocus (no-op)

        Assert.Single(profiles.Renames);
    }

    [Fact]
    public void CancelRename_discards_the_entry_and_exits_edit_mode()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session: session);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        row.EditText = "Discarded";
        vm.CancelRenameCommand.Execute(row); // Esc

        Assert.Empty(profiles.Renames);
        Assert.False(row.IsEditing);
        Assert.Equal("Alpha", row.Name); // unchanged
    }

    // ---- delete ------------------------------------------------------------

    [Fact]
    public async Task DeleteProfile_prompts_for_confirmation_with_the_name()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService();
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, dialogs, session);
        var row = Row(vm, "Alpha");

        await vm.DeleteProfileCommand.ExecuteAsync(row);

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Contains("Alpha", dialogs.LastConfirmMessage);
        Assert.Contains("mod list", dialogs.LastConfirmMessage!);
    }

    [Fact]
    public async Task DeleteProfile_deletes_when_confirmed()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, dialogs, session);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Alpha"));

        Assert.Equal(a.Id, Assert.Single(profiles.DeletedIds));
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task DeleteProfile_aborts_when_not_confirmed()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, dialogs, session);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Alpha"));

        Assert.Empty(profiles.DeletedIds);
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task DeleteProfile_of_the_active_clears_the_active_id()
    {
        // Delete-of-active is blocked while the game runs (trash disabled), so this
        // path runs when stopped. The active clears (null), not a switch to Bravo.
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, dialogs, session);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Alpha"));

        Assert.Equal(a.Id, Assert.Single(profiles.DeletedIds));
        Assert.Equal(1, session.ReconcileCalls);
        Assert.Null(session.ActiveProfileId); // cleared, not switched to Bravo
        Assert.False(Row(vm, "Bravo").IsActive); // no row marked active
    }

    [Fact]
    public async Task DeleteProfile_of_the_last_active_clears_to_null()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, dialogs, session);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Alpha"));

        Assert.Null(session.ActiveProfileId);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task DeleteProfile_of_a_non_active_profile_leaves_the_active_unchanged()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, dialogs, session);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Bravo"));

        Assert.Equal(b.Id, Assert.Single(profiles.DeletedIds));
        Assert.Equal(a.Id, session.ActiveProfileId); // active untouched
        Assert.True(Row(vm, "Alpha").IsActive);
    }

    // ---- delete gate: active locked while running -------------------------

    [Fact]
    public void Delete_of_the_active_profile_is_blocked_while_the_game_runs()
    {
        // The trash on the active row is disabled (session.CanDeleteProfile false),
        // so the user cannot delete it. Non-active rows stay deletable.
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true };
        var vm = Build(profiles, session: session);

        Assert.False(session.CanDeleteProfile(a.Id));    // the gate behind the binding
        Assert.False(Row(vm, "Alpha").IsDeleteEnabled); // active row's trash disabled
        Assert.True(Row(vm, "Bravo").IsDeleteEnabled);  // non-active row's trash enabled
        Assert.Empty(profiles.DeletedIds);              // nothing deleted
        Assert.Equal(a.Id, session.ActiveProfileId);    // active unchanged
    }

    [Fact]
    public async Task DeleteProfile_command_on_the_active_profile_while_running_is_a_noop()
    {
        // Defense-in-depth: the trash button binds its enabled state to the session's
        // gate, but the command is the real authority. Invoking the command directly
        // on the active row while the game runs (bypassing the UI binding) must still
        // no-op: no confirm prompt, no delete, no reconcile, active + list unchanged.
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = a.Id,
            IsRunning = true,
        };
        var vm = Build(profiles, dialogs, session);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Alpha"));

        Assert.Empty(profiles.DeletedIds);           // not deleted
        Assert.Equal(0, dialogs.ConfirmCalls);        // no confirm prompt (early bail)
        Assert.Equal(0, session.ReconcileCalls);      // no reconcile (early bail)
        Assert.Equal(a.Id, session.ActiveProfileId);  // active unchanged
        Assert.Equal(2, vm.Items.Count);              // list unchanged
    }

    [Fact]
    public void Trash_on_the_active_row_is_enabled_when_the_game_is_not_running()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var vm = Build(TestDoubles.Profiles(a), session: session);

        Assert.True(Row(vm, "Alpha").IsDeleteEnabled);
    }

    [Fact]
    public void Trash_on_the_active_row_flips_with_the_live_running_state()
    {
        // The dialog subscribes to the session's live running-state: the trash
        // disables when Darktide starts and re-enables when it stops (the polling
        // timer drives IsRunning in production).
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var vm = Build(TestDoubles.Profiles(a), session: session);
        Assert.True(Row(vm, "Alpha").IsDeleteEnabled);

        session.IsRunning = true;
        Assert.False(Row(vm, "Alpha").IsDeleteEnabled);

        session.IsRunning = false;
        Assert.True(Row(vm, "Alpha").IsDeleteEnabled);
    }

    [Fact]
    public async Task DeleteProfile_of_a_non_active_profile_while_running_deletes_and_leaves_active_unchanged()
    {
        // Non-active profiles stay deletable anytime; deleting one never touches the active.
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = a.Id,
            IsRunning = true,
        };
        var vm = Build(profiles, dialogs, session);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Bravo"));

        Assert.Equal(b.Id, Assert.Single(profiles.DeletedIds));
        Assert.Equal(a.Id, session.ActiveProfileId); // active untouched
    }

    // ---- create ("+ New profile") -----------------------------------------

    [Fact]
    public void StartCreate_shows_the_add_row_and_clears_the_entry()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(TestDoubles.Profiles(a), session: session);
        vm.NewProfileName = "stale";

        vm.StartCreateCommand.Execute(null);

        Assert.True(vm.IsAddingNew);
        Assert.Empty(vm.NewProfileName);
    }

    [Fact]
    public void StartCreate_cancels_any_inline_rename_in_flight()
    {
        var a = Profile("Alpha");
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(TestDoubles.Profiles(a), session: session);
        var row = Row(vm, "Alpha");
        vm.StartRenameCommand.Execute(row);

        vm.StartCreateCommand.Execute(null);

        Assert.False(row.IsEditing);
    }

    [Fact]
    public void CommitCreate_adds_the_profile_and_requests_it_active_when_not_running()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = false };
        var vm = Build(profiles, session: session);
        vm.StartCreateCommand.Execute(null);
        vm.NewProfileName = "Bravo";

        vm.CommitCreateCommand.Execute(null); // Enter

        Assert.Contains("Bravo", profiles.CreatedNames);
        Assert.False(vm.IsAddingNew);
        Assert.Empty(vm.NewProfileName);
        Assert.Equal(1, session.RequestActiveCalls); // the dialog asked the session
        Assert.Equal(Row(vm, "Bravo").Id, session.ActiveProfileId); // session applied it
        Assert.True(Row(vm, "Bravo").IsActive);  // marker follows the session
        Assert.False(Row(vm, "Alpha").IsActive);
    }

    [Fact]
    public void CommitCreate_while_running_creates_the_profile_but_the_gate_blocks_active()
    {
        // Create while the game runs: the profile is created (it appears in the
        // list), but the session's gate blocks making it active. The marker reads
        // the session, so it stays on the current active. The dialog did not
        // decide; it asked the session, which is the sole gate.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id, IsRunning = true };
        var vm = Build(profiles, session: session);
        vm.StartCreateCommand.Execute(null);
        vm.NewProfileName = "Bravo";

        vm.CommitCreateCommand.Execute(null); // Enter

        Assert.Contains("Bravo", profiles.CreatedNames); // created
        Assert.Equal(1, session.RequestActiveCalls);      // the dialog asked
        Assert.Equal(a.Id, session.ActiveProfileId);       // but the gate held
        Assert.True(Row(vm, "Alpha").IsActive);            // marker held
        Assert.False(Row(vm, "Bravo").IsActive);
    }

    [Fact]
    public void CommitCreate_rejects_an_empty_entry_and_cancels()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session: session);
        vm.StartCreateCommand.Execute(null);
        vm.NewProfileName = "   ";

        vm.CommitCreateCommand.Execute(null);

        Assert.Empty(profiles.CreatedNames);
        Assert.False(vm.IsAddingNew);
        Assert.Empty(vm.NewProfileName);
        Assert.Equal(0, session.RequestActiveCalls); // no create, no request
    }

    [Fact]
    public void CommitCreate_is_idempotent_after_exit_so_blur_does_not_double_create()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session: session);
        vm.StartCreateCommand.Execute(null);
        vm.NewProfileName = "Bravo";

        vm.CommitCreateCommand.Execute(null); // Enter
        vm.CommitCreateCommand.Execute(null); // LostFocus (no-op)

        Assert.Single(profiles.CreatedNames);
    }

    [Fact]
    public void CancelCreate_discards_the_entry_and_hides_the_add_row()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session: session);
        vm.StartCreateCommand.Execute(null);
        vm.NewProfileName = "Discarded";

        vm.CancelCreateCommand.Execute(null); // Esc

        Assert.Empty(profiles.CreatedNames);
        Assert.False(vm.IsAddingNew);
        Assert.Empty(vm.NewProfileName);
    }
}
