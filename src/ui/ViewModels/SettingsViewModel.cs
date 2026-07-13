using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;
using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Settings;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the Settings modal (<see cref="Views.SettingsWindow"/>).
/// Two sections:
/// <list type="bullet">
/// <item><description><b>Discovery:</b> the user-override paths
/// (<c>UserSteamInstallPath</c>, <c>UserDarktideGameBinaryPath</c>,
/// <c>UserCompatdataPath</c>, <c>UserProtonBinaryPath</c>), platform-gated
/// so Windows renders only the Steam install + Darktide binary rows (the
/// compatdata + Proton rows are Linux-only: <c>WindowsLaunchStrategy</c>
/// ignores them, so they would be silently ineffective on Windows). Each
/// row's TextBox is pre-filled with the current override (or empty when the
/// field is set to auto-discover). Editing writes the override immediately
/// via a read-modify-save through <see cref="IConfigLoader"/> (the Track D
/// Preferences pattern: apply + persist per change). An empty TextBox
/// clears the override (writes <c>null</c>, so the field falls back to
/// auto-discovery).</description></item>
/// <item><description><b>Storage:</b> the mod-repository location
/// (<c>ModsFolder</c>). Read-only on open (pre-filled from config); Browse opens
/// a folder picker, and a change runs the relocate flow as a single atomic call:
/// <c>repo.Relocate(new)</c> owns the move + config save + rescan (rolling back
/// the move on save failure). Failures (invalid path, conflicting UUID, IO
/// error, save failure) surface as a <see cref="StatusMessage"/> under the
/// field.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Never holds a cached <see cref="CuratorConfig"/>:</b> each field change
/// calls <see cref="IConfigLoader.Load"/> + <see cref="IConfigLoader.Save"/> (a
/// read-modify-save), so concurrent edits by other surfaces (the escape-hatch)
/// are never clobbered. The config file is tiny; the round-trip is cheap.</para>
/// <para><b>The browse buttons:</b> opening a storage-provider picker is a view
/// concern (it needs the live TopLevel), so the view code-behind opens the
/// picker and either sets the row's <c>Value</c> (discovery) or calls
/// <see cref="ApplyModsFolderCommand"/> (storage). No file paths cross the VM
/// boundary except the picked path itself.</para>
/// </remarks>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IConfigLoader _configLoader;
    private readonly IModRepository _repo;
    private readonly LocalizationService _localization;
    private readonly IAppUpdateService _appUpdate;
    private readonly IDialogService _dialogs;
    private readonly Action<Action> _invokeOnUi;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>
    /// True during the initial restore (constructing the VM pre-fills the bound
    /// controls from the current config without re-writing each field back). The
    /// values already match what is persisted, so re-writing would be a noisy
    /// no-op.
    /// </summary>
    private bool _suppressApply;

    /// <summary>
    /// True after the user has run at least one manual check from this Settings
    /// session (or a background check landed while Settings was open). Before
    /// that, <see cref="AppUpdateStatusMessage"/> is null (no status shown); once
    /// a check has run, a null <see cref="IAppUpdateService.LastCheckResult"/>
    /// resolves to the "up to date" message rather than blank.
    /// </summary>
    /// <remarks>Volatile: <see cref="OnAppUpdateStateChanged"/> fires on a
    /// threadpool thread (the service publishes from its background check) and
    /// sets this before marshaling to the UI thread, while
    /// <see cref="RefreshAppUpdateStatus"/> (and the manual-check paths) read it
    /// on the UI thread. <c>volatile</c> guarantees the UI-thread read observes
    /// the background write without a stale-cache reordering. The UI-thread
    /// writes (the manual <see cref="CheckAppUpdate"/> paths) are
    /// single-threaded by construction.</remarks>
    private volatile bool _hasCheckedAppUpdate;

    /// <summary>
    /// Creates the Settings VM, pre-fills the discovery rows (platform-gated:
    /// Steam install + Darktide binary on Windows; all four on Linux) + the
    /// ModsFolder TextBox from the live config, and wires each discovery row's
    /// change callback to the write-through path.
    /// </summary>
    /// <param name="configLoader">The live config reader/writer. Each field change
    /// does a read-modify-save through this.</param>
    /// <param name="repo">The mod repository, used for the relocate flow on a
    /// ModsFolder change.</param>
    /// <param name="localization">The localization service; handed to each
    /// discovery row so its label resolves + refreshes on a culture change.</param>
    /// <param name="appUpdate">The app self-update service; backs the Updates
    /// section (current version, manual check, download + restart).</param>
    /// <param name="dialogs">The dialog service; the download + restart flow runs
    /// the download under its modal spinner and surfaces failures as an alert.</param>
    /// <param name="invokeOnUi">Marshals the off-thread
    /// <see cref="IAppUpdateService.UpdateStateChanged"/> handler's refresh onto
    /// the UI thread. Production wires <c>Dispatcher.UIThread.Post</c>; tests
    /// inject a synchronous <c>action =&gt; action()</c>.</param>
    /// <param name="logger">Logger for the relocate flow.</param>
    public SettingsViewModel(
        IConfigLoader configLoader,
        IModRepository repo,
        LocalizationService localization,
        IAppUpdateService appUpdate,
        IDialogService dialogs,
        Action<Action> invokeOnUi,
        ILogger<SettingsViewModel> logger)
    {
        _configLoader = configLoader;
        _repo = repo;
        _localization = localization;
        _appUpdate = appUpdate;
        _dialogs = dialogs;
        _invokeOnUi = invokeOnUi ?? throw new ArgumentNullException(nameof(invokeOnUi));
        _logger = logger;

        _suppressApply = true;
        try
        {
            var discovery = _configLoader.Load().Discovery;

            // Each discovery row's change callback is the write-through: the
            // value lands in config (read-modify-save) the moment the TextBox
            // edits (or the Browse picker sets it). Suppressed only during the
            // initial restore, which is what pre-fills the boxes.
            //
            // Platform-gated: on Windows the compatdata + Proton overrides are
            // Linux-only (WindowsLaunchStrategy.ComputeStatus never reads them,
            // so surfacing them would be silently ineffective rows). Only the
            // Steam install + Darktide binary rows render on Windows. The
            // escape-hatch is already correct (it renders only the names in
            // LaunchResult.MissingDiscoveryFields, which on Windows never
            // includes the Linux-only ones).
            DiscoveryRows = new ObservableCollection<DiscoveryFieldRowViewModel>(
                DiscoveryFields.All
                    .Where(field => OperatingSystem.IsLinux() || !IsLinuxOnlyField(field))
                    .Select(field => new DiscoveryFieldRowViewModel(
                        field,
                        InitialValue(field, discovery),
                        _localization,
                        onValueChanged: WriteThroughDiscovery)));

            // Pre-fill the app-update startup-check toggle. Inside the
            // _suppressApply block so the OnCheckOnStartupChanged write-back is
            // a no-op during the restore.
            CheckOnStartup = _configLoader.Load().AppUpdates.CheckOnStartup;
        }
        finally
        {
            _suppressApply = false;
        }

        // Subscribe for the localized section headers + the status message
        // (which embeds a resx key when set); the row VMs each subscribe on
        // their own.
        _localization.PropertyChanged += OnCultureChanged;

        // Subscribe to the app self-update state so a check that lands while
        // Settings is open refreshes the inline status. Also reflect any result
        // the startup check already published so the section shows the current
        // state immediately on open.
        _appUpdate.UpdateStateChanged += OnAppUpdateStateChanged;
        RefreshAppUpdateStatus();

        ModsFolder = _configLoader.Load().ModsFolder;
    }

    /// <summary>
    /// The discovery-field rows (Steam install, Darktide binary, compatdata,
    /// Proton binary), platform-gated: Windows renders only the first two (the
    /// compatdata + Proton rows are Linux-only). Bound to an
    /// <c>ItemsControl</c> in the view; each row owns its TextBox value + Browse
    /// button (the browse kind drives which picker opens).
    /// </summary>
    public ObservableCollection<DiscoveryFieldRowViewModel> DiscoveryRows { get; }

    /// <summary>
    /// The mod-repository location (<c>ModsFolder</c>). Pre-filled from config;
    /// editable only through the Browse button (the TextBox is read-only in the
    /// view to prevent free-text entry of an invalid path). A change is applied
    /// via <see cref="ApplyModsFolderCommand"/>, which runs the relocate flow.
    /// </summary>
    [ObservableProperty]
    private string _modsFolder = string.Empty;

    /// <summary>
    /// The localized header for the discovery section. Re-resolves on a culture
    /// change.
    /// </summary>
    public string DiscoverySectionHeader => _localization["Settings_DiscoverySection"];

    /// <summary>
    /// The localized header for the storage section. Re-resolves on a culture
    /// change.
    /// </summary>
    public string StorageSectionHeader => _localization["Settings_StorageSection"];

    /// <summary>
    /// The localized label for the ModsFolder row. Re-resolves on a culture
    /// change.
    /// </summary>
    public string ModsFolderLabel => _localization["Settings_ModRepoLabel"];

    /// <summary>
    /// A non-blocking status message surfaced under the ModsFolder row (red,
    /// visible only when non-empty). Set when the relocate flow fails; cleared
    /// on a successful relocate or when the user clears the field. The text is
    /// the raw exception message (no localization): it is operator-facing
    /// diagnostic detail, not user copy.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    // ---- Updates section ---------------------------------------------------

    /// <summary>
    /// The localized header for the Updates section. Re-resolves on a culture
    /// change.
    /// </summary>
    public string UpdatesSectionHeader => _localization["Settings_UpdatesSection"];

    /// <summary>
    /// The localized label for the current-version row. Re-resolves on a culture
    /// change.
    /// </summary>
    public string CurrentVersionLabel => _localization["Settings_CurrentVersionLabel"];

    /// <summary>
    /// Whether app self-update is meaningful for this build (a packaged Windows
    /// install). The section always renders; the controls are disabled when this
    /// is false (Linux, a dev run) so those users still see the version.
    /// </summary>
    public bool IsAppUpdateSupported => _appUpdate.IsUpdateSupported;

    /// <summary>
    /// The installed Curator version, or a localized "unknown" when it cannot be
    /// resolved (a non-packaged build). Re-resolves on a culture change.
    /// </summary>
    public string CurrentVersionDisplay =>
        _appUpdate.CurrentVersion ?? _localization["Settings_VersionUnknown"];

    /// <summary>
    /// Whether Curator checks for a new version of itself on startup. Pre-filled
    /// from <c>CuratorConfig.AppUpdates.CheckOnStartup</c> on construction;
    /// persisted on each user change via a read-modify-save. Gates ONLY the
    /// automatic startup check (<c>AppUpdateCheckRunner</c>); the manual "Check
    /// for Updates" button always works regardless.
    /// </summary>
    [ObservableProperty]
    private bool _checkOnStartup;

    /// <summary>
    /// Persisted when the user flips <see cref="CheckOnStartup"/>. Read-modify-
    /// saves <c>CuratorConfig.AppUpdates.CheckOnStartup</c> (no caching, mirrors
    /// <c>IntegrationsViewModel.SaveAutoUpdateSettings</c>). Suppressed during
    /// the initial restore so pre-filling the field does not trigger a redundant
    /// write-back.
    /// </summary>
    partial void OnCheckOnStartupChanged(bool value)
    {
        if (_suppressApply)
        {
            return;
        }

        var config = _configLoader.Load();
        config.AppUpdates.CheckOnStartup = value;
        _configLoader.Save(config);
    }

    /// <summary>
    /// True while a manual update check is in flight (drives the inline spinner +
    /// disables the Check button).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckAppUpdateCommand))]
    private bool _isCheckingAppUpdate;

    /// <summary>
    /// Inline status under the Check button: null before any check; a localized
    /// "up to date" once a check finds nothing; or a formatted "Curator X is
    /// available" once a check finds an update. The visibility of this row in the
    /// view is gated on non-empty (the up-to-date + available messages).
    /// </summary>
    [ObservableProperty]
    private string? _appUpdateStatusMessage;

    /// <summary>
    /// Whether an update is available (a non-null
    /// <see cref="IAppUpdateService.LastCheckResult"/>). Gates the Download and
    /// Restart button's visibility + the download command's CanExecute.
    /// </summary>
    public bool IsAppUpdateAvailable => _appUpdate.LastCheckResult is not null;

    /// <summary>
    /// Detaches the VM's culture subscription + each row's, so the short-lived
    /// dialog VM is collectable after its window closes (the localization service
    /// is a singleton that outlives the dialog). Called by the Settings window
    /// on close.
    /// </summary>
    public void Detach()
    {
        _localization.PropertyChanged -= OnCultureChanged;
        _appUpdate.UpdateStateChanged -= OnAppUpdateStateChanged;
        foreach (var row in DiscoveryRows)
        {
            row.Detach();
        }
    }

    /// <summary>
    /// Re-fires the localized derived strings (section headers + the ModsFolder
    /// label) on a culture change. The per-row labels refresh themselves (each
    /// row subscribes on its own).
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationService.Culture) or "Item[]"))
        {
            return;
        }

        OnPropertyChanged(nameof(DiscoverySectionHeader));
        OnPropertyChanged(nameof(StorageSectionHeader));
        OnPropertyChanged(nameof(ModsFolderLabel));
        OnPropertyChanged(nameof(UpdatesSectionHeader));
        OnPropertyChanged(nameof(CurrentVersionLabel));
        OnPropertyChanged(nameof(CurrentVersionDisplay));
    }

    /// <summary>
    /// The relocate flow as a single atomic call: <c>repo.Relocate(newPath)</c>
    /// owns the move (every container directory from the current mods root to
    /// the new one), the config save (<c>ModsFolder = newPath</c>), and the
    /// rescan. On any failure (invalid path, conflicting UUID, IO error, or a
    /// save failure that the repository rolls back), the exception message is
    /// surfaced via <see cref="StatusMessage"/> and the ModsFolder TextBox is
    /// left as-is so the user can see the prior value. A successful relocate
    /// clears <see cref="StatusMessage"/> + updates the TextBox.
    /// </summary>
    /// <param name="newPath">The absolute path the user picked. Caller (the
    /// view) guarantees non-null/non-empty; the repo also validates.</param>
    [RelayCommand]
    private void ApplyModsFolder(string newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath))
        {
            return;
        }

        // Same-path is a no-op (the repo also short-circuits). Skipping early
        // avoids a confusing save + rescan when the user re-picks the current
        // location.
        if (string.Equals(ModsFolder, newPath, StringComparison.Ordinal))
        {
            StatusMessage = null;
            return;
        }

        try
        {
            // Atomic: the repo owns move + save + rescan (rolling the move back
            // on save failure). See IModRepository.Relocate.
            _repo.Relocate(newPath);

            ModsFolder = newPath;
            StatusMessage = null;

            _logger.LogInformation("Relocated mod repository to {Path}.", newPath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            // Relocate failed (invalid path / conflict / IO / rolled-back save
            // failure). Surface the message; the ModsFolder TextBox keeps its
            // prior value, so the user sees what is currently in effect.
            StatusMessage = ex.Message;
            _logger.LogWarning(ex, "Mod repository relocate to {Path} failed.", newPath);
        }
    }

    // ---- Updates section: app self-update ---------------------------------

    /// <summary>
    /// The app self-update service published new state (a check landed while
    /// Settings was open). The event fires on a threadpool thread, so the
    /// property changes are marshaled to the UI thread via the
    /// <see cref="_invokeOnUi"/> seam before touching
    /// <see cref="ObservableObject"/> bindings (mirrors the shell's handler).
    /// </summary>
    private void OnAppUpdateStateChanged(object? sender, EventArgs e)
    {
        // Set on the threadpool thread (before the marshal); volatile so the
        // UI-thread RefreshAppUpdateStatus observes the write.
        _hasCheckedAppUpdate = true;
        _invokeOnUi(RefreshAppUpdateStatus);
    }

    /// <summary>
    /// Re-derives <see cref="AppUpdateStatusMessage"/> from
    /// <see cref="IAppUpdateService.LastCheckResult"/> + re-fires
    /// <see cref="IsAppUpdateAvailable"/> + the download command's CanExecute.
    /// Before any check has run, the status is blank (no message shown); after a
    /// check, a null result resolves to "up to date" and a non-null result to
    /// the available-version message.
    /// </summary>
    private void RefreshAppUpdateStatus()
    {
        var info = _appUpdate.LastCheckResult;
        if (info is not null)
        {
            AppUpdateStatusMessage = _localization.Format("Settings_UpdateAvailable", info.TargetVersion);
        }
        else if (_hasCheckedAppUpdate)
        {
            AppUpdateStatusMessage = _localization["Settings_UpToDate"];
        }
        else
        {
            AppUpdateStatusMessage = null;
        }

        OnPropertyChanged(nameof(IsAppUpdateAvailable));
        DownloadAndRestartAppUpdateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Manual "Check for Updates": runs the availability check off the UI thread
    /// and refreshes the inline status from the result. A failure surfaces a
    /// localized "check failed" inline status (the check itself is best-effort,
    /// so a throw here is a wiring problem; defensive). Toggles
    /// <see cref="IsCheckingAppUpdate"/> around the check for the spinner.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckAppUpdate))]
    private async Task CheckAppUpdate()
    {
        IsCheckingAppUpdate = true;
        try
        {
            // The check is I/O; offload it to a thread-pool task so the UI thread
            // stays free. Bare await inside Task.Run is fine (no
            // SynchronizationContext).
            await Task.Run(() => _appUpdate.CheckForUpdatesAsync());
            _hasCheckedAppUpdate = true;
            RefreshAppUpdateStatus();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual app update check failed.");
            _hasCheckedAppUpdate = true;
            AppUpdateStatusMessage = _localization["AppUpdate_CheckFailedMessage"];
            OnPropertyChanged(nameof(IsAppUpdateAvailable));
            DownloadAndRestartAppUpdateCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsCheckingAppUpdate = false;
        }
    }

    /// <summary>Only one check at a time, and only when self-update is supported.</summary>
    private bool CanCheckAppUpdate() => !IsCheckingAppUpdate && IsAppUpdateSupported;

    /// <summary>
    /// Download and Restart: runs the download under a modal spinner, then
    /// applies the update on restart. Download failures surface an alert and do
    /// NOT proceed to apply. Mirrors the shell's download flow.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadAndRestartAppUpdate))]
    private async Task DownloadAndRestartAppUpdate()
    {
        try
        {
            // The download is I/O; offload it to a thread-pool task inside the
            // spinner's work delegate. The ProgressDialog is indeterminate (the
            // final design), so no percentage is surfaced.
            await _dialogs.ShowProgressAsync(
                _localization["AppUpdate_DownloadingTitle"],
                _localization["AppUpdate_DownloadingMessage"],
                () => Task.Run(async () =>
                {
                    // Bare await inside Task.Run (no SynchronizationContext); the
                    // VM-file convention forbids ConfigureAwait(false) entirely.
                    await _appUpdate.DownloadUpdatesAsync();
                    return true;
                }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "App update download failed (Settings).");
            await _dialogs.ShowAlertAsync(
                _localization["AppUpdate_DownloadFailedTitle"],
                _localization["AppUpdate_DownloadFailedMessage"] + " " + ex.Message);
            return;
        }

        // Success: terminates this process + relaunches under the new version.
        _appUpdate.ApplyUpdatesAndRestart();
    }

    /// <summary>The download is only reachable when an update is available.</summary>
    private bool CanDownloadAndRestartAppUpdate() => IsAppUpdateAvailable;

    /// <summary>
    /// The write-through for a discovery field change: read-modify-save the
    /// matching <c>User*Path</c> in <see cref="DiscoveryConfig"/>. An empty /
    /// whitespace value writes <c>null</c> (clears the override -> auto-discover).
    /// Suppressed during the initial restore.
    /// </summary>
    private void WriteThroughDiscovery(DiscoveryFieldRowViewModel row)
    {
        if (_suppressApply)
        {
            return;
        }

        var value = row.Value;
        var written = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var config = _configLoader.Load();
        SetOverride(config.Discovery, row.Field.FieldName, written);
        _configLoader.Save(config);
    }

    /// <summary>
    /// Maps a discovery field's canonical name to its setter on
    /// <see cref="DiscoveryConfig"/>. The name matches the property names of
    /// <see cref="Steam.DiscoveryResult"/> (the same names that flow through
    /// <c>LaunchResult.MissingDiscoveryFields</c>).
    /// </summary>
    private static void SetOverride(DiscoveryConfig discovery, string fieldName, string? value)
    {
        switch (fieldName)
        {
            case "SteamInstallPath":
                discovery.UserSteamInstallPath = value;
                return;
            case "DarktideGameBinaryPath":
                discovery.UserDarktideGameBinaryPath = value;
                return;
            case "CompatdataPath":
                discovery.UserCompatdataPath = value;
                return;
            case "ProtonBinaryPath":
                discovery.UserProtonBinaryPath = value;
                return;
            default:
                return;
        }
    }

    /// <summary>
    /// Reads the current override value for a field from config (or empty when
    /// it is null/whitespace, i.e. set to auto-discover). Used to pre-fill each
    /// row at construction.
    /// </summary>
    private static string InitialValue(DiscoveryField field, DiscoveryConfig discovery) =>
        field.FieldName switch
        {
            "SteamInstallPath" => discovery.UserSteamInstallPath ?? string.Empty,
            "DarktideGameBinaryPath" => discovery.UserDarktideGameBinaryPath ?? string.Empty,
            "CompatdataPath" => discovery.UserCompatdataPath ?? string.Empty,
            "ProtonBinaryPath" => discovery.UserProtonBinaryPath ?? string.Empty,
            _ => string.Empty,
        };

    /// <summary>
    /// Whether a discovery field is Linux-only (the compatdata + Proton
    /// overrides, which <c>WindowsLaunchStrategy</c> ignores). Used to
    /// platform-gate the Settings rows so Windows does not surface
    /// silently-ineffective rows. The catalog is the single source of truth for
    /// field identity; this helper is the only place that knows which of those
    /// fields are Linux-scoped.
    /// </summary>
    private static bool IsLinuxOnlyField(DiscoveryField field) =>
        field.FieldName is "CompatdataPath" or "ProtonBinaryPath";
}
