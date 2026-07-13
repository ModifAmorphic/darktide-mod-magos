using Modificus.Curator.Mods;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Import-modal VM behaviors: source chooser shows/hides the conditional fields,
/// the URL parser accepts / rejects inputs and gates OK, Version is required for
/// the remote source while Local needs nothing, and Confirm yields a parsed
/// <see cref="ImportModResult"/> (URL to canonical source) or leaves it
/// <c>null</c> (cancel). All against the pure VM (no window).
/// </summary>
public sealed class ImportModViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static ImportModViewModel Build(string modName = "MyMod") =>
        new(new ImportModRequest(modName, $"/mods/{modName}"), Localization);

    // ---- source chooser shows / hides fields -------------------------------

    [Fact]
    public void Default_source_is_Nexus_so_the_modal_opens_ready_for_a_URL()
    {
        // Most Darktide mods ship on Nexus, so the modal opens with Nexus
        // selected (the Version + URL fields visible). The user can switch to
        // Untracked when needed.
        var vm = Build();

        Assert.Equal(ImportModViewModel.ImportSource.Nexus, vm.SourceChoice);
        Assert.True(vm.IsRemote);
        Assert.True(vm.IsVersionVisible);
        // Cannot confirm yet: a Nexus import needs a Version + a URL.
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public void Local_source_hides_the_remote_fields()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Untracked;

        Assert.False(vm.IsRemote);
        Assert.False(vm.IsVersionVisible);
    }

    [Fact]
    public void Nexus_source_shows_the_remote_fields()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Nexus;

        Assert.True(vm.IsRemote);
        Assert.True(vm.IsVersionVisible);
    }

    [Fact]
    public void SourceChoiceIndex_maps_to_and_from_the_enum()
    {
        // The source chooser offers exactly two options: 0 = Untracked, 1 = Nexus.
        var vm = Build();
        vm.SourceChoiceIndex = 0;
        Assert.Equal(ImportModViewModel.ImportSource.Untracked, vm.SourceChoice);
        Assert.Equal(0, vm.SourceChoiceIndex);

        vm.SourceChoice = ImportModViewModel.ImportSource.Nexus;
        Assert.Equal(1, vm.SourceChoiceIndex);
    }

    [Fact]
    public void Source_chooser_has_only_untracked_and_nexus()
    {
        // The only valid ImportSource values are Untracked (0) and Nexus (1).
        var values = Enum.GetValues<ImportModViewModel.ImportSource>();
        Assert.Equal(2, values.Length);
        Assert.Contains(ImportModViewModel.ImportSource.Untracked, values);
        Assert.Contains(ImportModViewModel.ImportSource.Nexus, values);
    }

    // ---- Untracked needs nothing -------------------------------------------

    [Fact]
    public void Untracked_can_confirm_with_nothing_extra_and_records_UntrackedSource()
    {
        var vm = Build("MyMod");
        vm.SourceChoice = ImportModViewModel.ImportSource.Untracked;

        Assert.True(vm.CanConfirm);

        vm.ConfirmCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.IsType<UntrackedSource>(vm.Result!.Source);
        Assert.Equal(string.Empty, vm.Result.Version);
    }

    // ---- Nexus parse -------------------------------------------------------

    [Fact]
    public void Nexus_with_a_valid_url_and_version_confirms_with_NexusSource()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Nexus;
        vm.Version = "1.2";
        vm.Url = "https://www.nexusmods.com/warhammer40kdarktide/mods/12345";

        Assert.True(vm.CanConfirm);

        vm.ConfirmCommand.Execute(null);

        var nexus = Assert.IsType<NexusSource>(vm.Result!.Source);
        Assert.Equal(12345, nexus.ModId);
        Assert.Equal("1.2", vm.Result.Version);
    }

    [Fact]
    public void Nexus_accepts_a_plain_mod_id_url()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Nexus;
        vm.Version = "1.0";
        vm.Url = "12345";

        Assert.True(vm.CanConfirm);

        vm.ConfirmCommand.Execute(null);

        var nexus = Assert.IsType<NexusSource>(vm.Result!.Source);
        Assert.Equal(12345, nexus.ModId);
    }

    [Fact]
    public void Nexus_with_an_invalid_url_disables_OK_and_shows_a_message()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Nexus;
        vm.Version = "1.0";
        vm.Url = "not a nexus url";

        Assert.False(vm.CanConfirm);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.NotEmpty(vm.UrlValidationMessage);
    }

    [Fact]
    public void Nexus_version_is_required_ok_disabled_with_empty_version()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Nexus;
        vm.Version = "   ";
        vm.Url = "https://www.nexusmods.com/warhammer40kdarktide/mods/12345";

        Assert.False(vm.CanConfirm);
        Assert.NotEmpty(vm.VersionValidationMessage);
    }

    [Fact]
    public void Nexus_version_filled_and_valid_url_enables_ok_with_no_message()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Nexus;
        vm.Version = "1.2";
        vm.Url = "https://www.nexusmods.com/warhammer40kdarktide/mods/12345";

        Assert.True(vm.CanConfirm);
        Assert.Empty(vm.VersionValidationMessage);

        vm.ConfirmCommand.Execute(null);

        var nexus = Assert.IsType<NexusSource>(vm.Result!.Source);
        Assert.Equal(12345, nexus.ModId);
        Assert.Equal("1.2", vm.Result.Version);
    }

    [Fact]
    public void Nexus_empty_url_shows_the_required_message()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Nexus;

        Assert.NotEmpty(vm.UrlValidationMessage);
        Assert.False(vm.CanConfirm);
    }

    // ---- name handling -----------------------------------------------------

    [Fact]
    public void An_empty_mod_name_disables_OK()
    {
        var vm = Build();
        vm.ModName = "   ";

        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public void Confirm_writes_the_trimmed_name_back_to_the_request()
    {
        var request = new ImportModRequest("MyMod", "/mods/MyMod");
        var vm = new ImportModViewModel(request, Localization);
        vm.ModName = "  RenamedMod  ";
        vm.SourceChoice = ImportModViewModel.ImportSource.Untracked;

        vm.ConfirmCommand.Execute(null);

        Assert.Equal("RenamedMod", request.ModName);
        Assert.IsType<UntrackedSource>(vm.Result!.Source);
    }

    // ---- cancel -----------------------------------------------------------

    [Fact]
    public void A_new_modal_has_no_result_until_confirm()
    {
        var vm = Build();

        // Cancel = the user dismisses without confirming; Result stays null.
        Assert.Null(vm.Result);
    }

    // ---- policy selector ---------------------------------------------------

    [Fact]
    public void Default_policy_is_Latest()
    {
        var vm = Build();

        Assert.Equal(ImportModViewModel.ImportPolicyChoice.Latest, vm.PolicyChoice);
        Assert.Equal(0, vm.PolicyChoiceIndex);
        Assert.IsType<LatestPolicy>(vm.Policy);
    }

    [Fact]
    public void Switching_policy_to_Pinned_yields_a_PinnedPolicy()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Untracked;
        vm.PolicyChoiceIndex = 1; // Pinned

        Assert.Equal(ImportModViewModel.ImportPolicyChoice.Pinned, vm.PolicyChoice);
        // The derived policy is a placeholder PinnedPolicy (the modal does not
        // know the version id yet; the add flow substitutes it after Import).
        var pinned = Assert.IsType<PinnedPolicy>(vm.Policy);
        Assert.Equal(string.Empty, pinned.VersionId);
    }

    [Fact]
    public void Confirm_carries_the_chosen_policy_in_the_result()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Untracked;
        vm.PolicyChoice = ImportModViewModel.ImportPolicyChoice.Pinned;

        vm.ConfirmCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.IsType<PinnedPolicy>(vm.Result!.Policy);
    }

    [Fact]
    public void Confirm_with_Latest_choice_carries_a_LatestPolicy()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.Untracked;
        // Latest is the default, but set it explicitly for clarity.
        vm.PolicyChoice = ImportModViewModel.ImportPolicyChoice.Latest;

        vm.ConfirmCommand.Execute(null);

        Assert.NotNull(vm.Result);
        Assert.IsType<LatestPolicy>(vm.Result!.Policy);
    }
}
