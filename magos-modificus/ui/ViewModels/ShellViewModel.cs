using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magos.Modificus.EnginseerClient;
using Magos.Modificus.Profiles;
using Magos.Modificus.UI.Dialogs;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.Session;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI.ViewModels;

/// <summary>
/// The view model behind the Magos Modificus main window, the Phase 3 Track A
/// app shell. Milestone 2 makes the profile controls work: the dropdown switches
/// the active profile (the request flows through <see cref="IProfileSession"/>,
/// which owns the active id + persistence), switching is blocked while Darktide
/// runs (the session gates it), and "Manage profiles…" opens a CRUD dialog. The
/// shell owns only the profile-list snapshot + the dropdown selection binding;
/// the <b>session</b> is the single source of truth for the active id, the
/// can-change gate, and the LIVE running-state. Track D adds the Preferences
/// affordance (top-bar gear) + dynamic-language refresh of the status text /
/// tooltips via <see cref="LocalizationService"/>.
/// </summary>
/// <remarks>
/// <para><b>Running-state is live:</b> the shell mirrors <see cref="IsGameRunning"/>
/// from <see cref="IProfileSession.IsRunning"/>, which a polling timer refreshes.
/// So the status strip, launch-availability, and dropdown-enable react within a
/// few seconds of Darktide starting or stopping while Magos is open.</para>
/// <para><b>Localizable text is live:</b> <see cref="GameRunningText"/> and
/// <see cref="ProfileSwitchTooltip"/> re-resolve from <see cref="LocalizationService"/>
/// when the UI culture changes (Preferences dialog), so the shell copy refreshes
/// in-step with the rest of the UI on a language switch.</para>
/// <para>Track C (Launch behavior) and Track B (mod-list contents) are not wired
/// here yet. <see cref="LaunchCommand"/> stays a no-op placeholder whose guard is
/// real so it lights up once a profile is selected and the game is stopped.</para>
/// </remarks>
public partial class ShellViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private readonly IProfileSession _session;
    private readonly IEnginseerLaunchService _launchService;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly ILogger<ShellViewModel> _logger;

    // Guards selection updates while the shell mirrors the session's authoritative
    // active id back into the dropdown, so re-syncing the selection does not
    // re-request an active change (avoids a feedback loop).
    private bool _syncing;

    /// <summary>
    /// Creates the shell VM, loads the profile list, mirrors the session's current
    /// active id + running-state, and subscribes to live running-state changes.
    /// </summary>
    public ShellViewModel(
        IProfileService profiles,
        IProfileSession session,
        IEnginseerLaunchService launchService,
        IDialogService dialogs,
        LocalizationService localization,
        ModListViewModel modList,
        ILogger<ShellViewModel> logger)
    {
        _profileService = profiles;
        _session = session;
        _launchService = launchService;
        _dialogs = dialogs;
        _localization = localization;
        ModList = modList;
        _logger = logger;

        // Set the backing fields directly: no subscribers yet, and setting
        // SelectedProfile through the property would route through the selection
        // handler (which requests an active change) during the initial restore.
        _profiles = _profileService.ListProfiles();
        _isGameRunning = _session.IsRunning;
        _selectedProfile = ResolveActive();

        _session.PropertyChanged += OnSessionPropertyChanged;
        // Re-resolve the localized strings when the UI culture flips so the
        // status strip + tooltip refresh alongside the rest of the UI.
        _localization.PropertyChanged += OnCultureChanged;

        _logger.LogInformation(
            "Shell initialized: {ProfileCount} profile(s) loaded; active={ActiveId}; " +
            "Darktide running: {IsRunning}; launch facade: {LaunchFacade}",
            Profiles.Count,
            _session.ActiveProfileId?.ToString() ?? "(none)",
            IsGameRunning,
            _launchService.GetType().Name);
    }

    /// <summary>
    /// All known profiles, a snapshot of <see cref="IProfileService.ListProfiles"/>,
    /// refreshed after the management dialog closes. Empty pre-release until a
    /// profile is created.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfiles))]
    [NotifyPropertyChangedFor(nameof(CanSwitchProfile))]
    [NotifyPropertyChangedFor(nameof(ProfileSwitchTooltip))]
    private IReadOnlyList<ProfileSummary> _profiles = Array.Empty<ProfileSummary>();

    /// <summary>
    /// Whether at least one profile exists.</summary>
    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>
    /// The active profile's mod-list view model (the dominant content area). A
    /// singleton injected here + bound in <c>MainWindow</c> as
    /// <c>{Binding ModList}</c> onto <c>ModListView</c>. The shell does not touch
    /// the mod list; all mod-list state + edits live on this VM. Exposed so the
    /// view's <c>DataContext</c> binding can reach it.
    /// </summary>
    public ModListViewModel ModList { get; }

    /// <summary>
    /// The currently-selected (active) profile, or <c>null</c>. Bound to the
    /// top-bar dropdown; selecting requests the active change through the session
    /// (which gates it). The shell then re-syncs to the session's authoritative
    /// active id, so a blocked change snaps the dropdown back to the real active.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    private ProfileSummary? _selectedProfile;

    /// <summary>
    /// Whether Darktide is currently running, mirrored LIVE from
    /// <see cref="IProfileSession.IsRunning"/> (a polling timer refreshes it).
    /// Gates profile switching (<see cref="CanSwitchProfile"/>) and launch.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyPropertyChangedFor(nameof(GameRunningText))]
    [NotifyPropertyChangedFor(nameof(CanSwitchProfile))]
    [NotifyPropertyChangedFor(nameof(ProfileSwitchTooltip))]
    private bool _isGameRunning;

    /// <summary>Status-strip label for the game-running indicator (localized).</summary>
    public string GameRunningText =>
        IsGameRunning
            ? _localization["Status_GameRunning"]
            : _localization["Status_GameNotRunning"];

    /// <summary>
    /// Whether the profile dropdown is interactive: a profile must exist and the
    /// game must not be running. The gate itself lives in the session; this just
    /// exposes the running-state so the dropdown can disable while Darktide runs.
    /// </summary>
    public bool CanSwitchProfile => !IsGameRunning && HasProfiles;

    /// <summary>
    /// Tooltip explaining the dropdown's current enabled state (the block reason
    /// when the game is running, or a first-run hint when no profile exists).
    /// Localized + re-resolves on a culture change.
    /// </summary>
    public string ProfileSwitchTooltip =>
        IsGameRunning
            ? _localization["ProfileSwitch_RunningTooltip"]
            : HasProfiles
                ? string.Empty
                : _localization["ProfileSwitch_CreateFirstTooltip"];

    /// <summary>
    /// The dropdown (or a programmatic set) changed the selection. Asks the session
    /// to make it active; the session gates it (only when the game isn't running).
    /// Then re-syncs to the session's authoritative active id so a blocked or
    /// cleared selection reverts, the dropdown never lies about the active profile.
    /// </summary>
    partial void OnSelectedProfileChanged(ProfileSummary? value)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (value is { } profile)
            {
                _session.RequestActive(profile.Id);
            }

            SelectedProfile = ResolveActive();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Mirrors the session's live running-state into <see cref="IsGameRunning"/>
    /// (the status strip, launch-availability, and dropdown-enable all cascade
    /// from it). Active-id changes are handled at the known points they can occur
    /// (dropdown request + after the dialog), not here.
    /// </summary>
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IProfileSession.IsRunning))
        {
            IsGameRunning = _session.IsRunning;
        }
    }

    /// <summary>
    /// The UI culture flipped (Preferences dialog). Re-fire the property-changed
    /// events for the localized derived strings so bindings re-resolve.
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalizationService.Culture)
            && e.PropertyName != "Item[]")
        {
            return;
        }

        OnPropertyChanged(nameof(GameRunningText));
        OnPropertyChanged(nameof(ProfileSwitchTooltip));
    }

    /// <summary>
    /// Opens the "Manage profiles…" dialog, then reloads the profile list and
    /// re-syncs the selection to the session's active id. The dialog applies
    /// active changes live through the session during its session, so by the time
    /// it closes the session already reflects whatever the gate allowed; the shell
    /// just refreshes its list snapshot and follows the authoritative active id.
    /// </summary>
    [RelayCommand]
    private async Task ManageProfiles()
    {
        await _dialogs.ShowManageProfilesAsync();

        Profiles = _profileService.ListProfiles();

        _syncing = true;
        try
        {
            SelectedProfile = ResolveActive();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Opens the Preferences dialog (theme / font scale / language). Each
    /// preference applies + persists immediately through the dialog, so on
    /// return the shell only needs to let its localized bindings refresh (which
    /// the culture-changed subscription handles for a language switch).
    /// </summary>
    [RelayCommand]
    private async Task OpenPreferences()
    {
        await _dialogs.ShowPreferencesAsync();
    }

    /// <summary>
    /// Resolves the session's active id to the matching profile in the current
    /// list (null when the id is unknown or no profile exists).
    /// </summary>
    private ProfileSummary? ResolveActive() =>
        _session.ActiveProfileId is Guid id
            ? Profiles.FirstOrDefault(p => p.Id == id)
            : null;

    /// <summary>
    /// Track C: launch is not wired yet. The command is present so the Launch
    /// button binds cleanly; <see cref="CanLaunch"/> encodes the real guard (a
    /// profile must be selected and the game must not already be running), so it
    /// lights up once selection lands.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private void Launch()
    {
        // Track C: _launchService.Launch(SelectedProfile!.Id) + LaunchResult handling.
    }

    /// <summary>A profile must be selected and the game must not be running.</summary>
    private bool CanLaunch() => SelectedProfile is not null && !IsGameRunning;
}
