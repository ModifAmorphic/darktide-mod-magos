using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magos.Modificus.EnginseerClient;
using Magos.Modificus.General;
using Magos.Modificus.Profiles;
using Magos.Modificus.Steam;
using Magos.Modificus.UI.Dialogs;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI.ViewModels;

/// <summary>
/// The view model behind the Magos Modificus main window — the Phase 3 Track A
/// app shell. Milestone 2 makes the profile controls work: the dropdown
/// switches the active profile (persisted across restarts via
/// <see cref="IAppStateStore"/>), switching is blocked while Darktide runs, and
/// "Manage profiles…" opens a CRUD dialog. The shell is the single owner of the
/// active <see cref="SelectedProfile"/> + its persistence; the dialog reports a
/// requested active id and the shell applies it on close.
/// </summary>
/// <remarks>
/// Track C (Launch behavior) and Track B (mod-list contents) are not wired here
/// yet — <see cref="LaunchCommand"/> stays a no-op placeholder whose guard is
/// real so it lights up once a profile is selected and the game is stopped.
/// </remarks>
public partial class ShellViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private readonly ISteamService _steam;
    private readonly IEnginseerLaunchService _launchService;
    private readonly IAppStateStore _appState;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ShellViewModel> _logger;

    // Guards persistence during the constructor's initial selection restore —
    // loading the saved active id must not write it straight back.
    private bool _suppressPersistence = true;

    /// <summary>
    /// Creates the shell VM, snapshots the profile list + game-running state,
    /// and restores the last-chosen active profile from app-state.
    /// </summary>
    public ShellViewModel(
        IProfileService profiles,
        ISteamService steam,
        IEnginseerLaunchService launchService,
        IAppStateStore appState,
        IDialogService dialogs,
        ILogger<ShellViewModel> logger)
    {
        _profileService = profiles;
        _steam = steam;
        _launchService = launchService;
        _appState = appState;
        _dialogs = dialogs;
        _logger = logger;

        Profiles = _profileService.ListProfiles();
        _isGameRunning = _steam.IsGameRunning();

        // Restore the persisted active profile (null on first run / corrupt state).
        var activeId = _appState.ActiveProfileId;
        SelectedProfile = activeId is Guid id
            ? Profiles.FirstOrDefault(p => p.Id == id)
            : null;

        _suppressPersistence = false;

        _logger.LogInformation(
            "Shell initialized: {ProfileCount} profile(s) loaded; active={ActiveId}; " +
            "Darktide running: {IsRunning}; launch facade: {LaunchFacade}",
            Profiles.Count,
            activeId?.ToString() ?? "(none)",
            _isGameRunning,
            _launchService.GetType().Name);
    }

    /// <summary>
    /// All known profiles — a snapshot of <see cref="IProfileService.ListProfiles"/>,
    /// refreshed after the management dialog closes. Empty pre-release until a
    /// profile is created.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfiles))]
    [NotifyPropertyChangedFor(nameof(CanSwitchProfile))]
    [NotifyPropertyChangedFor(nameof(ProfileSwitchTooltip))]
    private IReadOnlyList<ProfileSummary> _profiles = Array.Empty<ProfileSummary>();

    /// <summary>Whether at least one profile exists.</summary>
    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>
    /// The currently-selected (active) profile, or <c>null</c>. Bound to the
    /// top-bar dropdown; selecting switches the active profile and persists it.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedProfile))]
    private ProfileSummary? _selectedProfile;

    /// <summary>Whether a profile is currently selected (drives the mod-list empty state).</summary>
    public bool HasSelectedProfile => SelectedProfile is not null;

    /// <summary>
    /// Whether Darktide is currently running — the real
    /// <see cref="ISteamService.IsGameRunning"/> check, snapshotted at
    /// construction. Gates profile switching (<see cref="CanSwitchProfile"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyPropertyChangedFor(nameof(GameRunningText))]
    [NotifyPropertyChangedFor(nameof(CanSwitchProfile))]
    [NotifyPropertyChangedFor(nameof(ProfileSwitchTooltip))]
    private bool _isGameRunning;

    /// <summary>Status-strip label for the game-running indicator.</summary>
    public string GameRunningText => IsGameRunning ? "Darktide: running" : "Darktide: not running";

    /// <summary>
    /// Whether the profile dropdown is interactive: a profile must exist and the
    /// game must not be running. Switching the active (staged) root while the
    /// game runs is disallowed.
    /// </summary>
    public bool CanSwitchProfile => !IsGameRunning && HasProfiles;

    /// <summary>
    /// Tooltip explaining the dropdown's current enabled state (the block reason
    /// when the game is running, or a first-run hint when no profile exists).
    /// </summary>
    public string ProfileSwitchTooltip =>
        IsGameRunning ? "Darktide is running — stop it before switching profiles"
        : HasProfiles ? string.Empty
        : "Create a profile first";

    /// <summary>
    /// Persists the active profile whenever the selection changes from the UI —
    /// except during the constructor's initial restore (guarded).
    /// </summary>
    partial void OnSelectedProfileChanged(ProfileSummary? value)
    {
        if (_suppressPersistence)
        {
            return;
        }

        _appState.ActiveProfileId = value?.Id;
    }

    /// <summary>
    /// Opens the "Manage profiles…" dialog, then refreshes the profile list and
    /// applies the active id the dialog reports (newly-created, fallback after
    /// active-delete, or unchanged on rename/no-op).
    /// </summary>
    [RelayCommand]
    private async Task ManageProfiles()
    {
        var requestedActiveId = await _dialogs.ShowManageProfilesAsync(SelectedProfile?.Id);
        RefreshProfiles(requestedActiveId);
    }

    /// <summary>
    /// Reloads the profile list and re-applies the requested active id, falling
    /// back to the current selection when the requested id is absent (deleted
    /// without an active change) so the dropdown stays on a sensible row.
    /// </summary>
    private void RefreshProfiles(Guid? requestedActiveId)
    {
        Profiles = _profileService.ListProfiles();

        var target = requestedActiveId ?? SelectedProfile?.Id;
        SelectedProfile = target is Guid id
            ? Profiles.FirstOrDefault(p => p.Id == id)
            : null;
    }

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
