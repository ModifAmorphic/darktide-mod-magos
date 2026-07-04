using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magos.Modificus.Profiles;
using Magos.Modificus.Mods;
using Magos.Modificus.UI.Dialogs;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.Session;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI.ViewModels;

/// <summary>
/// The add flow's current mode (which picker the Add split button's primary
/// click opens). Tracked by the view code-behind + mirrored on the VM (as
/// <see cref="ModListViewModel.AddMode"/>) so the split button's label reflects
/// the selected mode. Zip is the default.
/// </summary>
public enum ModAddMode
{
    /// <summary>Import <c>.zip</c> archives via the file picker (default).</summary>
    Zip,

    /// <summary>Import mod folders via the folder picker.</summary>
    Folder,
}

/// <summary>
/// Owns the active profile's mod list, the dominant content area of the app
/// shell. Loads the profile's mods (joined with source + version from the mod
/// repository for the badge), and applies every edit through
/// <see cref="IProfileService"/>: enable/disable, reorder (up/down), per-mod
/// policy (Latest / Pinned), remove (confirmed), auto-sort (identity stub), and
/// the add flow (file picker + drag-and-drop) via <see cref="IModImportService"/>
/// + the per-mod import modal.
/// </summary>
/// <remarks>
/// <para><b>Active profile is the session's:</b> the list never decides the active
/// id; it reads <see cref="IProfileSession.ActiveProfileId"/> and reloads when it
/// changes. No active profile yields an empty list + the "no profile" empty state
/// (owned here, not the shell).</para>
/// <para><b>Rows carry state only:</b> each row is a <see cref="ModItemViewModel"/>
/// (container id + name + source badge + enabled + order + policy + policy-edit
/// state). All service calls live here; the view routes row interactions (toggle,
/// move, policy, remove) through code-behind handlers calling these commands with
/// the row as the parameter (the established <c>ManageProfilesWindow</c> pattern).</para>
/// <para><b>The join key is <see cref="ModContainer.Id"/></b> (the profile entry's
/// identity): on reload, each entry's container is looked up via
/// <see cref="IModRepository.Get"/> for the display name, source badge, and
/// resolved version. A missing container yields a <see cref="UntrackedSource"/> +
/// a "not found" badge (staging warns at launch).</para>
/// <para><b>Edits are allowed while the game runs:</b> the list is the active
/// profile's config, not the running game's. The active profile is already locked
/// against switching by the shell, so the list stays put while the game runs and
/// edits land on the profile the user will launch next.</para>
/// <para><b>Localized text is live:</b> the header count + empty-state messages
/// re-resolve from <see cref="LocalizationService"/> on a culture change, and each
/// row's badge + policy text refresh too (via <see cref="ModItemViewModel.Refresh"/>).</para>
/// <para><b>Add flow:</b> the Add split button (zip picker + folder picker) +
/// drag-and-drop all reduce to <see cref="AddModsCommand"/>, which processes
/// paths sequentially: one import modal per path, then
/// <c>IModImportService.Import</c> (extract/copy into the repository, returning
/// the container id) + <c>IProfileService.AddMod</c> (the profile reference). A
/// cancelled modal cancels the whole remaining batch.</para>
/// </remarks>
public partial class ModListViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IModRepository _repo;
    private readonly IModImportService _importService;
    private readonly IModOrderResolver _orderResolver;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly ILogger<ModListViewModel> _logger;

    /// <summary>
    /// Creates the list VM, subscribes to the session (reload on active-profile
    /// change) + localization (culture refresh), and loads the current profile's
    /// mods.
    /// </summary>
    public ModListViewModel(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModImportService importService,
        IModOrderResolver orderResolver,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<ModListViewModel> logger)
    {
        _profiles = profiles;
        _session = session;
        _repo = repo;
        _importService = importService;
        _orderResolver = orderResolver;
        _dialogs = dialogs;
        _localization = localization;
        _logger = logger;

        _session.PropertyChanged += OnSessionPropertyChanged;
        _localization.PropertyChanged += OnCultureChanged;

        Reload();
    }

    /// <summary>The active profile's mod rows, in load order (lower first).</summary>
    public ObservableCollection<ModItemViewModel> Mods { get; } = new();

    /// <summary>
    /// Whether a profile is active. Drives the header + the "no profile" empty
    /// state (owned here, not the shell).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderCountText))]
    [NotifyPropertyChangedFor(nameof(IsEmptyNoMods))]
    private bool _hasActiveProfile;

    /// <summary>Whether the active profile has at least one mod (drives the
    /// "no mods yet" empty state).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderCountText))]
    [NotifyPropertyChangedFor(nameof(IsEmptyNoMods))]
    private bool _hasMods;

    /// <summary>
    /// Whether the "no mods yet" empty state should show: an active profile with
    /// zero mods. A dedicated derived property because the view cannot express the
    /// conjunction in a single Avalonia compiled binding.
    /// </summary>
    public bool IsEmptyNoMods => HasActiveProfile && !HasMods;

    /// <summary>
    /// The auto-sort toggle state. Turning it on applies the
    /// <see cref="IModOrderResolver"/> once (the identity stub is a no-op). Held
    /// in-memory only for v1 (not persisted): the real dependency-driven resolver
    /// lands later, and the toggle reflects "apply once" intent.
    /// </summary>
    [ObservableProperty]
    private bool _autoSortEnabled;

    /// <summary>
    /// The Add split button's current mode (which picker the primary click
    /// opens). Defaults to <see cref="ModAddMode.Zip"/>. The view sets this from
    /// the split button's flyout items + main click (public setter); the
    /// <see cref="AddModeLabel"/> derived string tracks it so the button reads
    /// "Add Mod (zip)" / "Add Mod (folder)" per the current mode.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddModeLabel))]
    private ModAddMode _addMode = ModAddMode.Zip;

    /// <summary>
    /// The localized split-button label for the current <see cref="AddMode"/>
    /// (mirrors the operator's mock: "Add Mod (zip)" / "Add Mod (folder)").
    /// Re-fires on a culture change (live-refresh with the rest of the UI).
    /// </summary>
    public string AddModeLabel =>
        AddMode == ModAddMode.Folder
            ? _localization["ModList_AddFolder"]
            : _localization["ModList_AddZip"];

    /// <summary>
    /// The localized header count text: "Mods ({n})" with the current mod count,
    /// or the neutral header when no profile is active.
    /// </summary>
    public string HeaderCountText =>
        HasActiveProfile
            ? _localization.Format("ModList_Count", Mods.Count)
            : _localization["ModList_Header"];

    /// <summary>The localized empty-state message for the no-profile case.</summary>
    public string EmptyNoProfileText => _localization["ModList_EmptyNoProfile"];

    /// <summary>The localized empty-state message for the no-mods case.</summary>
    public string EmptyNoModsText => _localization["ModList_EmptyNoMods"];

    /// <summary>
    /// Session-driven reload: the active id changed (dropdown switch, create,
    /// delete-of-active). Rebuilds the list from the new profile. Running-state
    /// changes do not trigger a reload (the list stays put; edits are allowed
    /// while the game runs).
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IProfileSession.ActiveProfileId))
        {
            Reload();
        }
    }

    /// <summary>
    /// The UI culture flipped (Preferences dialog). Re-fire the localized derived
    /// strings + refresh each row's badge + policy text.
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.Culture)
            && e.PropertyName != "Item[]")
        {
            return;
        }

        OnPropertyChanged(nameof(HeaderCountText));
        OnPropertyChanged(nameof(EmptyNoProfileText));
        OnPropertyChanged(nameof(EmptyNoModsText));
        OnPropertyChanged(nameof(AddModeLabel));
        foreach (var row in Mods)
        {
            row.Refresh();
        }
    }

    /// <summary>
    /// Rebuilds <see cref="Mods"/> from the active profile. Each row's source +
    /// version are joined from the repository (by container id); a missing
    /// container yields a <see cref="UntrackedSource"/> + a "not found" badge
    /// (staging warns at launch). Rows are sorted by <see cref="ModListEntry.Order"/>.
    /// No active profile clears the list + sets the empty state.
    /// </summary>
    private void Reload()
    {
        var activeId = _session.ActiveProfileId;
        Mods.Clear();

        if (activeId is not Guid id)
        {
            HasActiveProfile = false;
            HasMods = false;
            return;
        }

        HasActiveProfile = true;

        var entries = _profiles.GetModList(id);
        foreach (var entry in entries.OrderBy(e => e.Order, Comparer<int>.Default))
        {
            var container = _repo.Get(entry.ContainerId);
            var found = container is not null;
            var source = container?.Source ?? new UntrackedSource();
            // The displayed version is the resolved one (Latest -> isLatest;
            // Pinned(id) -> the matching version's tag). An orphan pin (an id
            // with no matching version in the container) yields empty rather than
            // surfacing the opaque id; the dropdown exposes the container's
            // versions for re-pinning.
            var version = ResolveDisplayVersion(entry, container);
            Mods.Add(new ModItemViewModel(
                _localization,
                entry.ContainerId,
                container?.Name ?? string.Empty,
                source,
                version,
                entry.Enabled,
                entry.Order,
                entry.Policy,
                container?.Versions ?? Array.Empty<ModVersion>(),
                found));
        }

        HasMods = Mods.Count > 0;
    }

    /// <summary>
    /// The version string shown in the badge for an entry: the resolved
    /// version's tag when the container + a matching version exist; empty
    /// otherwise (an orphan pin surfaces no readable tag, since the pin is an
    /// opaque id whose version is not present in the container).
    /// </summary>
    private static string ResolveDisplayVersion(ModListEntry entry, ModContainer? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        return container.ResolveVersion(entry.Policy)?.VersionString ?? string.Empty;
    }

    // ---- enable / disable --------------------------------------------------

    /// <summary>
    /// Applies a row's enabled toggle through <see cref="IProfileService.SetModEnabled"/>.
    /// The row's <see cref="ModItemViewModel.Enabled"/> is already two-way bound
    /// (the CheckBox flipped it); this persists it. Defense: no-op with no active
    /// profile.
    /// </summary>
    [RelayCommand]
    private void ToggleEnabled(ModItemViewModel? row)
    {
        if (row is null || _session.ActiveProfileId is not Guid id)
        {
            return;
        }

        _profiles.SetModEnabled(id, row.ContainerId, row.Enabled);
        _logger.LogDebug("Toggled {Container} enabled={Enabled}", row.ContainerId, row.Enabled);
    }

    // ---- reorder (up / down) -----------------------------------------------

    /// <summary>
    /// Moves a row up one position: swaps with its predecessor in <see cref="Mods"/>,
    /// persists the new container-id order through <see cref="IProfileService.SetModOrder"/>,
    /// then reloads (so the persisted <see cref="ModListEntry.Order"/> fields drive
    /// the display). No-op at the top or with no active profile.
    /// </summary>
    [RelayCommand]
    private void MoveUp(ModItemViewModel? row) => Move(row, -1);

    /// <summary>
    /// Moves a row down one position (symmetric to <see cref="MoveUp"/>). No-op at
    /// the bottom or with no active profile.
    /// </summary>
    [RelayCommand]
    private void MoveDown(ModItemViewModel? row) => Move(row, +1);

    private void Move(ModItemViewModel? row, int delta)
    {
        if (row is null || _session.ActiveProfileId is not Guid id)
        {
            return;
        }

        var from = Mods.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Mods.Count)
        {
            return;
        }

        var ids = Mods.Select(m => m.ContainerId).ToArray();
        (ids[from], ids[to]) = (ids[to], ids[from]);

        _profiles.SetModOrder(id, ids);
        Reload();
    }

    // ---- per-mod policy ----------------------------------------------------

    /// <summary>
    /// Switches a row's policy to <see cref="ModVersionPolicy.Latest"/> via
    /// <see cref="IProfileService.SetModPolicy"/>, then reloads.
    /// </summary>
    [RelayCommand]
    private void SetPolicyLatest(ModItemViewModel? row) =>
        ApplyPolicy(row, ModVersionPolicy.Latest);

    /// <summary>
    /// Switches a row's policy to <see cref="PinnedPolicy"/> with the row's
    /// selected dropdown version id, via <see cref="IProfileService.SetModPolicy"/>,
    /// then reloads. The dropdown guarantees the id exists in the container (it
    /// is built from the container's version list), so the call satisfies
    /// <see cref="IProfileService"/>'s orphan-id validation. A <c>null</c>
    /// selection (a version-less container) is a no-op: such a container cannot
    /// be pinned.
    /// </summary>
    [RelayCommand]
    private void SetPolicyPinned(ModItemViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        // The dropdown guarantees the id exists in the container. A null
        // selection means the container has no versions to pin to: no-op (the
        // policy ComboBox reset is handled on the next genuine change).
        if (row.SelectedVersion is null)
        {
            return;
        }

        ApplyPolicy(row, new PinnedPolicy(row.SelectedVersion.VersionId));
    }

    private void ApplyPolicy(ModItemViewModel? row, ModVersionPolicy policy)
    {
        if (row is null || _session.ActiveProfileId is not Guid id)
        {
            return;
        }

        _profiles.SetModPolicy(id, row.ContainerId, policy);
        Reload();
        _logger.LogDebug("Set policy {Policy} on container {Container}", policy, row.ContainerId);
    }

    // ---- remove (confirmed) ------------------------------------------------

    /// <summary>
    /// Removes a row from the profile after a confirmation (the user-facing
    /// "remove from this list" gate). The repository copy survives
    /// (<c>RemoveMod</c> drops only the profile-local reference); the confirm is
    /// about the profile edit, not data loss. No-op with no active profile.
    /// </summary>
    [RelayCommand]
    private async Task Remove(ModItemViewModel? row)
    {
        if (row is null || _session.ActiveProfileId is not Guid id)
        {
            return;
        }

        var title = _localization["RemoveMod_Title"];
        var message = _localization.Format("RemoveMod_Message", row.Name);
        if (!await _dialogs.ConfirmAsync(title, message))
        {
            return;
        }

        _profiles.RemoveMod(id, row.ContainerId);
        Reload();
        _logger.LogInformation("Removed container {Container} from profile {Id}", row.ContainerId, id);
    }

    // ---- auto-sort (identity stub) -----------------------------------------

    /// <summary>
    /// Applies the <see cref="IModOrderResolver"/> to the current list + persists
    /// the resolved order. With the identity stub this is a no-op (order
    /// unchanged); the real dependency-driven resolver drops in later without a
    /// UI change. <see cref="AutoSortEnabled"/> reflects the toggle state.
    /// </summary>
    [RelayCommand]
    private void AutoSort()
    {
        if (_session.ActiveProfileId is not Guid id || Mods.Count == 0)
        {
            return;
        }

        var entries = _profiles.GetModList(id);
        var order = _orderResolver.ResolveOrder(entries);
        _profiles.SetModOrder(id, order);
        Reload();
        _logger.LogDebug("Auto-sorted via {Resolver}", _orderResolver.GetType().Name);
    }

    // ---- add (picker + drag-and-drop) --------------------------------------

    /// <summary>
    /// Processes a list of local paths (folders or <c>.zip</c> archives) from the
    /// add flow: one import modal per path, sequentially, then
    /// <see cref="IModImportService.Import"/> (extract / copy into the repository)
    /// + <see cref="IProfileService.AddMod"/> (the profile reference). A cancelled
    /// modal cancels the whole remaining batch (mods already imported earlier in
    /// the batch stay imported). Used by the Add split button (the zip file
    /// picker + the folder picker) + the drop handler.
    /// </summary>
    /// <remarks>
    /// The name is derived from each path (folder name or archive stem, no
    /// extension) and pre-filled in the modal; the user may rename at import (the
    /// edited name becomes the container's display name + the untracked dedup
    /// key). The import happens before the profile reference is added (order
    /// matters: import the repository copy, then reference it). The new profile
    /// entry defaults to <see cref="ModVersionPolicy.Latest"/>.
    /// </remarks>
    [RelayCommand]
    private async Task AddMods(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        if (_session.ActiveProfileId is not Guid id)
        {
            _logger.LogWarning("Add flow ignored: no active profile");
            return;
        }

        foreach (var path in paths)
        {
            var modName = DeriveModName(path);
            var request = new ImportModRequest(modName, path);
            var result = await _dialogs.ShowImportModAsync(request);
            if (result is null)
            {
                _logger.LogInformation("Add batch cancelled at {Path} (user cancelled the modal)", path);
                break;
            }

            // Import the repository copy first (extract/copy + container/version
            // upsert), then add the profile reference. The canonical name comes
            // from the request (the modal wrote the user's edited + trimmed name
            // back), so a rename at import establishes the container's name.
            var canonicalName = string.IsNullOrWhiteSpace(request.ModName) ? modName : request.ModName.Trim();
            var (containerId, _) = _importService.Import(path, canonicalName, result.Source, result.Version);
            _profiles.AddMod(id, containerId, ModVersionPolicy.Latest);
            _logger.LogInformation("Imported {Mod} from {Path} (source={Source}, version={Version}) onto container {Container}",
                canonicalName, path, result.Source, result.Version, containerId);
        }

        Reload();
    }

    /// <summary>
    /// Derives the default mod name from a path: the folder name, or the archive
    /// stem (no <c>.zip</c> extension) for a <c>.zip</c>. Falls back to the raw
    /// path when the stem is empty (a defensive edge case).
    /// </summary>
    private static string DeriveModName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        const string zip = ".zip";
        if (name.EndsWith(zip, StringComparison.OrdinalIgnoreCase) && name.Length > zip.Length)
        {
            name = name[..^zip.Length];
        }

        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
