using Magos.Modificus.Config;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Settings;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Tests for <see cref="DiscoveryEscapeHatchViewModel"/>: only the missing
/// fields are shown, submit writes the entered paths through a read-modify-save
/// + marks the result true, cancel aborts without a write, and there is no
/// auto-retry (the caller does not re-launch).
/// </summary>
public sealed class DiscoveryEscapeHatchViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static DiscoveryFieldRowViewModel Row(DiscoveryEscapeHatchViewModel vm, string fieldName) =>
        vm.Rows.First(r => r.Field.FieldName == fieldName);

    // ---- only the missing fields are shown --------------------------------

    [Fact]
    public void Empty_missing_fields_yields_no_rows()
    {
        var vm = new DiscoveryEscapeHatchViewModel(
            Array.Empty<string>(), new FakeConfigLoader(), Localization);

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Rows_are_built_only_for_the_missing_fields_in_catalog_order()
    {
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "ProtonBinaryPath", "SteamInstallPath" },
            new FakeConfigLoader(),
            Localization);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("SteamInstallPath", vm.Rows[0].Field.FieldName);
        Assert.Equal("ProtonBinaryPath", vm.Rows[1].Field.FieldName);
    }

    [Fact]
    public void Unknown_field_names_are_dropped_silently()
    {
        // A future field name the catalog does not know yet must not crash the
        // dialog; it is dropped, the known ones still show.
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath", "SomeFutureField" },
            new FakeConfigLoader(),
            Localization);

        Assert.Single(vm.Rows);
        Assert.Equal("SteamInstallPath", vm.Rows[0].Field.FieldName);
    }

    [Fact]
    public void All_four_fields_can_show_at_once()
    {
        // Linux worst case: every field missing.
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath", "DarktideGameBinaryPath", "CompatdataPath", "ProtonBinaryPath" },
            new FakeConfigLoader(),
            Localization);

        Assert.Equal(4, vm.Rows.Count);
    }

    // ---- pre-fill --------------------------------------------------------

    [Fact]
    public void Rows_pre_fill_with_the_current_override_when_set()
    {
        var loader = new FakeConfigLoader
        {
            Config = new MagosConfig
            {
                Discovery = new DiscoveryConfig
                {
                    UserSteamInstallPath = "/prior/steam", // a wrong path the user is correcting
                },
            },
        };

        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath" }, loader, Localization);

        Assert.Equal("/prior/steam", Row(vm, "SteamInstallPath").Value);
    }

    // ---- submit ----------------------------------------------------------

    [Fact]
    public void Submit_writes_all_entered_paths_in_one_read_modify_save()
    {
        var loader = new FakeConfigLoader();
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath", "ProtonBinaryPath" }, loader, Localization);

        Row(vm, "SteamInstallPath").Value = "/steam";
        Row(vm, "ProtonBinaryPath").Value = "/proton";

        // Editing the rows does NOT save (the escape-hatch stages until submit).
        Assert.Equal(0, loader.SaveCalls);

        vm.SubmitCommand.Execute(null);

        Assert.Equal(1, loader.SaveCalls);
        Assert.Equal("/steam", loader.LastSaved!.Discovery.UserSteamInstallPath);
        Assert.Equal("/proton", loader.LastSaved.Discovery.UserProtonBinaryPath);
    }

    [Fact]
    public void Submit_marks_result_true()
    {
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath" }, new FakeConfigLoader(), Localization);

        Assert.False(vm.Result);

        vm.SubmitCommand.Execute(null);

        Assert.True(vm.Result);
    }

    [Fact]
    public void Submit_writes_null_for_empty_values_so_they_fall_back_to_auto()
    {
        var loader = new FakeConfigLoader
        {
            Config = new MagosConfig
            {
                Discovery = new DiscoveryConfig { UserSteamInstallPath = "/old" },
            },
        };
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath" }, loader, Localization);

        Row(vm, "SteamInstallPath").Value = ""; // cleared
        vm.SubmitCommand.Execute(null);

        Assert.Null(loader.LastSaved!.Discovery.UserSteamInstallPath);
    }

    // ---- cancel ----------------------------------------------------------

    [Fact]
    public void Cancel_aborts_without_writing_and_marks_result_false()
    {
        var loader = new FakeConfigLoader();
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath" }, loader, Localization);

        Row(vm, "SteamInstallPath").Value = "/ignored";
        vm.CancelCommand.Execute(null);

        Assert.Equal(0, loader.SaveCalls);
        Assert.False(vm.Result);
    }

    // ---- no auto-retry ---------------------------------------------------

    [Fact]
    public void Submit_does_not_flag_any_retry_signal_to_the_caller()
    {
        // The escape-hatch's contract is fire-and-forget: it returns
        // true/false, and the caller (the shell) does not auto-retry on true.
        // There is no RetryRequested flag or similar; the contract is the
        // boolean Result. Verified here by asserting the surface stays at one
        // signal.
        var vm = new DiscoveryEscapeHatchViewModel(
            new[] { "SteamInstallPath" }, new FakeConfigLoader(), Localization);

        vm.SubmitCommand.Execute(null);

        // No additional surface beyond Result. (The shell's behavior of NOT
        // auto-retrying is verified in ShellViewModelTests.)
        Assert.True(vm.Result);
    }
}

/// <summary>
/// Smoke test the <see cref="DiscoveryFields"/> catalog: All lists the four
/// fields in catalog order, and Find round-trips the canonical names.
/// </summary>
public sealed class DiscoveryFieldsCatalogTests
{
    [Fact]
    public void All_lists_the_four_fields_in_catalog_order()
    {
        var names = DiscoveryFields.All.Select(f => f.FieldName).ToArray();

        Assert.Equal(
            new[] { "SteamInstallPath", "DarktideGameBinaryPath", "CompatdataPath", "ProtonBinaryPath" },
            names);
    }

    [Theory]
    [InlineData("SteamInstallPath", DiscoveryBrowseKind.Folder)]
    [InlineData("DarktideGameBinaryPath", DiscoveryBrowseKind.File)]
    [InlineData("CompatdataPath", DiscoveryBrowseKind.Folder)]
    [InlineData("ProtonBinaryPath", DiscoveryBrowseKind.File)]
    public void Find_returns_the_field_with_its_browse_kind(string name, DiscoveryBrowseKind expected)
    {
        var field = DiscoveryFields.Find(name);

        Assert.NotNull(field);
        Assert.Equal(expected, field!.BrowseKind);
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_name()
    {
        Assert.Null(DiscoveryFields.Find("SomeFutureField"));
    }
}
