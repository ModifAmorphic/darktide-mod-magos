using Magos.Modificus.Profiles;
using Magos.Modificus.Mods;
using Magos.Modificus.UI.Dialogs;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.ViewModels;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// Mod-list VM behaviors against hand-rolled fakes: load on active profile +
/// reload on active-id change, empty states, enable/disable, reorder (up/down),
/// auto-sort (identity no-op), remove (confirm / cancel), per-mod policy, and the
/// add flow (picker / drag-and-drop) with sequential per-mod modals including
/// cancel-mid-batch. Source + version badge text is joined from the repository by
/// container id.
/// </summary>
public sealed class ModListViewModelTests
{
    private static readonly LocalizationService Localization = new();

    private static ModListViewModel Build(
        FakeProfileService? profiles = null,
        FakeProfileSession? session = null,
        FakeModRepository? repo = null,
        FakeModImportService? importService = null,
        FakeDialogService? dialogs = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        importService ??= new FakeModImportService(repo);
        return TestDoubles.BuildModList(profiles, session, repo, importService,
            dialogs: dialogs, localization: Localization);
    }

    private static ProfileSummary Profile(string name) => new(Guid.NewGuid(), name);

    /// <summary>
    /// Seeds the repository with a container that has one latest version, for the
    /// badge-join tests. Returns the container.
    /// </summary>
    private static ModContainer Seed(FakeModRepository repo, ModSource source, string name, string versionTag = "1.0")
        => repo.Seed(source, name, versionTag);

    private static ModItemViewModel Row(ModListViewModel vm, string name) =>
        vm.Mods.Single(m => m.Name == name);

    // ---- load on active profile + empty states -----------------------------

