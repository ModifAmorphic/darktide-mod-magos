using Avalonia.Controls;
using Magos.Modificus.Profiles;
using Magos.Modificus.Steam;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Views;

namespace Magos.Modificus.UI.Dialogs;

/// <summary>
/// Production <see cref="IDialogService"/>. Owns all real Avalonia
/// <c>Window</c>/<c>ShowDialog</c> wiring so view models never construct windows
/// directly. Dialogs are shown modally over the owning main window. This is the
/// only place the app news-up a dialog window — everything else flows through
/// the <see cref="IDialogService"/> seam, which tests replace with a fake.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly Window _owner;
    private readonly IProfileService _profiles;
    private readonly ISteamService _steam;

    /// <param name="owner">The window dialog parents are shown over (the main window).</param>
    /// <param name="profiles">Resolved lazily to construct the manage-profiles VM.</param>
    /// <param name="steam">The live running-state source; handed to the manage-profiles VM so its
    /// create-sets-active consults the same <see cref="ISteamService.IsGameRunning"/> the shell gates on.</param>
    public DialogService(Window owner, IProfileService profiles, ISteamService steam)
    {
        _owner = owner;
        _profiles = profiles;
        _steam = steam;
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
    public async Task<Guid?> ShowManageProfilesAsync(Guid? currentActiveProfileId)
    {
        var viewModel = new ManageProfilesViewModel(_profiles, this, _steam, currentActiveProfileId);
        var window = new ManageProfilesWindow
        {
            DataContext = viewModel,
        };

        await window.ShowDialog(_owner);
        return viewModel.ActiveProfileId;
    }
}
