using Magos.Modificus.Profiles;
using Magos.Modificus.SharedMods;
using Magos.Modificus.UI.Dialogs;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.ViewModels;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Mod-list VM behaviors against hand-rolled fakes: load on active profile +
/// reload on active-id change, empty states, enable/disable, reorder (up/down),
/// auto-sort (identity no-op), remove (confirm / cancel), per-mod policy, and the
/// add flow (picker / drag-and-drop) with sequential per-mod modals including
/// cancel-mid-batch. Source + version badge text is joined from the shared store.
/// </summary>
public sealed class ModListViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static FakeSharedModStore Shared(params SharedModEntry[] entries)
    {
        var store = new FakeSharedModStore();
        foreach (var e in entries)
        {
            store.Add(e);
        }
        return store;
    }

    private static ModListViewModel Build(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeSharedModStore? sharedStore = null,
        FakeModImportService? importService = null,
        FakeDialogService? dialogs = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        sharedStore ??= new FakeSharedModStore();
        importService ??= new FakeModImportService(sharedStore);
        return TestDoubles.BuildModList(profiles, session, sharedStore, importService,
            dialogs: dialogs, localization: Localization);
    }

    private static ProfileSummary Profile(string name) => new(Guid.NewGuid(), name);

    private static ModItemViewModel Row(ModListViewModel vm, string name) =>
        vm.Mods.Single(m => m.Name == name);

    // ---- load on active profile + empty states -----------------------------

    [Fact]
    public void Load_with_an_active_profile_joins_source_and_version_from_the_shared_store()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Enabled = true, Order = 0 },
            new ModListEntry { Name = "SoundPack", Enabled = false, Order = 1 });
        var shared = Shared(
            new SharedModEntry { Name = "DMF", Source = new NexusSource { ModId = 1234 }, ActualVersion = "1.0" },
            new SharedModEntry { Name = "SoundPack", Source = new NoneSource(), ActualVersion = "" });

        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, shared);

        Assert.True(vm.HasActiveProfile);
        Assert.True(vm.HasMods);
        Assert.False(vm.IsEmptyNoMods);
        Assert.Equal(2, vm.Mods.Count);
        // Sorted by Order.
        Assert.Equal("DMF", vm.Mods[0].Name);
        Assert.Equal("SoundPack", vm.Mods[1].Name);
        // Source / version joined from the shared store.
        Assert.Equal("Nexus #1234", Row(vm, "DMF").SourceBadgeText);
        Assert.Equal("Local", Row(vm, "SoundPack").SourceBadgeText);
        Assert.True(Row(vm, "DMF").Enabled);
        Assert.False(Row(vm, "SoundPack").Enabled);
    }

    [Fact]
    public void Load_with_no_active_profile_clears_and_shows_the_no_profile_empty_state()
    {
        var profiles = TestDoubles.Profiles();
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null });

        Assert.False(vm.HasActiveProfile);
        Assert.False(vm.HasMods);
        Assert.Empty(vm.Mods);
        Assert.NotEmpty(vm.EmptyNoProfileText);
    }

    [Fact]
    public void Load_with_an_active_profile_but_no_mods_shows_the_no_mods_empty_state()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id });

        Assert.True(vm.HasActiveProfile);
        Assert.False(vm.HasMods);
        Assert.True(vm.IsEmptyNoMods);
        Assert.Empty(vm.Mods);
    }

    [Fact]
    public void A_missing_shared_entry_shows_the_not_found_badge()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "Ghost", Enabled = true, Order = 0 });

        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, Shared());

        Assert.False(Row(vm, "Ghost").Found);
        Assert.Equal("Not found", Row(vm, "Ghost").SourceBadgeText);
    }

    // ---- reload on active-id change ----------------------------------------

    [Fact]
    public void Changing_the_active_profile_reloads_the_list()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        profiles.WithMods(a.Id, new ModListEntry { Name = "A1", Order = 0 });
        profiles.WithMods(b.Id, new ModListEntry { Name = "B1", Order = 0 });
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        Assert.Equal("A1", Assert.Single(vm.Mods).Name);

        session.ActiveProfileId = b.Id;

        Assert.Equal("B1", Assert.Single(vm.Mods).Name);
    }

    [Fact]
    public void Clearing_the_active_profile_empties_the_list()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "A1", Order = 0 });
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);
        Assert.Single(vm.Mods);

        session.ActiveProfileId = null;

        Assert.Empty(vm.Mods);
        Assert.False(vm.HasActiveProfile);
    }

    // ---- enable / disable --------------------------------------------------

    [Fact]
    public void ToggleEnabled_applies_the_new_state_via_SetModEnabled()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Enabled = true, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);
        var row = Row(vm, "DMF");

        // The CheckBox two-way binding flips Enabled first; the command applies it.
        row.Enabled = false;
        vm.ToggleEnabledCommand.Execute(row);

        Assert.Contains((a.Id, "DMF", false), profiles.SetModEnabledCalls);
    }

    [Fact]
    public void ToggleEnabled_is_a_noop_without_an_active_profile()
    {
        var profiles = TestDoubles.Profiles();
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null });

        vm.ToggleEnabledCommand.Execute(new ModItemViewModel(Localization, "X",
            new NoneSource(), "", true, 0, ModVersionPolicy.Latest, true));

        Assert.Empty(profiles.SetModEnabledCalls);
    }

    // ---- reorder (up / down) -----------------------------------------------

    [Fact]
    public void MoveUp_swaps_with_the_predecessor_and_persists_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Order = 0 },
            new ModListEntry { Name = "SoundPack", Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.MoveUpCommand.Execute(Row(vm, "SoundPack"));

        // The persisted order name list has SoundPack first.
        Assert.Equal(new[] { "SoundPack", "DMF" }, Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("SoundPack", vm.Mods[0].Name);
        Assert.Equal("DMF", vm.Mods[1].Name);
    }

    [Fact]
    public void MoveDown_swaps_with_the_successor_and_persists_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Order = 0 },
            new ModListEntry { Name = "SoundPack", Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.MoveDownCommand.Execute(Row(vm, "DMF"));

        Assert.Equal(new[] { "SoundPack", "DMF" }, Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("SoundPack", vm.Mods[0].Name);
    }

    [Fact]
    public void MoveUp_at_the_top_is_a_noop()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Order = 0 },
            new ModListEntry { Name = "SoundPack", Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.MoveUpCommand.Execute(Row(vm, "DMF"));

        Assert.Empty(profiles.SetModOrderCalls);
    }

    // ---- auto-sort (identity no-op) ----------------------------------------

    [Fact]
    public void AutoSort_runs_the_resolver_and_persists_the_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Order = 0 },
            new ModListEntry { Name = "SoundPack", Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);

        vm.AutoSortCommand.Execute(null);

        // Identity resolver returns the current order unchanged.
        Assert.Equal(new[] { "DMF", "SoundPack" }, Assert.Single(profiles.SetModOrderCalls));
    }

    // ---- remove (confirm / cancel) -----------------------------------------

    [Fact]
    public async Task Remove_confirmed_calls_RemoveMod_and_drops_the_row()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, session, dialogs: dialogs);

        await vm.RemoveCommand.ExecuteAsync(Row(vm, "DMF"));

        Assert.Contains((a.Id, "DMF"), profiles.RemoveModCalls);
        Assert.Empty(vm.Mods);
        Assert.Contains("DMF", dialogs.LastConfirmMessage);
    }

    [Fact]
    public async Task Remove_cancelled_leaves_the_list_and_service_untouched()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var vm = Build(profiles, session, dialogs: dialogs);

        await vm.RemoveCommand.ExecuteAsync(Row(vm, "DMF"));

        Assert.Empty(profiles.RemoveModCalls);
        Assert.Single(vm.Mods);
    }

    [Fact]
    public async Task Remove_is_a_noop_without_an_active_profile()
    {
        var profiles = TestDoubles.Profiles();
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null }, dialogs: dialogs);

        await vm.RemoveCommand.ExecuteAsync(new ModItemViewModel(Localization, "X",
            new NoneSource(), "", true, 0, ModVersionPolicy.Latest, true));

        Assert.Empty(profiles.RemoveModCalls);
        Assert.Equal(0, dialogs.ConfirmCalls);
    }

    // ---- per-mod policy ----------------------------------------------------

    [Fact]
    public void SetPolicyPinned_applies_a_PinnedPolicy_with_the_rows_version()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);
        var row = Row(vm, "DMF");
        row.PinnedVersion = "1.2.3";

        vm.SetPolicyPinnedCommand.Execute(row);

        var (id, mod, policy) = Assert.Single(profiles.SetModPolicyCalls);
        Assert.Equal(a.Id, id);
        Assert.Equal("DMF", mod);
        var pinned = Assert.IsType<PinnedPolicy>(policy);
        Assert.Equal("1.2.3", pinned.Version);
        // The reloaded row reflects the new effective policy (the captured row is
        // stale after Reload recreated the list).
        Assert.True(Row(vm, "DMF").Policy is PinnedPolicy);
    }

    [Fact]
    public void SetPolicyLatest_applies_the_Latest_policy()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Order = 0, Policy = new PinnedPolicy("1.0") });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session);
        var row = Row(vm, "DMF");
        Assert.True(row.IsPinned);

        vm.SetPolicyLatestCommand.Execute(row);

        var (_, _, policy) = Assert.Single(profiles.SetModPolicyCalls);
        Assert.IsType<LatestPolicy>(policy);
        Assert.False(Row(vm, "DMF").IsPinned);
    }

    [Fact]
    public void PinnedPolicy_display_text_uses_the_pinned_version()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { Name = "DMF", Order = 0, Policy = new PinnedPolicy("9.9") });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id });

        Assert.Contains("9.9", Row(vm, "DMF").PolicyDisplayText);
    }

    // ---- add flow (split button picker / drag-and-drop) --------------------

    [Fact]
    public async Task AddMods_processes_each_path_with_a_modal_then_Import_then_AddMod()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var shared = Shared();
        var import = new FakeModImportService(shared);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService
        {
            ImportResult = new ImportModResult(new NexusSource { ModId = 5 }, "2.0"),
        };
        var vm = Build(profiles, session, shared, import, dialogs);

        await vm.AddModsCommand.ExecuteAsync(
            new[] { "/mods/DMF", "/mods/SoundPack.zip" });

        // One modal per path, in order.
        Assert.Equal(2, dialogs.ImportCalls);
        Assert.Equal("DMF", dialogs.ImportRequests[0].ModName);
        Assert.Equal("SoundPack", dialogs.ImportRequests[1].ModName); // .zip stem
        // Import then AddMod, per path, using the derived name.
        Assert.Equal(2, import.Imports.Count);
        Assert.Equal("DMF", import.Imports[0].ModName);
        Assert.Equal("SoundPack", import.Imports[1].ModName);
        Assert.Equal(new[] { (a.Id, "DMF"), (a.Id, "SoundPack") }, profiles.AddModCalls);
        // Both mods now appear, joined from the shared store.
        Assert.Equal(2, vm.Mods.Count);
    }

    [Fact]
    public async Task AddMods_cancel_mid_batch_cancels_the_remaining_paths()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var shared = Shared();
        var import = new FakeModImportService(shared);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService
        {
            // First path confirmed, second cancelled, third never reached.
            ImportResultQueue = new Queue<ImportModResult?>(new ImportModResult?[]
            {
                new(new NoneSource(), ""),
                null,
                new(new NoneSource(), ""),
            }),
        };
        var vm = Build(profiles, session, shared, import, dialogs);

        await vm.AddModsCommand.ExecuteAsync(
            new[] { "/mods/One", "/mods/Two", "/mods/Three" });

        Assert.Equal(2, dialogs.ImportCalls); // third modal never shown
        Assert.Single(import.Imports);        // only One imported
        Assert.Single(profiles.AddModCalls);
        Assert.Contains((a.Id, "One"), profiles.AddModCalls);
        Assert.Single(vm.Mods);
    }

    [Fact]
    public async Task AddMods_with_no_active_profile_logs_and_does_nothing()
    {
        var profiles = TestDoubles.Profiles();
        var dialogs = new FakeDialogService
        {
            ImportResult = new ImportModResult(new NoneSource(), ""),
        };
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null }, dialogs: dialogs);

        await vm.AddModsCommand.ExecuteAsync(new[] { "/mods/One" });

        Assert.Equal(0, dialogs.ImportCalls);
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task AddMods_empty_path_list_is_a_noop()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService
        {
            ImportResult = new ImportModResult(new NoneSource(), ""),
        };
        var vm = Build(profiles, session, dialogs: dialogs);

        await vm.AddModsCommand.ExecuteAsync(Array.Empty<string>());

        Assert.Equal(0, dialogs.ImportCalls);
    }

    // ---- add split-button view state ---------------------------------------

    [Fact]
    public void AddMode_defaults_to_Zip_and_the_label_tracks_the_mode()
    {
        var vm = Build();

        Assert.Equal(ModAddMode.Zip, vm.AddMode);
        Assert.Equal("Add Mod (zip)", vm.AddModeLabel);

        vm.AddMode = ModAddMode.Folder;

        Assert.Equal(ModAddMode.Folder, vm.AddMode);
        Assert.Equal("Add Mod (folder)", vm.AddModeLabel);
    }
}
