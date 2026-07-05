using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Magos.Modificus.UI.Settings;
using Magos.Modificus.UI.ViewModels;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// The Settings modal window. Its <c>DataContext</c> is a
/// <see cref="SettingsViewModel"/> (set by <see cref="Dialogs.DialogService"/>).
/// Discovery field rows are an <c>ItemsControl</c> bound to the VM's
/// <see cref="SettingsViewModel.DiscoveryRows"/>; each row's Browse button is
/// wired through here (the picker is a view concern, needing the live
/// <c>TopLevel</c>). After a pick, the row's <c>Value</c> is set directly, which
/// triggers the VM's write-through. The ModsFolder Browse button calls the VM's
/// <see cref="SettingsViewModel.ApplyModsFolderCommand"/> with the picked path.
/// </summary>
/// <remarks>
/// All persistence logic lives in the (unit-tested) VM; this is pure view
/// mechanics. See <see cref="SettingsViewModel"/> for the discovery write-through
/// + the relocate flow.
/// </remarks>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    /// <summary>
    /// Detaches the VM's subscriptions on close so the short-lived dialog VM is
    /// collectable (the localization service is a singleton that outlives the
    /// dialog).
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Detach();
        base.OnClosed(e);
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A discovery row's Browse button. The button carries the field's
    /// <see cref="DiscoveryBrowseKind"/> as its <c>CommandParameter</c>; this
    /// opens the matching picker, takes the first selection (single-select), and
    /// sets the row's <c>Value</c> (which triggers the VM's write-through).
    /// Multi-select is not meaningful for a single path field.
    /// </summary>
    private async void BrowseDiscovery_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b
            || b.DataContext is not DiscoveryFieldRowViewModel row)
        {
            return;
        }

        var kind = b.CommandParameter is DiscoveryBrowseKind k
            ? k
            : row.Field.BrowseKind;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        string? picked;
        if (kind == DiscoveryBrowseKind.File)
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { AllowMultiple = false });
            picked = files is { Count: > 0 } ? files[0].Path.LocalPath : null;
        }
        else
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { AllowMultiple = false });
            picked = folders is { Count: > 0 } ? folders[0].Path.LocalPath : null;
        }

        if (!string.IsNullOrEmpty(picked))
        {
            row.Value = picked;
        }
    }

    /// <summary>
    /// The ModsFolder Browse button: opens a single-select folder picker and
    /// forwards the picked path to the VM's
    /// <see cref="SettingsViewModel.ApplyModsFolderCommand"/> (which runs the
    /// relocate flow + surfaces any failure as a StatusMessage under the field).
    /// </summary>
    private async void BrowseModsFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders is null || folders.Count == 0)
        {
            return;
        }

        var picked = folders[0].Path.LocalPath;
        vm.ApplyModsFolderCommand.Execute(picked);
    }
}
