using Magos.Modificus.Profiles;
using Magos.Modificus.UI.ViewModels;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Manage-profiles dialog VM — the editable-list mechanics: ✏ inline rename
/// (commit / cancel / empty-rejected / no-op-same-name), 🗑 delete-confirm
/// (yes deletes / no aborts / active-fallback), "+ New profile" add row (create
/// becomes active / empty cancels), and the active-profile tracking the shell
/// consumes via <see cref="ManageProfilesViewModel.ActiveProfileId"/>. Delete
/// confirmation is exercised through the <see cref="IDialogService"/> seam — no
/// real Avalonia window.
/// </summary>
public sealed class ManageProfilesViewModelTests
{
    private static ManageProfilesViewModel Build(
        FakeProfileService profiles,
        FakeDialogService? dialogs = null,
        FakeSteamService? steam = null,
        Guid? initialActive = null)
    {
        dialogs ??= new FakeDialogService();
        steam ??= new FakeSteamService { Running = false };
        return new ManageProfilesViewModel(profiles, dialogs, steam, initialActive);
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
        var vm = Build(TestDoubles.Profiles(a, b), initialActive: b.Id);

        Assert.Equal(2, vm.Items.Count);
        Assert.True(Row(vm, "Bravo").IsActive);
        Assert.False(Row(vm, "Alpha").IsActive);
        Assert.Equal(b.Id, vm.ActiveProfileId);
    }

    [Fact]
    public void Construction_marks_no_row_active_when_the_active_id_is_unknown()
    {
        var a = Profile("Alpha");
        var vm = Build(TestDoubles.Profiles(a), initialActive: Guid.NewGuid());

        Assert.False(Row(vm, "Alpha").IsActive);
        Assert.Single(vm.Items);
    }

    [Fact]
    public void Construction_marks_no_row_active_when_none_is_active()
    {
        var a = Profile("Alpha");
        var vm = Build(TestDoubles.Profiles(a), initialActive: null);

        Assert.False(Row(vm, "Alpha").IsActive);
    }

    // ---- rename (✏) --------------------------------------------------------

    [Fact]
    public void StartRename_prefills_the_edit_field_and_enters_edit_mode()
    {
        var a = Profile("Alpha");
        var vm = Build(TestDoubles.Profiles(a), initialActive: a.Id);
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
        var vm = Build(TestDoubles.Profiles(a, b), initialActive: a.Id);
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
        var vm = Build(profiles, initialActive: a.Id);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        row.EditText = "Alpha Renamed";
        vm.CommitRenameCommand.Execute(row); // Enter

        Assert.Equal((a.Id, "Alpha Renamed"), Assert.Single(profiles.Renames));
        Assert.False(row.IsEditing);
        Assert.Equal("Alpha Renamed", row.Name);
    }

    [Fact]
    public void CommitRename_does_not_change_the_active_id()
    {
        var a = Profile("Alpha");
        var vm = Build(TestDoubles.Profiles(a), initialActive: a.Id);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        row.EditText = "Beta";
        vm.CommitRenameCommand.Execute(row);

        Assert.Equal(a.Id, vm.ActiveProfileId);
    }

    [Fact]
    public void CommitRename_rejects_an_empty_entry_and_reverts()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var vm = Build(profiles, initialActive: a.Id);
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
        var vm = Build(profiles, initialActive: a.Id);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        // EditText pre-filled with "Alpha" — commit should not call RenameProfile.
        vm.CommitRenameCommand.Execute(row);

