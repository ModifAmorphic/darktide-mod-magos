using Magos.Modificus.Mods;
using Magos.Modificus.UI.Dialogs;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.ViewModels;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Import-modal VM behaviors: source chooser shows/hides the conditional fields,
/// the URL parsers accept / reject inputs and gate OK, Version is required for
/// remote sources while Local needs nothing, and Confirm yields a parsed
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
    public void GitHub_source_shows_the_remote_fields_with_a_repo_label()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.GitHub;

        Assert.True(vm.IsRemote);
        Assert.NotEmpty(vm.UrlLabel);
    }

    [Fact]
    public void SourceChoiceIndex_maps_to_and_from_the_enum()
    {
        var vm = Build();
        vm.SourceChoiceIndex = 2;
        Assert.Equal(ImportModViewModel.ImportSource.GitHub, vm.SourceChoice);
        Assert.Equal(2, vm.SourceChoiceIndex);

        vm.SourceChoice = ImportModViewModel.ImportSource.Nexus;
        Assert.Equal(1, vm.SourceChoiceIndex);
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

    // ---- GitHub parse ------------------------------------------------------

    [Fact]
    public void GitHub_with_a_valid_url_and_version_confirms_with_GitHubSource()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.GitHub;
        vm.Version = "v2.0.1";
        vm.Url = "https://github.com/owner/repo";

        Assert.True(vm.CanConfirm);

        vm.ConfirmCommand.Execute(null);

        var git = Assert.IsType<GitHubSource>(vm.Result!.Source);
        Assert.Equal("owner", git.Owner);
        Assert.Equal("repo", git.Repo);
        Assert.Equal("v2.0.1", vm.Result.Version);
    }

    [Fact]
    public void GitHub_strips_the_dot_git_suffix()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.GitHub;
        vm.Version = "1.0";
        vm.Url = "https://github.com/owner/repo.git";

        vm.ConfirmCommand.Execute(null);

        var git = Assert.IsType<GitHubSource>(vm.Result!.Source);
        Assert.Equal("repo", git.Repo);
    }

    [Fact]
    public void GitHub_with_too_few_segments_disables_OK()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.GitHub;
        vm.Version = "1.0";
        vm.Url = "https://github.com/owner";

        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public void GitHub_version_is_required_ok_disabled_with_empty_version()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.GitHub;
        vm.Version = "   ";
        vm.Url = "https://github.com/owner/repo";

        Assert.False(vm.CanConfirm);
        Assert.NotEmpty(vm.VersionValidationMessage);
    }

    [Fact]
    public void GitHub_version_filled_and_valid_url_enables_ok_with_no_message()
    {
        var vm = Build();
        vm.SourceChoice = ImportModViewModel.ImportSource.GitHub;
        vm.Version = "v2.0.1";
        vm.Url = "https://github.com/owner/repo";

        Assert.True(vm.CanConfirm);
        Assert.Empty(vm.VersionValidationMessage);

        vm.ConfirmCommand.Execute(null);

        var git = Assert.IsType<GitHubSource>(vm.Result!.Source);
        Assert.Equal("owner", git.Owner);
        Assert.Equal("repo", git.Repo);
        Assert.Equal("v2.0.1", vm.Result.Version);
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

    // ---- cancel ------------------------------------------------------------

    [Fact]
    public void A_new_modal_has_no_result_until_confirm()
    {
        var vm = Build();

        // Cancel = the user dismisses without confirming; Result stays null.
        Assert.Null(vm.Result);
    }
}
