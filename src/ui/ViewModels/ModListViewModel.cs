using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Integrations;
using Modificus.Curator.Profiles;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

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
    /// <summary>
    /// The Darktide Nexus game domain. Fixed: Curator supports only Darktide, so
    /// there is no config key for it (mirrors <c>UpdateCheckService</c> +
    /// <c>ModAcquisitionService</c>).
    /// </summary>
    private const string GameDomain = "warhammer40kdarktide";

    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IModRepository _repo;
    private readonly IModImportService _importService;
    private readonly IModOrderResolver _orderResolver;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly IUpdateCheckService _updateCheck;
    private readonly IModAcquisitionService _acquisition;
    private readonly INexusAuthService _auth;
    private readonly UpdateCheckRunner _updateCheckRunner;
    private readonly ILogger<ModListViewModel> _logger;
    private readonly Action<Action> _invokeOnUi;
    private readonly Action<Action>? _startCountdownTimer;
    private readonly Action? _stopCountdownTimer;

    /// <summary>
    /// Creates the list VM, subscribes to the session (reload on active-profile
    /// change), the update-check service (badge refresh on
    /// <see cref="IUpdateCheckService.CheckCompleted"/>), and localization
    /// (culture refresh), loads the current profile's mods, and reads the Nexus
    /// premium state once (fire-and-forget; flips <see cref="IsPremiumUser"/>
    /// when it lands).
    /// </summary>
    public ModListViewModel(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModImportService importService,
        IModOrderResolver orderResolver,
        IDialogService dialogs,
        LocalizationService localization,
        IUpdateCheckService updateCheck,
        IModAcquisitionService acquisition,
        INexusAuthService auth,
        UpdateCheckRunner updateCheckRunner,
        Action<Action> invokeOnUi,
        ILogger<ModListViewModel> logger,
        Action<Action>? startCountdownTimer = null,
        Action? stopCountdownTimer = null)
    {
        _profiles = profiles;
        _session = session;
        _repo = repo;
        _importService = importService;
        _orderResolver = orderResolver;
        _dialogs = dialogs;
        _localization = localization;
        _updateCheck = updateCheck;
        _acquisition = acquisition;
        _auth = auth;
        _updateCheckRunner = updateCheckRunner;
        _logger = logger;
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _startCountdownTimer = startCountdownTimer;
        _stopCountdownTimer = stopCountdownTimer;

        _session.PropertyChanged += OnSessionPropertyChanged;
        _localization.PropertyChanged += OnCultureChanged;
        _updateCheck.CheckCompleted += OnUpdateCheckCompleted;

        // The refresh button's tooltip defaults to the normal "check now"
        // string. When the manual sliding-window throttle engages, the countdown
        // owns the tooltip (RefreshManualRefreshThrottle / OnCountdownTick).
        ManualRefreshTooltip = _localization["ModList_CheckNowTooltip"];

        // Read the Nexus premium state once at construction. Fire-and-forget:
        // GetCurrentStateAsync hits the network, so blocking the (UI-thread)
        // constructor on it would stall app startup. The result lands quickly
        // (sub-second typically) and flips IsPremiumUser; until then the Update
        // buttons stay hidden (also gated on an update-check result, which takes
        // longer). No mid-session refresh by design (re-checking on Integrations
        // dialog close would burn an API call each time; a user signing in
        // mid-session needing a restart for the buttons to appear is acceptable).
        _ = LoadPremiumStateAsync();

        Reload();
    }

    /// <summary>The active profile's mod rows, in load order (lower first).</summary>
    public ObservableCollection<ModItemViewModel> Mods { get; } = new();

    /// <summary>
    /// Whether a profile is active. Drives the header + the "no profile" empty
    /// state (owned here, not the shell).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyNoMods))]
    private bool _hasActiveProfile;

    /// <summary>Whether the active profile has at least one mod (drives the
    /// "no mods yet" empty state).</summary>
    [ObservableProperty]
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
    /// Whether the Nexus account is premium. Read once at construction (see the
    /// constructor's premium-read note); no mid-session refresh. Drives the
    /// per-row Update button's visibility (premium-only, hidden not nagged for
    /// non-premium). False until the read lands (or on a read failure; a
    /// restart re-reads).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateEnabled))]
    private bool _isPremiumUser;

    /// <summary>
    /// Whether the last update check was rate-limited. Drives the header
    /// rate-limit notice (the "check incomplete" indicator). Set from
    /// <see cref="IUpdateCheckService.LastResult"/> on reload + on
    /// <see cref="IUpdateCheckService.CheckCompleted"/>. Takes precedence over
    /// <see cref="IsRecentOnly"/> in the derived <see cref="ShowRecentOnlyNotice"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecentOnlyNotice))]
    private bool _isRateLimited;

    /// <summary>
    /// Whether the last update check was Month-only (NOT thorough). Drives the
    /// "showing recent updates" notice: a Month-only check completed but did
    /// not do the per-mod pass, so the badges may not reflect every available
    /// update. Set from <see cref="IUpdateCheckService.LastResult"/> on reload +
    /// on <see cref="IUpdateCheckService.CheckCompleted"/>: true when the result
    /// is non-null, not rate-limited, and not thorough. Cleared after a
    /// thorough check. Suppressed while <see cref="IsRateLimited"/> is set (the
    /// rate-limit notice takes precedence via <see cref="ShowRecentOnlyNotice"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecentOnlyNotice))]
    private bool _isRecentOnly;

    /// <summary>
    /// Whether any row is currently running a one-click update. True while the
    /// async <see cref="UpdateCommand"/> is in flight; disables every row's
    /// Update button (one update at a time). Re-enabled in the command's finally
    /// block on success or failure (no stuck state).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateEnabled))]
    private bool _anyRowUpdating;

    /// <summary>
    /// Whether the manual "check now" affordance is running a thorough check.
    /// True while <see cref="CheckForUpdatesNowCommand"/> awaits the runner's
    /// thorough check (multiple API calls, a few seconds); drives the header
    /// refresh button's enabled + spinner state. Cleared in the command's
    /// finally block on success or failure (no stuck state).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRefreshEnabled))]
    private bool _isCheckingNow;

    /// <summary>
    /// Whether the manual sliding-window throttle is currently blocking the
    /// refresh button (the runner's free 10/hour budget is spent + the 2-minute
    /// cooldown has not elapsed). Set by <see cref="RefreshManualRefreshThrottle"/>
    /// after each manual attempt + re-evaluated on each countdown tick. Drives
    /// <see cref="IsRefreshEnabled"/> (the button disables) + the countdown
    /// tooltip (<see cref="ManualRefreshTooltip"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRefreshEnabled))]
    private bool _isManualRefreshThrottled;

    /// <summary>
    /// The refresh button's tooltip. The normal "Check for updates now" string
    /// when not throttled; the live countdown string ("Rate limiting protection
    /// enabled. Manual refresh will be available again in {m:ss}.") while
    /// throttled. Updated each second by the countdown tick. Bound to the
    /// button's <c>ToolTip.Tip</c>.
    /// </summary>
    [ObservableProperty]
    private string _manualRefreshTooltip;

    /// <summary>
    /// Whether the per-row Update buttons are enabled: the user is premium AND
    /// no other update is in flight. The row's IsVisible gating already hides
    /// the button for non-premium; this is the IsEnabled half (one update at a
    /// time). A single computed property so the view binds IsEnabled without
    /// negating a deep path across a parent walk (compiled-binding limitation).
    /// </summary>
    public bool IsUpdateEnabled => IsPremiumUser && !AnyRowUpdating;

    /// <summary>
    /// Whether the manual "check now" refresh button is enabled: NOT while a
    /// thorough check is in flight (<see cref="IsCheckingNow"/>) AND NOT while
    /// the manual sliding-window throttle is blocking (<see cref="IsManualRefreshThrottled"/>).
    /// A single computed property so the view binds the button's IsEnabled to
    /// one source (mirrors <see cref="IsUpdateEnabled"/>); its dependencies each
    /// carry <c>[NotifyPropertyChangedFor(nameof(IsRefreshEnabled))]</c> so the
    /// binding re-evaluates when either flips.
    /// </summary>
    public bool IsRefreshEnabled => !IsCheckingNow && !IsManualRefreshThrottled;

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
    /// The localized header label: "Mods". Shown for both the active-profile +
    /// no-profile states (the per-profile mod count was removed; the row list
    /// itself is the count). Re-fires on a culture change.
    /// </summary>
    public string HeaderCountText => _localization["ModList_Header"];

    /// <summary>The localized empty-state message for the no-profile case.</summary>
    public string EmptyNoProfileText => _localization["ModList_EmptyNoProfile"];

    /// <summary>The localized empty-state message for the no-mods case.</summary>
    public string EmptyNoModsText => _localization["ModList_EmptyNoMods"];

    /// <summary>
    /// The localized rate-limit notice text shown in the header when
    /// <see cref="IsRateLimited"/> is true. Re-fires on a culture change.
    /// </summary>
    public string RateLimitedNoticeText => _localization["ModList_RateLimited"];

    /// <summary>
    /// The localized "recent updates only" notice text shown in the header when
    /// <see cref="ShowRecentOnlyNotice"/> is true (a Month-only check landed
    /// without a thorough pass). Re-fires on a culture change.
    /// </summary>
    public string RecentOnlyNoticeText => _localization["ModList_RecentOnly"];

    /// <summary>
    /// Whether the "recent updates only" notice should show: the last check was
    /// Month-only (NOT thorough) AND not rate-limited (the rate-limit notice
    /// takes precedence). The view binds the notice's <c>IsVisible</c> here so
    /// the precedence rule stays in the VM (one source of truth).
    /// </summary>
    public bool ShowRecentOnlyNotice => !IsRateLimited && IsRecentOnly;

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
        OnPropertyChanged(nameof(RateLimitedNoticeText));
        OnPropertyChanged(nameof(RecentOnlyNoticeText));
        // When not throttled, the refresh tooltip re-resolves to the normal
        // resx string (the culture changed, so the localized value changed).
        // When throttled, the countdown tick owns the tooltip and re-renders it
        // with the new culture's throttle string on the next tick.
        if (!IsManualRefreshThrottled)
        {
            ManualRefreshTooltip = _localization["ModList_CheckNowTooltip"];
        }
        foreach (var row in Mods)
        {
            row.Refresh();
        }
    }

    /// <summary>
    /// The update check finished (background task fires the event on its
    /// completing thread). Re-apply <see cref="IUpdateCheckService.LastResult"/>
    /// to the rows + the list-level rate-limit flag. Idempotent: safe to call on
    /// every completion, including the no-auth / no-checkable-mods / failure
    /// short-circuits.
    /// </summary>
    private void OnUpdateCheckCompleted(object? sender, UpdateCheckResult? result)
    {
        // The event fires on the check's completing thread (a threadpool thread
        // via UpdateCheckRunner's Task.Run). Marshal to the UI thread so
        // ApplyUpdateCheckResult's iteration of the UI-bound Mods collection
        // doesn't race with a UI-thread Reload (ObservableCollection's
        // enumerator is not thread-safe vs concurrent mutation).
        _invokeOnUi(ApplyUpdateCheckResult);
    }

    /// <summary>
    /// Reads <see cref="IUpdateCheckService.LastResult"/> and applies it to the
    /// list: sets <see cref="IsRateLimited"/> + <see cref="IsRecentOnly"/> +
    /// per-row <see cref="ModItemViewModel.UpdateAvailable"/> (matched by
    /// container id). Called on <see cref="IUpdateCheckService.CheckCompleted"/>
    /// + at the end of <see cref="Reload"/> (so a freshly rebuilt list picks up
    /// the last result without waiting for the next check).
    /// </summary>
    private void ApplyUpdateCheckResult()
    {
        var result = _updateCheck.LastResult;
        IsRateLimited = result?.RateLimited == true;
        // The "recent updates only" notice fires when a Month-only (non-thorough)
        // check landed without being rate-limited. Suppressed before the first
        // check (null result) + after a thorough check (the operator's "click
        // refresh for a complete check" affordance clears it).
        IsRecentOnly = result is not null && !result.RateLimited && !result.Thorough;

        if (Mods.Count == 0)
        {
            return;
        }

        // Index the flagged container ids once for an O(1) per-row lookup (the
        // result is shared across every row; a LINQ Any per row would re-scan).
        var flagged = result?.Updates;
        if (flagged is null || flagged.Count == 0)
        {
            foreach (var row in Mods)
            {
                row.UpdateAvailable = false;
            }
            return;
        }

        var flaggedIds = flagged.Select(u => u.ContainerId).ToHashSet();
        foreach (var row in Mods)
        {
            row.UpdateAvailable = flaggedIds.Contains(row.ContainerId);
        }
    }

    /// <summary>
    /// Reads the Nexus premium state once (called fire-and-forget from the
    /// constructor). On success flips <see cref="IsPremiumUser"/>; on failure
    /// logs + leaves it false (a restart re-reads; the operator accepted this
    /// edge case over burning API calls on every Integrations-dialog close).
    /// </summary>
    private async Task LoadPremiumStateAsync()
    {
        try
        {
            var state = await _auth.GetCurrentStateAsync();
            IsPremiumUser = state?.IsPremium == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Nexus premium state read failed; per-mod Update buttons stay hidden until restart.");
        }
    }

    /// <summary>
    /// Rebuilds <see cref="Mods"/> from the active profile. Each row's source +
    /// version are joined from the repository (by container id); a missing
    /// container yields a <see cref="UntrackedSource"/> + a "not found" badge
    /// (staging warns at launch). Rows are sorted by <see cref="ModListEntry.Order"/>.
    /// No active profile clears the list + sets the empty state.
    /// </summary>
    /// <remarks>
    /// Called on construction + on an active-profile change (session-driven), and
    /// also by the shell after the Settings dialog closes: a Settings relocate
    /// rescans the repository's index out-of-band, so the <see cref="Mods"/>
    /// snapshot is stale until this reloads it.
    /// </remarks>
    public void Reload()
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

        // The freshly built rows default UpdateAvailable=false; re-apply the
        // last check result so a profile switch (or a post-edit reload) reflects
        // the most recent check without waiting for the next one.
        ApplyUpdateCheckResult();
    }

    /// <summary>
    /// Reloads the mod list and clears the update flag for the specified
    /// container. Called after an nxm install/reinstall: the stale
    /// <see cref="IUpdateCheckService.LastResult"/> (computed before the
    /// version change) would re-apply the flag via
    /// <see cref="ApplyUpdateCheckResult"/>, so this overrides it for instant
    /// UX. The next automatic or manual check reconciles the flag (the pin
    /// was cleared by <c>AddVersion</c>, so the mod is re-evaluated).
    /// </summary>
    public void ReloadAndClearUpdateFlag(Guid containerId)
    {
        Reload();
        var row = Mods.FirstOrDefault(m => m.ContainerId == containerId);
        if (row is not null)
        {
            row.UpdateAvailable = false;
        }
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

    // ---- per-mod update (one at a time, premium-only) ----------------------

    /// <summary>
    /// The manual "check for updates now" trigger (the mod-list header refresh
    /// button). Routes through <see cref="Session.UpdateCheckRunner.CheckNowAsync"/>
    /// so the runner stays the single owner of "fire a check" logic + uses the
    /// thorough path (<see cref="IUpdateCheckService.CheckThoroughAsync"/>) that
    /// also catches mods outside the Month window. Awaits the runner's task so
    /// <see cref="IsCheckingNow"/> can drive the button's spinner + disable for
    /// the duration (a few seconds for the per-mod pass). <see cref="IsCheckingNow"/>
    /// is set before the await + cleared in the finally block on success or
    /// failure (no stuck state). The existing
    /// <see cref="IUpdateCheckService.CheckCompleted"/> subscription re-applies
    /// the result to the rows when it lands. The command is a no-op (via the
    /// runner) when no profile is active; a second click while one is in flight
    /// is a no-op (the <see cref="IsCheckingNow"/> guard). After the await,
    /// <see cref="RefreshManualRefreshThrottle"/> re-evaluates the runner's
    /// sliding-window throttle so the button disables + the countdown tooltip
    /// engages when the manual path is rate-limited.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesNow()
    {
        // Re-entrancy guard: a second click while a thorough check is running is
        // a no-op (the button's IsEnabled is also bound to IsRefreshEnabled, but
        // the guard makes a programmatic call safe too).
        if (IsCheckingNow)
        {
            return;
        }

        IsCheckingNow = true;
        try
        {
            // No ConfigureAwait(false): the finally (clearing IsCheckingNow)
            // should run on the UI thread so the bound control re-enables
            // synchronously. The runner's Task.Run dispatches the actual check
            // to a thread-pool task; we only await its completion here.
            await _updateCheckRunner.CheckNowAsync();
            // Re-evaluate the manual throttle after every attempt (a fire that
            // spent the free budget engages the countdown; a blocked attempt
            // also lands here as CompletedTask with the throttle active). Stays
            // on the UI thread (no ConfigureAwait above).
            RefreshManualRefreshThrottle();
        }
        finally
        {
            IsCheckingNow = false;
        }
    }

    // ---- manual-refresh throttle (countdown tooltip + disabled button) -------

    /// <summary>
    /// Re-reads <see cref="UpdateCheckRunner.NextManualRefreshAllowedAt"/> and
    /// applies the throttle state to <see cref="IsManualRefreshThrottled"/>,
    /// <see cref="ManualRefreshTooltip"/>, and the countdown timer. Called after
    /// every <c>CheckForUpdatesNow</c> attempt. When throttled, starts the
    /// 1-second countdown timer (which re-evaluates via <see cref="OnCountdownTick"/>
    /// until the cooldown elapses). When not throttled, stops the timer and
    /// restores the normal tooltip.
    /// </summary>
    private void RefreshManualRefreshThrottle()
    {
        var next = _updateCheckRunner.NextManualRefreshAllowedAt;
        if (next is null)
        {
            IsManualRefreshThrottled = false;
            _stopCountdownTimer?.Invoke();
            ManualRefreshTooltip = _localization["ModList_CheckNowTooltip"];
            return;
        }

        IsManualRefreshThrottled = true;
        // Production's start delegate is idempotent (no-op if already running),
        // so invoking it on every throttled re-eval is safe.
        _startCountdownTimer?.Invoke(OnCountdownTick);
        ManualRefreshTooltip = BuildThrottleTooltip(next.Value);
    }

    /// <summary>
    /// The 1-second countdown tick callback (production wires a
    /// <c>DispatcherTimer</c>; tests invoke the captured callback directly).
    /// Re-reads <see cref="UpdateCheckRunner.NextManualRefreshAllowedAt"/>: when
    /// the cooldown has elapsed (null), clears the throttle, stops the timer,
    /// and restores the normal tooltip; otherwise recomputes the remaining time
    /// and updates the tooltip.
    /// </summary>
    private void OnCountdownTick()
    {
        var next = _updateCheckRunner.NextManualRefreshAllowedAt;
        if (next is null)
        {
            IsManualRefreshThrottled = false;
            _stopCountdownTimer?.Invoke();
            ManualRefreshTooltip = _localization["ModList_CheckNowTooltip"];
            return;
        }

        ManualRefreshTooltip = BuildThrottleTooltip(next.Value);
    }

    /// <summary>
    /// Formats the throttle tooltip from the absolute unlock instant: resolves
    /// the localized throttle template with the remaining time formatted as
    /// <c>m:ss</c>. The remaining time is a COSMETIC computation for the tooltip
    /// only (the throttle enforcement is entirely in the runner, testable via
    /// the injected clock), so this uses <see cref="DateTimeOffset.UtcNow"/>
    /// directly and keeps the VM clock-free.
    /// </summary>
    private string BuildThrottleTooltip(DateTimeOffset nextAllowedAt)
    {
        var remaining = nextAllowedAt - DateTimeOffset.UtcNow;
        return _localization.Format("ModList_ManualRefreshThrottled", FormatRemaining(remaining));
    }

    /// <summary>
    /// Formats a remaining <see cref="TimeSpan"/> as <c>m:ss</c> (e.g.
    /// <c>1:30</c>, <c>0:05</c>, <c>2:00</c>). Clamps negative values (a tick
    /// landing a hair past the unlock instant) to zero so the tooltip never
    /// shows a negative. Pure + culture-free (manual integer math, no
    /// format-string separators), so it is independently unit-testable.
    /// </summary>
    internal static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }
        var totalSeconds = (int)remaining.TotalSeconds;
        return $"{totalSeconds / 60}:{totalSeconds % 60:D2}";
    }

    /// <summary>
    /// Runs the one-click per-mod update: re-downloads the mod's latest MAIN
    /// release via <see cref="IModAcquisitionService.AcquireLatestNexusAsync"/>
    /// (the auth-only / premium path), then reloads so the new version shows +
    /// the marker clears (the new ImportedAt is now, so the next check will not
    /// re-flag it). Premium-only (hidden, not nagged, for non-premium). One at a
    /// time: a second invocation while <see cref="AnyRowUpdating"/> is a no-op.
    /// </summary>
    /// <remarks>
    /// <para><b>Defense.</b> No-op when: there is no active profile; another
    /// update is in flight (<see cref="AnyRowUpdating"/>); the row is not
    /// Nexus+Latest (<see cref="ModItemViewModel.IsNexusLatest"/>); no update is
    /// flagged (<see cref="ModItemViewModel.UpdateAvailable"/>); the user is not
    /// premium (<see cref="IsPremiumUser"/>); or the row has no
    /// <see cref="ModItemViewModel.NexusModId"/>.</para>
    /// <para><b>Transactional extraction.</b> The mod repository's
    /// <c>AddVersion</c> extracts into a sibling temp + atomically swaps on
    /// success, so a mid-update failure leaves the existing version intact (the
    /// user keeps the version they had). On failure the command surfaces a
    /// user-facing alert; on success (or failure) the finally block clears
    /// <see cref="ModItemViewModel.IsUpdating"/> + <see cref="AnyRowUpdating"/>
    /// so the row's other controls re-enable.</para>
    /// </remarks>
    [RelayCommand]
    private async Task Update(ModItemViewModel? row)
    {
        if (row is null || _session.ActiveProfileId is not Guid)
        {
            return;
        }

        // One at a time: a second click while another update is in flight is a
        // no-op. Checked here (not via the button's IsEnabled) so a programmatic
        // call is also gated.
        if (AnyRowUpdating)
        {
            return;
        }

        // Defense: the button is only visible+enabled under these conditions,
        // but the command is the source of truth (a programmatic caller, a test,
        // or a future keystroke could bypass the view's gating).
        if (!IsPremiumUser || !row.IsNexusLatest || !row.UpdateAvailable || row.NexusModId is not int modId)
        {
            return;
        }

        AnyRowUpdating = true;
        row.IsUpdating = true;
        try
        {
            // No ConfigureAwait(false): the continuation must stay on the UI
            // thread so Reload (mutates the UI-bound Mods collection) + the
            // failure-path ShowAlertAsync below run on the UI thread. Matches
            // Remove + AddMods in this file; the NxmModDownloadHandler marshals
            // explicitly via an _invokeOnUi seam instead, but this file's
            // convention is to stay on the captured UI context.
            await _acquisition.AcquireLatestNexusAsync(GameDomain, modId);

            // Reload so the new version (its IsLatest flip + the fresh
            // ImportedAt) shows. Then clear the marker for this row immediately:
            // the stale LastResult (from before the update) still flags this
            // container, so ApplyUpdateCheckResult (called inside Reload)
            // re-set UpdateAvailable=true. Override it here for instant UX, then
            // fire a fresh check so the stale LastResult is replaced (otherwise
            // the next edit's Reload would re-apply the stale result + flicker
            // the marker back). The fresh check won't re-flag this mod: its
            // RemoteUploadedAt is now the latest file's upload date, so
            // LatestFileUpdateUtc > RemoteUploadedAt is false.
            Reload();
            var updated = Mods.FirstOrDefault(m => m.ContainerId == row.ContainerId);
            if (updated is not null)
            {
                updated.UpdateAvailable = false;
            }

            if (_session.ActiveProfileId is Guid checkId)
            {
                _ = _updateCheck.CheckAsync(checkId).ContinueWith(
                    t => _logger.LogError(t.Exception, "Post-update check failed for profile {Profile}.", checkId),
                    TaskContinuationOptions.OnlyOnFaulted);
            }

            _logger.LogInformation("Updated mod {Container} to the latest Nexus release.", row.ContainerId);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a failure: the user (or shutdown) cancelled;
            // no alert. Re-throwing would surface as an unobserved exception on
            // the fire-and-forget AsyncRelayCommand, so swallow instead.
            _logger.LogInformation("Update of mod {Container} was cancelled.", row.ContainerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update of mod {Container} failed.", row.ContainerId);
            await _dialogs.ShowAlertAsync(
                _localization["Update_FailedTitle"],
                _localization.Format("Update_FailedMessage", row.Name) + " " + ex.Message);
        }
        finally
        {
            row.IsUpdating = false;
            AnyRowUpdating = false;
        }
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
    /// add flow: one import modal per path, sequentially. Per path the flow is:
    /// <b>(1)</b> peek the base folder name from the source (validates structure,
    /// throws on an invalid source); <b>(2)</b> hard-block a base-name collision
    /// against the active profile (refuse, create nothing, alert); <b>(3)</b>
    /// <see cref="IModImportService.Import"/> (extract / copy into the repository)
    /// + <see cref="IProfileService.AddMod"/> (the profile reference). A cancelled
    /// modal, a failed peek/import, OR a collision cancels the whole remaining
    /// batch (mods imported earlier in the batch stay imported). Used by the Add
    /// split button (the zip file picker + the folder picker) + the drop handler.
    /// </summary>
    /// <remarks>
    /// The name is derived from each path (folder name or archive stem, no
    /// extension) and pre-filled in the modal; the user may rename at import (the
    /// edited name becomes the container's display name + the untracked dedup
    /// key). The import happens before the profile reference is added (order
    /// matters: import the repository copy, then reference it). A re-add of a mod
    /// already in the profile is NOT a collision: the would-be container is
    /// peeked (<see cref="IModImportService.FindExistingContainer"/>) and excluded
    /// from the collision check, so the idempotent <see cref="IProfileService.AddMod"/>
    /// stays a no-op. The new profile entry adopts the modal's chosen policy:
    /// Latest (the default) or Pinned to the version being imported.
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

            var canonicalName = string.IsNullOrWhiteSpace(request.ModName) ? modName : request.ModName.Trim();

            // (1) Peek the base folder name. This validates the source structure
            // (exactly one base dir with a matching <base>.mod) BEFORE any
            // container/version is created. An invalid source throws here; catch
            // it per mod, surface an alert naming the failing source, and abort
            // the remaining batch (the cancel-aborts-batch posture).
            string baseName;
            try
            {
                baseName = _importService.GetBaseName(path);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException
                    or IOException or UnauthorizedAccessException
                    or System.IO.InvalidDataException)
            {
                await AlertImportFailed(path, ex);
                break;
            }

            // (2) Base-name collision hard-block. Two mods with the same base
            // folder name can't coexist in one profile (the loader can't tell
            // them apart). Exclude the container a re-add would dedup to: a
            // re-add resolves to the same container, and AddMod is idempotent on
            // it, so it must NOT be treated as a collision. On a collision, name
            // the conflicting mod + the base folder, then abort the batch
            // (nothing is created: no Import, no AddMod).
            var existing = _importService.FindExistingContainer(result.Source, canonicalName);
            var collision = _profiles.GetBaseNameCollision(id, baseName, existing?.Id);
            if (collision is not null)
            {
                var conflictingName = _repo.Get(collision.ContainerId)?.Name ?? baseName;
                _logger.LogWarning(
                    "Add blocked at {Path}: base folder '{Base}' collides with existing mod '{Conflicting}' (container {Container}) on profile {Id}",
                    path, baseName, conflictingName, collision.ContainerId, id);
                await _dialogs.ShowAlertAsync(
                    _localization["Import_CollisionTitle"],
                    _localization.Format("Import_CollisionMessage", path, baseName, conflictingName));
                break;
            }

            // (3) Import the repository copy (extract/copy + container/version
            // upsert), then add the profile reference. The canonical name comes
            // from the request (the modal wrote the user's edited + trimmed name
            // back), so a rename at import establishes the container's name. A
            // late I/O failure (copy/extract) is caught per mod.
            //
            // Policy: the modal's chosen Latest/Pinned drives the profile entry's
            // initial version policy. Latest tracks the container's newest
            // release (auto-update on re-import); Pinned freezes the entry to
            // exactly the version being imported (constructed from the
            // VersionId the import just minted, not from the modal's empty
            // placeholder).
            Guid containerId;
            string versionId;
            try
            {
                var (importedId, importedVersionId) = _importService.Import(path, canonicalName, result.Source, result.Version);
                containerId = importedId;
                versionId = importedVersionId;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException
                    or IOException or UnauthorizedAccessException
                    or System.IO.InvalidDataException)
            {
                await AlertImportFailed(path, ex);
                break;
            }

            var policy = result.Policy is PinnedPolicy
                ? new PinnedPolicy(versionId)
                : ModVersionPolicy.Latest;
            _profiles.AddMod(id, containerId, policy);
            _logger.LogInformation("Imported {Mod} from {Path} (source={Source}, version={Version}, policy={Policy}) onto container {Container}",
                canonicalName, path, result.Source, result.Version, policy, containerId);
        }

        Reload();
    }

    /// <summary>
    /// Surfaces an import-failure alert for a source path + the underlying
    /// exception, using the localized <c>Import_Failed</c> strings. Logs the
    /// exception with its stack + shows the message text to the user.
    /// </summary>
    private async Task AlertImportFailed(string path, Exception ex)
    {
        _logger.LogError(ex, "Import of {Path} failed", path);
        await _dialogs.ShowAlertAsync(
            _localization["Import_FailedTitle"],
            _localization.Format("Import_FailedMessage", path) + " " + ex.Message);
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