    [Fact]
    public void Load_with_an_active_profile_joins_source_and_version_from_the_repo_by_container_id()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new NexusSource { ModId = 1234 }, "DMF", "1.0");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack", "");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Enabled = true, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Enabled = false, Order = 1 });

        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        Assert.True(vm.HasActiveProfile);
        Assert.True(vm.HasMods);
        Assert.False(vm.IsEmptyNoMods);
        Assert.Equal(2, vm.Mods.Count);
        // Sorted by Order.
        Assert.Equal("DMF", vm.Mods[0].Name);
        Assert.Equal("SoundPack", vm.Mods[1].Name);
        // Source / version joined from the repo by container id.
        Assert.Equal("Nexus #1234", Row(vm, "DMF").SourceBadgeText);
        Assert.Equal("Untracked", Row(vm, "SoundPack").SourceBadgeText);
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
    public void A_missing_container_shows_the_not_found_badge()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a).WithMods(a.Id,
            new ModListEntry { ContainerId = Guid.NewGuid(), Enabled = true, Order = 0 });

        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id });

        Assert.False(vm.Mods[0].Found);
        Assert.Equal("Not found", vm.Mods[0].SourceBadgeText);
    }

    // ---- reload on active-id change ----------------------------------------

    [Fact]
    public void Changing_the_active_profile_reloads_the_list()
    {
        var a = Profile("Alpha");
        var b = Profile("Bravo");
        var profiles = TestDoubles.Profiles(a, b);
        var repo = new FakeModRepository();
        var aContainer = Seed(repo, new UntrackedSource(), "A1");
        var bContainer = Seed(repo, new UntrackedSource(), "B1");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = aContainer.Id, Order = 0 });
        profiles.WithMods(b.Id, new ModListEntry { ContainerId = bContainer.Id, Order = 0 });
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        Assert.Equal("A1", Assert.Single(vm.Mods).Name);

        session.ActiveProfileId = b.Id;

        Assert.Equal("B1", Assert.Single(vm.Mods).Name);
    }

    [Fact]
    public void Clearing_the_active_profile_empties_the_list()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "A1");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession(() => profiles.ListProfiles()) { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
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
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Enabled = true, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
        var row = Row(vm, "DMF");

        // The CheckBox two-way binding flips Enabled first; the command applies it.
        row.Enabled = false;
        vm.ToggleEnabledCommand.Execute(row);

        Assert.Contains((a.Id, container.Id, false), profiles.SetModEnabledCalls);
    }

    [Fact]
    public void ToggleEnabled_is_a_noop_without_an_active_profile()
    {
        var profiles = TestDoubles.Profiles();
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = null });

        vm.ToggleEnabledCommand.Execute(new ModItemViewModel(Localization, Guid.NewGuid(), "X",
            new UntrackedSource(), "", true, 0, ModVersionPolicy.Latest, Array.Empty<ModVersion>(), true));

        Assert.Empty(profiles.SetModEnabledCalls);
    }

    // ---- reorder (up / down) -----------------------------------------------

    [Fact]
    public void MoveUp_swaps_with_the_predecessor_and_persists_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        vm.MoveUpCommand.Execute(Row(vm, "SoundPack"));

        // The persisted order has SoundPack's container first.
        Assert.Equal(new[] { sound.Id, dmf.Id }, Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("SoundPack", vm.Mods[0].Name);
        Assert.Equal("DMF", vm.Mods[1].Name);
    }

    [Fact]
    public void MoveDown_swaps_with_the_successor_and_persists_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        vm.MoveDownCommand.Execute(Row(vm, "DMF"));

        Assert.Equal(new[] { sound.Id, dmf.Id }, Assert.Single(profiles.SetModOrderCalls));
        Assert.Equal("SoundPack", vm.Mods[0].Name);
    }

    [Fact]
    public void MoveUp_at_the_top_is_a_noop()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        vm.MoveUpCommand.Execute(Row(vm, "DMF"));

        Assert.Empty(profiles.SetModOrderCalls);
    }

    // ---- auto-sort (identity no-op) ----------------------------------------

    [Fact]
    public void AutoSort_runs_the_resolver_and_persists_the_order()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var dmf = Seed(repo, new UntrackedSource(), "DMF");
        var sound = Seed(repo, new UntrackedSource(), "SoundPack");
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = dmf.Id, Order = 0 },
            new ModListEntry { ContainerId = sound.Id, Order = 1 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);

        vm.AutoSortCommand.Execute(null);

        // Identity resolver returns the current order unchanged (by container id).
        Assert.Equal(new[] { dmf.Id, sound.Id }, Assert.Single(profiles.SetModOrderCalls));
    }

    // ---- remove (confirm / cancel) -----------------------------------------

    [Fact]
    public async Task Remove_confirmed_calls_RemoveMod_and_drops_the_row()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { ConfirmResult = true };
        var vm = Build(profiles, session, repo, dialogs: dialogs);

        await vm.RemoveCommand.ExecuteAsync(Row(vm, "DMF"));

        Assert.Contains((a.Id, container.Id), profiles.RemoveModCalls);
        Assert.Empty(vm.Mods);
        Assert.Contains("DMF", dialogs.LastConfirmMessage);
    }

    [Fact]
    public async Task Remove_cancelled_leaves_the_list_and_service_untouched()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService { ConfirmResult = false };
        var vm = Build(profiles, session, repo, dialogs: dialogs);

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

        await vm.RemoveCommand.ExecuteAsync(new ModItemViewModel(Localization, Guid.NewGuid(), "X",
            new UntrackedSource(), "", true, 0, ModVersionPolicy.Latest, Array.Empty<ModVersion>(), true));

        Assert.Empty(profiles.RemoveModCalls);
        Assert.Equal(0, dialogs.ConfirmCalls);
    }

    // ---- per-mod policy ----------------------------------------------------

    [Fact]
    public void SetPolicyPinned_applies_a_PinnedPolicy_with_the_selected_versionId()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
        var row = Row(vm, "DMF");

        // The row exposes the container's versions for the dropdown; pick the
        // only one (the dropdown guarantees the id exists in the container).
        Assert.Single(row.AvailableVersions);
        row.SelectedVersion = row.AvailableVersions[0];

        vm.SetPolicyPinnedCommand.Execute(row);

        var (id, containerId, policy) = Assert.Single(profiles.SetModPolicyCalls);
        Assert.Equal(a.Id, id);
        Assert.Equal(container.Id, containerId);
        var pinned = Assert.IsType<PinnedPolicy>(policy);
        Assert.Equal(container.Versions[0].Folder, pinned.VersionId);
        // The reloaded row reflects the new effective policy.
        Assert.True(Row(vm, "DMF").Policy is PinnedPolicy);
    }

    [Fact]
    public void SetPolicyPinned_is_a_noop_when_the_container_has_no_versions()
    {
        // A version-less container's dropdown is empty; pinning is impossible.
        // SetPolicyPinned no-ops rather than creating a phantom pin.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = repo.CreateContainer(new UntrackedSource(), "DMF"); // no versions
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
        var row = Row(vm, "DMF");

        Assert.Empty(row.AvailableVersions);
        Assert.Null(row.SelectedVersion);

        vm.SetPolicyPinnedCommand.Execute(row);

        Assert.Empty(profiles.SetModPolicyCalls);
    }

    [Fact]
    public void SetPolicyLatest_applies_the_Latest_policy()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        var vId = container.Versions[0].Folder;
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = container.Id, Order = 0, Policy = new PinnedPolicy(vId) });
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var vm = Build(profiles, session, repo);
        var row = Row(vm, "DMF");
        Assert.True(row.IsPinned);

        vm.SetPolicyLatestCommand.Execute(row);

        var (_, _, policy) = Assert.Single(profiles.SetModPolicyCalls);
        Assert.IsType<LatestPolicy>(policy);
        Assert.False(Row(vm, "DMF").IsPinned);
    }

    [Fact]
    public void PinnedPolicy_display_text_uses_the_resolved_pinned_version_tag()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        var vId = container.Versions[0].Folder;
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = container.Id, Order = 0, Policy = new PinnedPolicy(vId) });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);

        // The display text surfaces the resolved version's readable tag, not the
        // opaque folder id.
        Assert.Contains("1.0", Row(vm, "DMF").PolicyDisplayText);
        Assert.DoesNotContain(vId, Row(vm, "DMF").PolicyDisplayText);
    }

    [Fact]
    public void Row_exposes_the_container_versions_for_the_pin_dropdown()
    {
        // The dropdown's source is the container's version list, each option
        // pairing the readable tag (shown) with the opaque folder id (stored).
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        repo.AddVersion(container.Id, "2.0", _ => { });
        var versions = repo.Get(container.Id)!.Versions;
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        var row = Row(vm, "DMF");

        Assert.Equal(2, row.AvailableVersions.Count);
        Assert.Contains(row.AvailableVersions, o => o.VersionString == "1.0");
        Assert.Contains(row.AvailableVersions, o => o.VersionString == "2.0");
        // Each option carries the version's folder id (the versionId foreign key).
        Assert.All(row.AvailableVersions, o => Assert.NotEmpty(o.VersionId));
        Assert.Equal(versions.Select(v => v.Folder).ToHashSet(),
            row.AvailableVersions.Select(o => o.VersionId).ToHashSet());
    }

    [Fact]
    public void Pinned_row_pre_selects_the_pinned_version_in_the_dropdown()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        repo.AddVersion(container.Id, "2.0", _ => { });
        var v1Id = repo.Get(container.Id)!.Versions.Single(v => v.VersionString == "1.0").Folder;
        profiles.WithMods(a.Id,
            new ModListEntry { ContainerId = container.Id, Order = 0, Policy = new PinnedPolicy(v1Id) });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        var row = Row(vm, "DMF");

        Assert.NotNull(row.SelectedVersion);
        Assert.Equal(v1Id, row.SelectedVersion!.VersionId);
    }

    [Fact]
    public void Latest_row_pre_selects_the_isLatest_version_in_the_dropdown()
    {
        // A Latest row pre-selects the resolved (IsLatest) version, so a switch
        // to Pinned offers the actual version rather than a blank.
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var container = Seed(repo, new UntrackedSource(), "DMF", "1.0");
        repo.AddVersion(container.Id, "2.0", _ => { }); // becomes IsLatest
        var latestId = repo.Get(container.Id)!.Versions.Single(v => v.IsLatest).Folder;
        profiles.WithMods(a.Id, new ModListEntry { ContainerId = container.Id, Order = 0 });
        var vm = Build(profiles, new FakeProfileSession { ActiveProfileId = a.Id }, repo);
        var row = Row(vm, "DMF");

        Assert.NotNull(row.SelectedVersion);
        Assert.Equal(latestId, row.SelectedVersion!.VersionId);
    }

    // ---- add flow (split button picker / drag-and-drop) --------------------

    [Fact]
    public async Task AddMods_processes_each_path_with_a_modal_then_Import_then_AddMod()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        // Untracked source + distinct modNames (DMF / SoundPack) → distinct
        // containers (untracked dedups by name; the names differ here).
        var dialogs = new FakeDialogService
        {
            ImportResult = new ImportModResult(new UntrackedSource(), ""),
        };
        var vm = Build(profiles, session, repo, import, dialogs);

        await vm.AddModsCommand.ExecuteAsync(
            new[] { "/mods/DMF", "/mods/SoundPack.zip" });

        // One modal per path, in order.
        Assert.Equal(2, dialogs.ImportCalls);
        Assert.Equal("DMF", dialogs.ImportRequests[0].ModName);
        Assert.Equal("SoundPack", dialogs.ImportRequests[1].ModName); // .zip stem
        // Import then AddMod, per path.
        Assert.Equal(2, import.Imports.Count);
        Assert.Equal("DMF", import.Imports[0].ModName);
        Assert.Equal("SoundPack", import.Imports[1].ModName);
        // AddMod was called per path, with distinct container ids (distinct names).
        Assert.Equal(2, profiles.AddModCalls.Count);
        Assert.Equal(2, profiles.AddModCalls.Select(c => c.ContainerId).Distinct().Count());
        Assert.All(profiles.AddModCalls, c => Assert.Equal(a.Id, c.Id));
        // Both mods now appear, joined from the repo.
        Assert.Equal(2, vm.Mods.Count);
    }

    [Fact]
    public async Task AddMods_cancel_mid_batch_cancels_the_remaining_paths()
    {
        var a = Profile("Alpha");
        var profiles = TestDoubles.Profiles(a);
        var repo = new FakeModRepository();
        var import = new FakeModImportService(repo);
        var session = new FakeProfileSession { ActiveProfileId = a.Id };
        var dialogs = new FakeDialogService
        {
            // First path confirmed, second cancelled, third never reached.
            ImportResultQueue = new Queue<ImportModResult?>(new ImportModResult?[]
            {
                new(new UntrackedSource(), ""),
                null,
                new(new UntrackedSource(), ""),
            }),
        };
        var vm = Build(profiles, session, repo, import, dialogs);

        await vm.AddModsCommand.ExecuteAsync(
            new[] { "/mods/One", "/mods/Two", "/mods/Three" });

        Assert.Equal(2, dialogs.ImportCalls); // third modal never shown
        Assert.Single(import.Imports);        // only One imported
        Assert.Single(profiles.AddModCalls);
        Assert.Single(vm.Mods);
    }

    [Fact]
    public async Task AddMods_with_no_active_profile_logs_and_does_nothing()
    {
        var profiles = TestDoubles.Profiles();
        var dialogs = new FakeDialogService
        {
            ImportResult = new ImportModResult(new UntrackedSource(), ""),
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
            ImportResult = new ImportModResult(new UntrackedSource(), ""),
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
