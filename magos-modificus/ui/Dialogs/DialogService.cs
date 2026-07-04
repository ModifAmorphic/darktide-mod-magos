using Avalonia.Controls;
using Magos.Modificus.Config;
using Magos.Modificus.Profiles;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.Preferences;
using Magos.Modificus.UI.Session;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Views;

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
    private readonly MagosConfig _config;

    /// <param name="owner">The window dialog parents are shown over (the main window).</param>
    /// <param name="profiles">Resolved lazily to construct the manage-profiles VM.</param>
    /// <param name="session">The active-profile authority; handed to the manage-profiles
    /// VM so its marker reads the live active id and its create/delete route active
    /// changes through the session's gate.</param>
    /// <param name="preferences">The Preferences authority; handed to the Preferences VM
    /// so its controls apply + persist through the single authority.</param>
    /// <param name="localization">The Localization service; handed to the manage-profiles
    /// VM (delete-confirm message is localized + the marker tooltip) and to the
    /// Preferences VM (language picker reads the live culture).</param>
    /// <param name="config">The loaded <see cref="MagosConfig"/> singleton; handed to the
    /// Preferences VM so its pickers initialize from the persisted values.</param>
    public DialogService(
        Window owner,
        IProfileService profiles,
        IProfileSession session,
        IPreferencesService preferences,
        LocalizationService localization,
        MagosConfig config)
    {
        _owner = owner;
        _profiles = profiles;
        _session = session;
        _preferences = preferences;
        _localization = localization;
        _config = config;
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
        var viewModel = new PreferencesViewModel(_preferences, _config, _localization);
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
}