        Assert.Empty(profiles.Renames);
        Assert.False(row.IsEditing);
    }

    [Fact]
    public void CommitRename_is_idempotent_after_exit_so_blur_does_not_double_rename()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var vm = Build(profiles, initialActive: a.Id);
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
        var vm = Build(profiles, initialActive: a.Id);
        var row = Row(vm, "Alpha");

        vm.StartRenameCommand.Execute(row);
        row.EditText = "Discarded";
        vm.CancelRenameCommand.Execute(row); // Esc

        Assert.Empty(profiles.Renames);
        Assert.False(row.IsEditing);
        Assert.Equal("Alpha", row.Name); // unchanged
    }

    // ---- delete (🗑) --------------------------------------------------------

    [Fact]
    public async Task DeleteProfile_prompts_for_confirmation_with_the_name()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService();
        var vm = Build(profiles, dialogs, initialActive: a.Id);
        var row = Row(vm, "Alpha");

        await vm.DeleteProfileCommand.ExecuteAsync(row);

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Contains("Alpha", dialogs.LastConfirmMessage);
        Assert.Contains("mods/", dialogs.LastConfirmMessage!);
    }

    [Fact]
    public async Task DeleteProfile_deletes_when_confirmed()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, dialogs, initialActive: a.Id);

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
        var vm = Build(profiles, dialogs, initialActive: a.Id);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Alpha"));

        Assert.Empty(profiles.DeletedIds);
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task DeleteProfile_of_the_active_falls_back_to_first_remaining()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, dialogs, initialActive: a.Id);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Alpha"));

        Assert.Equal(a.Id, Assert.Single(profiles.DeletedIds));
        Assert.True(vm.ActiveProfileId.HasValue);
        Assert.NotEqual(a.Id, vm.ActiveProfileId); // fell back to a remaining one
        Assert.True(Row(vm, "Bravo").IsActive); // and that row is now marked
    }

    [Fact]
    public async Task DeleteProfile_of_the_last_active_falls_back_to_null()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, dialogs, initialActive: a.Id);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Alpha"));

        Assert.Null(vm.ActiveProfileId);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task DeleteProfile_of_a_non_active_profile_leaves_the_active_unchanged()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, dialogs, initialActive: a.Id);

        await vm.DeleteProfileCommand.ExecuteAsync(Row(vm, "Bravo"));

        Assert.Equal(b.Id, Assert.Single(profiles.DeletedIds));
        Assert.Equal(a.Id, vm.ActiveProfileId);
        Assert.True(Row(vm, "Alpha").IsActive);
    }

    // ---- create ("+ New profile") -----------------------------------------

    [Fact]
    public void StartCreate_shows_the_add_row_and_clears_the_entry()
    {
        var a = Profile("Alpha");
        var vm = Build(TestDoubles.Profiles(a), initialActive: a.Id);
        vm.NewProfileName = "stale";

        vm.StartCreateCommand.Execute(null);

        Assert.True(vm.IsAddingNew);
        Assert.Empty(vm.NewProfileName);
    }

    [Fact]
    public void StartCreate_cancels_any_inline_rename_in_flight()
    {
        var a = Profile("Alpha");
        var vm = Build(TestDoubles.Profiles(a), initialActive: a.Id);
        var row = Row(vm, "Alpha");
        vm.StartRenameCommand.Execute(row);

        vm.StartCreateCommand.Execute(null);

        Assert.False(row.IsEditing);
    }

    [Fact]
    public void CommitCreate_adds_the_profile_and_makes_it_active()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var vm = Build(profiles, initialActive: a.Id);
        vm.StartCreateCommand.Execute(null);
        vm.NewProfileName = "Bravo";

        vm.CommitCreateCommand.Execute(null); // Enter

        Assert.Contains("Bravo", profiles.CreatedNames);
        Assert.False(vm.IsAddingNew);
        Assert.Empty(vm.NewProfileName);
        // New profile is active + marked.
        var newRow = Row(vm, "Bravo");
        Assert.Equal(newRow.Id, vm.ActiveProfileId);
        Assert.True(newRow.IsActive);
        Assert.False(Row(vm, "Alpha").IsActive);
    }

    [Fact]
    public void CommitCreate_while_running_creates_the_profile_but_leaves_active_on_the_current_profile()
    {
        // The dialog-side half of the create-while-running gate: the profile is created
        // (it appears in the list), but create-sets-active consults the same running
        // state the shell's CanChangeActiveProfile gate reads, so the marker stays on the
        // current active instead of jumping to the new profile. This is the VM-side pin
        // for the gap that let the divergence ship (the shell-side test already exists);
        // the not-running branch is pinned by CommitCreate_adds_the_profile_and_makes_it_active.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var steam = new FakeSteamService { Running = true };
        var vm = Build(profiles, steam: steam, initialActive: a.Id);
        vm.StartCreateCommand.Execute(null);
        vm.NewProfileName = "Bravo";

        vm.CommitCreateCommand.Execute(null); // Enter

        Assert.Contains("Bravo", profiles.CreatedNames); // created...
        Assert.Equal(a.Id, vm.ActiveProfileId);          // ...but active stays on Alpha
        Assert.True(Row(vm, "Alpha").IsActive);          // marker held
        Assert.False(Row(vm, "Bravo").IsActive);         // new row not marked
    }

    [Fact]
    public void CommitCreate_rejects_an_empty_entry_and_cancels()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var vm = Build(profiles, initialActive: a.Id);
        vm.StartCreateCommand.Execute(null);
        vm.NewProfileName = "   ";

        vm.CommitCreateCommand.Execute(null);

        Assert.Empty(profiles.CreatedNames);
        Assert.False(vm.IsAddingNew);
        Assert.Empty(vm.NewProfileName);
    }

    [Fact]
    public void CommitCreate_is_idempotent_after_exit_so_blur_does_not_double_create()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var vm = Build(profiles, initialActive: a.Id);
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
        var vm = Build(profiles, initialActive: a.Id);
        vm.StartCreateCommand.Execute(null);
        vm.NewProfileName = "Discarded";

        vm.CancelCreateCommand.Execute(null); // Esc

        Assert.Empty(profiles.CreatedNames);
        Assert.False(vm.IsAddingNew);
        Assert.Empty(vm.NewProfileName);
    }
}
