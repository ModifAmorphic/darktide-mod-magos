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
/// <para><b>Track C is wired:</b> <see cref="LaunchCommand"/> invokes
/// <see cref="IEnginseerLaunchService.Launch"/> and branches on
/// <see cref="LaunchResult.Status"/> (Launched -> status note + an immediate
/// <see cref="IsGameRunning"/> refresh; DiscoveryIncomplete -> the focused
/// escape-hatch dialog over the missing fields; Error -> a modal alert), and
/// <see cref="OpenSettingsCommand"/> opens the Settings window (discovery
/// overrides + mod-repository relocation). After Settings closes, the bound
/// <see cref="ModList"/> reloads so a relocate's rescan is reflected in the
/// mod rows.</para>
/// <para><b>DMF prompt fires after the ManageProfiles + Integrations dialogs
/// close:</b> <see cref="DmfPromptService"/> subscribes to backend signals
/// (profile-created, auth-state-changed) that fire FROM inside those dialogs;
/// it records them as pending + processes them here (after the dialog closes)
/// so the DMF prompt is the topmost modal. The shell calls
/// <see cref="DmfPromptService.ProcessPendingAsync"/> after ManageProfiles +
/// Integrations; safe to call when nothing is pending (a no-op).</para>
/// </remarks>
public partial class ShellViewModel : ObservableObject
{
    private readonly IProfileService _profileService;
    private readonly IProfileSession _session;
    private readonly IEnginseerLaunchService _launchService;
    private readonly IDialogService _dialogs;
    private readonly DmfPromptService _dmfPrompts;
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
        DmfPromptService dmfPrompts,
        LocalizationService localization,
        ModListViewModel modList,
        ILogger<ShellViewModel> logger)
    {
        _profileService = profiles;
        _session = session;
        _launchService = launchService;
        _dialogs = dialogs;
        _dmfPrompts = dmfPrompts;
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
    /// A transient launch-status note surfaced in the status strip ("Launched
    /// 'X'" on success, or the launch error title on failure). Overwritten on
    /// each launch attempt; null when no launch has happened since the shell
    /// loaded. Localized.
    /// </summary>
    /// <remarks>
    /// Brief by design: a subsequent launch overwrites it, and the durable
    /// running-state signal is the <see cref="IsGameRunning"/> indicator + the
    /// localized <see cref="GameRunningText"/>. The note is the immediate
    /// confirmation that the click did something.
    /// </remarks>
    [ObservableProperty]
    private string? _launchStatusNote;

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
        // LaunchStatusNote is a transient formatted string (not a key), so a
        // culture flip while it happens to be visible is not re-translated. The
        // next launch overwrites it; the durable signal is GameRunningText.
    }

    /// <summary>
    /// Opens the "Manage profiles…" dialog, then reloads the profile list and
    /// re-syncs the selection to the session's active id. The dialog applies
    /// active changes live through the session during its session, so by the time
    /// it closes the session already reflects whatever the gate allowed; the shell
    /// just refreshes its list snapshot and follows the authoritative active id.
    /// After the dialog closes, also processes any pending DMF new-profile prompt
    /// the dialog's create may have triggered (the prompt fires here, on the main
    /// window, not dialog-on-dialog).
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

        // Surface any DMF prompt the dialog's create may have triggered. Fires
        // after the ManageProfiles dialog is gone so the prompt is the topmost
        // modal. Safe no-op when no create happened during the dialog.
        await _dmfPrompts.ProcessPendingAsync();

        // The DMF prompt (if it fired + the user accepted) added a mod to the
        // active profile. Reload so the new row shows without a profile switch.
        // Cheap no-op when nothing changed.
        ModList.Reload();
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
    /// Opens the Integrations dialog (Nexus auth: OAuth login + API-key validate
    /// + sign-out). Nexus-only in v1; GitHub stays config-file-only. After the
    /// dialog closes, processes any pending DMF auth-trigger prompt the dialog's
    /// auth action may have triggered (the prompt fires here, on the main window,
    /// not dialog-on-dialog).
    /// </summary>
    [RelayCommand]
    private async Task OpenIntegrations()
    {
        await _dialogs.ShowIntegrationsAsync();

        // Surface any DMF prompt the dialog's auth action may have triggered.
        // Fires after the Integrations dialog is gone so the prompt is the
        // topmost modal. Safe no-op when no auth state change happened (or the
        // ask-once flag is already set).
        await _dmfPrompts.ProcessPendingAsync();

        // The DMF prompt (if it fired + the user accepted) added a mod to the
        // active profile. Reload so the new row shows without a profile switch.
        // Cheap no-op when nothing changed.
        ModList.Reload();
    }

    /// <summary>
    /// Opens the Settings dialog (discovery paths + mod-repository location),
    /// then reloads the mod list. Each setting applies + persists immediately
    /// through the dialog; on close the only follow-up is a mod-list reload,
    /// because a Settings relocate rescans the repository's index out-of-band
    /// and the <see cref="ModListViewModel.Mods"/> snapshot would otherwise be
    /// stale. (Discovery overrides are read live by the next
    /// <c>Discover()</c> / launch; no shell-side action needed for those.)
    /// </summary>
    [RelayCommand]
    private async Task OpenSettings()
    {
        await _dialogs.ShowSettingsAsync();
        ModList.Reload();
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
    /// Launches the active profile modded. Branches on
    /// <see cref="LaunchResult.Status"/>:
    /// <list type="bullet">
    /// <item><term><see cref="LaunchStatus.Launched"/></term><description>a
    /// brief localized "Launched 'X'" note in the status strip + an immediate
    /// <see cref="IsGameRunning"/> refresh so the indicator + CanLaunch react at
    /// once (not on the next poll).</description></item>
    /// <item><term><see cref="LaunchStatus.DiscoveryIncomplete"/></term><description>
    /// opens the escape-hatch dialog with the missing fields. No retry: the user
    /// clicks Launch again after submitting.</description></item>
    /// <item><term><see cref="LaunchStatus.Error"/></term><description>shows a
    /// modal alert with the result's message.</description></item>
    /// </list>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task Launch()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        var result = _launchService.Launch(profile.Id);
        switch (result.Status)
        {
            case LaunchStatus.Launched:
                LaunchStatusNote = _localization.Format("Launch_LaunchedNote", profile.Name);
                // Refresh running-state right away so the indicator + CanLaunch
                // react at once. The session's polling timer would catch up
                // eventually, but the user just clicked Launch; they should see
                // the change immediately. The session's Refresh is the source
                // of truth; the shell mirrors IsRunning from it.
                _session.Refresh();
                _logger.LogInformation("Launched profile {Id} ('{Name}').", profile.Id, profile.Name);
                break;

            case LaunchStatus.DiscoveryIncomplete:
                // No retry: the user explicitly clicks Launch again after
                // submitting. A loop here would trap them if they could not get
                // the paths right.
                LaunchStatusNote = null;
                await _dialogs.ShowDiscoveryEscapeHatchAsync(result.MissingDiscoveryFields);
                _logger.LogInformation(
                    "Discovery incomplete on launch of {Id}; showed escape-hatch for fields: {Fields}.",
                    profile.Id, string.Join(", ", result.MissingDiscoveryFields));
                break;

            case LaunchStatus.Error:
                LaunchStatusNote = null;
                await _dialogs.ShowAlertAsync(
                    _localization["Launch_ErrorTitle"],
                    result.Message ?? string.Empty);
                _logger.LogWarning("Launch of {Id} failed: {Message}.", profile.Id, result.Message);
                break;
        }
    }

    /// <summary>A profile must be selected and the game must not be running.</summary>
    private bool CanLaunch() => SelectedProfile is not null && !IsGameRunning;
}
