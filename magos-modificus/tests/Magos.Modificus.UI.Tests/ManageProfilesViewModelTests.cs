using Magos.Modificus.Profiles;
using Magos.Modificus.UI.ViewModels;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Manage-profiles dialog VM: create / rename / delete(+confirm), and the
/// active-profile tracking the shell consumes via <see cref="ManageProfilesViewModel.ActiveProfileId"/>.
/// The delete confirmation is exercised through the <see cref="IDialogService"/>
/// seam — no real Avalonia window.
/// </summary>
public sealed class ManageProfilesViewModelTests
{
    private static ManageProfilesViewModel Build(
        FakeProfileService profiles,
        FakeDialogService? dialogs = null,
        Guid? initialActive = null)
    {
        dialogs ??= new FakeDialogService();
        return new ManageProfilesViewModel(profiles, dialogs, initialActive);
    }

    private static ProfileSummary Profile(string name) =>
        new(Guid.NewGuid(), name);

    // ---- construction / seeding --------------------------------------------

    [Fact]
    public void Construction_loads_the_list_and_preselects_the_active_profile()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var vm = Build(TestDoubles.Profiles(a, b), initialActive: b.Id);

        Assert.Equal(2, vm.ProfileList.Count);
        Assert.NotNull(vm.SelectedProfile);
        Assert.Equal(b.Id, vm.SelectedProfile!.Id);
        Assert.Equal(b.Id, vm.ActiveProfileId);
    }

    [Fact]
    public void Construction_preselects_first_when_the_active_id_is_unknown()
    {
        var a = Profile("Alpha");
        var vm = Build(TestDoubles.Profiles(a), initialActive: Guid.NewGuid());

        Assert.Equal(a.Id, vm.SelectedProfile?.Id);
    }

    [Fact]
    public void Selection_change_prefills_the_rename_field()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var vm = Build(TestDoubles.Profiles(a, b), initialActive: a.Id);

        vm.SelectedProfile = b;

        Assert.Equal("Bravo", vm.RenameName);
    }

    // ---- create ------------------------------------------------------------

    [Fact]
    public void CreateProfile_adds_the_profile_and_makes_it_active()
    {
        var existing = Profile("Alpha");
        var profiles = TestDoubles.Profiles(existing);
        var vm = Build(profiles, initialActive: existing.Id);

        vm.NewProfileName = "Bravo";
        vm.CreateProfileCommand.Execute(null);

        Assert.Contains("Bravo", profiles.CreatedNames);
        Assert.Equal(2, vm.ProfileList.Count);
        // New profile selected + reported as active.
        Assert.Equal("Bravo", vm.SelectedProfile?.Name);
        Assert.Equal(vm.SelectedProfile!.Id, vm.ActiveProfileId);
        Assert.Empty(vm.NewProfileName); // field cleared
    }

    [Fact]
    public void CreateProfile_command_is_disabled_without_a_name()
    {
        var vm = Build(TestDoubles.Profiles(Profile("Alpha")));

        Assert.False(vm.CreateProfileCommand.CanExecute(null));

        vm.NewProfileName = "   ";
        Assert.False(vm.CreateProfileCommand.CanExecute(null));

        vm.NewProfileName = "Bravo";
        Assert.True(vm.CreateProfileCommand.CanExecute(null));
    }

    // ---- rename ------------------------------------------------------------

    [Fact]
    public void RenameProfile_renames_the_selected_profile()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var vm = Build(profiles, initialActive: a.Id);
        vm.SelectedProfile = a;
        vm.RenameName = "Alpha Renamed";

        vm.RenameProfileCommand.Execute(null);

        Assert.Equal((a.Id, "Alpha Renamed"), Assert.Single(profiles.Renames));
        Assert.Equal("Alpha Renamed", vm.SelectedProfile?.Name);
    }

    [Fact]
    public void RenameProfile_does_not_change_the_active_id()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var vm = Build(profiles, initialActive: a.Id);

        vm.RenameName = "Beta";
        vm.RenameProfileCommand.Execute(null);

        Assert.Equal(a.Id, vm.ActiveProfileId);
    }

    [Fact]
    public void RenameProfile_command_requires_a_selection()
    {
        // No profiles → nothing to select → Rename disabled.
        var vm = Build(TestDoubles.Profiles());

        Assert.False(vm.RenameProfileCommand.CanExecute(null));
    }

    // ---- delete ------------------------------------------------------------

    [Fact]
    public async Task DeleteProfile_prompts_for_confirmation()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService();
        var vm = Build(profiles, dialogs, initialActive: a.Id);
        vm.SelectedProfile = a;

        await vm.DeleteProfileCommand.ExecuteAsync(null);

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
        vm.SelectedProfile = a;

        await vm.DeleteProfileCommand.ExecuteAsync(null);

        Assert.Equal(a.Id, Assert.Single(profiles.DeletedIds));
        Assert.Empty(vm.ProfileList);
    }

    [Fact]
    public async Task DeleteProfile_aborts_when_not_confirmed()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var vm = Build(profiles, dialogs, initialActive: a.Id);
        vm.SelectedProfile = a;

        await vm.DeleteProfileCommand.ExecuteAsync(null);

        Assert.Empty(profiles.DeletedIds);
        Assert.Single(vm.ProfileList);
    }

    [Fact]
    public async Task DeleteProfile_of_the_active_falls_back_to_first_remaining()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, dialogs, initialActive: a.Id);
        vm.SelectedProfile = a;

        await vm.DeleteProfileCommand.ExecuteAsync(null);

        Assert.Equal(a.Id, Assert.Single(profiles.DeletedIds));
        Assert.True(vm.ActiveProfileId.HasValue);
        Assert.NotEqual(a.Id, vm.ActiveProfileId); // fell back to a remaining one
    }

    [Fact]
    public async Task DeleteProfile_of_the_last_active_falls_back_to_null()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, dialogs, initialActive: a.Id);
        vm.SelectedProfile = a;

        await vm.DeleteProfileCommand.ExecuteAsync(null);

        Assert.Null(vm.ActiveProfileId);
        Assert.Null(vm.SelectedProfile);
    }

    [Fact]
    public void DeleteProfile_command_requires_a_selection()
    {
        // No profiles → nothing to select → Delete disabled.
        var vm = Build(TestDoubles.Profiles());

        Assert.False(vm.DeleteProfileCommand.CanExecute(null));
    }
}
