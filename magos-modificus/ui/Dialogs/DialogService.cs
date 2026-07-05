using Avalonia.Controls;
using Magos.Modificus.General;
using Magos.Modificus.Mods;
using Magos.Modificus.Profiles;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.Preferences;
using Magos.Modificus.UI.Session;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Views;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI.Dialogs;

/// <summary>
/// Production <see cref="IDialogService"/>. Owns all real Avalonia
/// <c>Window</c>/<c>ShowDialog</c> wiring so view models never construct windows
/// directly. Dialogs are shown modally over the owning main window. This is the
/// only place the app news-up a dialog window, everything else flows through
/// the <see cref="IDialogService"/> seam, which tests replace with a fake.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly Window _owner;
    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IPreferencesService _preferences;
    private readonly LocalizationService _localization;
    private readonly IConfigLoader _configLoader;
    private readonly IModRepository _mods;
    private readonly ILoggerFactory _loggerFactory;

    /// <param name="owner">The window dialog parents are shown over (the main window).</param>
    /// <param name="profiles">Resolved lazily to construct the manage-profiles VM.</param>
    /// <param name="session">The active-profile authority; handed to the manage-profiles
    /// VM so its marker reads the live active id and its create/delete route active
    /// changes through the session's gate.</param>
    /// <param name="preferences">The Preferences authority; handed to the Preferences VM
    /// so its controls apply + persist through the single authority.</param>
    /// <param name="localization">The Localization service; handed to the manage-profiles
    /// VM (delete-confirm message is localized + the marker tooltip), the Preferences VM
    /// (language picker reads the live culture), the Settings VM (section headers +
    /// per-row labels), and the escape-hatch VM (header + per-row labels).</param>
    /// <param name="configLoader">The live config reader/writer; handed to the
    /// Preferences VM (initial picker state), the Settings VM (read-modify-save per
    /// field change), and the escape-hatch VM (one read-modify-save on submit).</param>
    /// <param name="mods">The mod repository; handed to the Settings VM for the
    /// atomic relocate flow (move + save + rescan) on a ModsFolder change.</param>
    /// <param name="loggerFactory">The logger factory; the Settings VM gets a typed
    /// logger from it so its relocate success/failure log lines reach the configured
    /// sinks (the other dialog VMs take no logger).</param>
    public DialogService(
        Window owner,
        IProfileService profiles,
        IProfileSession session,
        IPreferencesService preferences,
        LocalizationService localization,
        IConfigLoader configLoader,
        IModRepository mods,
        ILoggerFactory loggerFactory)
    {
        _owner = owner;
        _profiles = profiles;
        _session = session;
        _preferences = preferences;
        _localization = localization;
        _configLoader = configLoader;
        _mods = mods;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ConfirmDialog
        {
            Title = title,
        };
        dialog.SetMessage(message);

        await dialog.ShowDialog(_owner);
        return dialog.Result;
    }

    /// <inheritdoc />
    public async Task ShowManageProfilesAsync()
    {
        var viewModel = new ManageProfilesViewModel(_profiles, this, _session, _localization);
        var window = new ManageProfilesWindow
        {
            DataContext = viewModel,
        };

        await window.ShowDialog(_owner);
    }

    /// <inheritdoc />
    public async Task ShowPreferencesAsync()
    {
        // The VM reads its initial state from a live snapshot (no cached
        // singleton); subsequent changes flow through IPreferencesService, which
        // read-modify-saves via the same loader.
        var viewModel = new PreferencesViewModel(_preferences, _configLoader, _localization);
        var window = new PreferencesWindow
        {
            DataContext = viewModel,
        };

        await window.ShowDialog(_owner);
    }

    /// <inheritdoc />
    public async Task<ImportModResult?> ShowImportModAsync(ImportModRequest request)
    {
        var viewModel = new ImportModViewModel(request, _localization);
        var window = new ImportModDialog
        {
            DataContext = viewModel,
        };

        await window.ShowDialog(_owner);
        return viewModel.Result;
    }

    /// <inheritdoc />
    public async Task ShowSettingsAsync()
    {
        // The VM reads its initial state from a live snapshot (no cached
        // singleton); subsequent changes do a read-modify-save per field via
        // the same loader. The ModsFolder change routes through the atomic
        // Relocate (move + save + rescan) on the wired repository. A typed
        // logger is created here so the relocate success/failure lines reach
        // the configured sinks (not a NullLogger that drops them).
        var viewModel = new SettingsViewModel(
            _configLoader,
            _mods,
            _localization,
            _loggerFactory.CreateLogger<SettingsViewModel>());
        var window = new SettingsWindow
        {
            DataContext = viewModel,
        };

        await window.ShowDialog(_owner);
    }

    /// <inheritdoc />
    public async Task<bool> ShowDiscoveryEscapeHatchAsync(IReadOnlyList<string> missingFields)
    {
        // No rows to fill: skip the modal entirely (the caller should not have
        // shown the hatch for an empty list, but defensive is cheaper than a
        // confusing empty dialog).
        if (missingFields is null || missingFields.Count == 0)
        {
            return false;
        }

        var viewModel = new DiscoveryEscapeHatchViewModel(missingFields, _configLoader, _localization);
        var window = new DiscoveryEscapeHatchDialog
        {
            DataContext = viewModel,
        };

        await window.ShowDialog(_owner);
        return viewModel.Result;
    }

    /// <inheritdoc />
    public async Task ShowAlertAsync(string title, string message)
    {
        // Reuses the ConfirmDialog chrome (title bar + message + button) in its
        // single-button mode: Cancel is hidden, so the only affordance is OK.
        // A dedicated AlertDialog would carry the same chrome; this keeps one
        // chrome implementation for all simple message dialogs.
        var dialog = new ConfirmDialog
        {
            Title = title,
            ShowCancel = false,
        };
        dialog.SetMessage(message);

        await dialog.ShowDialog(_owner);
    }
}
