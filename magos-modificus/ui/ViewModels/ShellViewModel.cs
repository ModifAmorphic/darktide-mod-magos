using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magos.Modificus.EnginseerClient;
using Magos.Modificus.Profiles;
using Magos.Modificus.Steam;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI.ViewModels;

/// <summary>
/// The view model behind the Magos Modificus main window — the Phase 3 Track A
/// app shell. It wires the backend services <b>read-only</b> so the shell is
/// alive (real profiles list, real game-running state) while staying empty:
/// no profile CRUD, no mod list, and no launch behavior land here in milestone 1.
/// Milestone 2 (profile CRUD) and Tracks B/C (mod list, launch) drop onto the
/// properties and commands this VM already exposes.
/// </summary>
/// <remarks>
/// <b>Real (live), milestone 1:</b> <see cref="Profiles"/> is
/// <see cref="IProfileService.ListProfiles"/> and <see cref="IsGameRunning"/> is
/// <see cref="ISteamService.IsGameRunning"/>. Pre-release both are empty/false.
/// <b>Stubbed/disabled, milestone 1:</b> <see cref="ManageProfilesCommand"/> and
/// <see cref="LaunchCommand"/> are present (so the UI binds cleanly) but
/// no-op / disabled; <see cref="SelectedProfile"/> stays <c>null</c>.
/// </remarks>
public partial class ShellViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly ISteamService _steam;
    private readonly IEnginseerLaunchService _launchService;
    private readonly ILogger<ShellViewModel> _logger;

    /// <summary>
    /// Creates the shell VM and snapshots the live profile list + game-running
    /// state. Resolving every backend service here also proves the DI wiring.
    /// </summary>
    public ShellViewModel(
        IProfileService profiles,
        ISteamService steam,
        IEnginseerLaunchService launchService,
        ILogger<ShellViewModel> logger)
    {
        _profiles = profiles;
        _steam = steam;
        _launchService = launchService;
        _logger = logger;

        // Live, read-only snapshots. Pre-release there are no profiles on disk,
        // so Profiles is empty until milestone 2 (CRUD) populates it.
        Profiles = _profiles.ListProfiles();
        _isGameRunning = _steam.IsGameRunning();

        _logger.LogInformation(
            "Shell initialized: {ProfileCount} profile(s) loaded; Darktide running: {IsRunning}; launch facade: {LaunchFacade}",
            Profiles.Count,
            _isGameRunning,
            _launchService.GetType().Name);
    }

    /// <summary>
    /// All known profiles — a read-only snapshot of
    /// <see cref="IProfileService.ListProfiles"/>. Empty pre-release; milestone 2
    /// makes this react to profile CRUD.
    /// </summary>
    public IReadOnlyList<ProfileSummary> Profiles { get; }

    /// <summary>Whether at least one profile exists (gates the profile dropdown's enabled state).</summary>
    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>
    /// The currently-selected profile, or <c>null</c>. Milestone 1 leaves this
    /// <c>null</c> (nothing to select); milestone 2 binds it to the top-bar
    /// profile dropdown.
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
    /// construction. Milestone 1 shows this read-only in the status strip.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyPropertyChangedFor(nameof(GameRunningText))]
    private bool _isGameRunning;

    /// <summary>Status-strip label for the game-running indicator.</summary>
    public string GameRunningText => IsGameRunning ? "Darktide: running" : "Darktide: not running";

    /// <summary>
    /// Milestone 1: profile management (the create/rename/delete dialog + the
    /// dropdown switch) is not yet implemented. The command is present so the
    /// <c>Manage profiles…</c> affordance binds, but it is always disabled;
    /// milestone 2 implements it (the management dialog + switch logic).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanManageProfiles))]
    private void ManageProfiles()
    {
        // milestone 2: open the create/rename/delete dialog and wire the dropdown switch.
    }

    /// <summary>Milestone 1: profile management is not implemented yet.</summary>
    private bool CanManageProfiles() => false;

    /// <summary>
    /// Milestone 1: launch is not wired (Track C). The command is present so the
    /// Launch button binds cleanly; its <see cref="CanLaunch"/> encodes the real
    /// guard (a profile must be selected and the game must not already be
    /// running), so it stays disabled in milestone 1 — <see cref="SelectedProfile"/>
    /// is <c>null</c> — and lights up naturally once selection + launch land.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private void Launch()
    {
        // Track C: _launchService.Launch(SelectedProfile!.Id) + LaunchResult handling.
    }

    /// <summary>A profile must be selected and the game must not be running.</summary>
    private bool CanLaunch() => SelectedProfile is not null && !IsGameRunning;
}
