using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Modificus.Curator.UI.Settings;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The discovery escape-hatch modal window. Its <c>DataContext</c> is a
/// <see cref="DiscoveryEscapeHatchViewModel"/> (set by
/// <see cref="Dialogs.DialogService"/>). The dialog reads
/// <see cref="DiscoveryEscapeHatchViewModel.Result"/> after <c>ShowDialog</c>
/// returns: <c>true</c> means the user submitted (the entered paths are now
/// persisted), <c>false</c> means they cancelled (no writes). No auto-retry:
/// the user clicks Launch again to retry after submitting.
/// </summary>
/// <remarks>
/// All persistence logic lives in the (unit-tested) VM; this is pure view
/// mechanics. The browse button opens the row's matching picker (folder / file)
/// and sets the row's <c>Value</c> directly. Submit + Cancel forward to the
/// VM's matching commands and close.
/// </remarks>
public partial class DiscoveryEscapeHatchDialog : Window
{
    public DiscoveryEscapeHatchDialog()
    {
        InitializeComponent();
    }

    private DiscoveryEscapeHatchViewModel? ViewModel => DataContext as DiscoveryEscapeHatchViewModel;

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

    /// <summary>
    /// Submit: runs the VM's <see cref="DiscoveryEscapeHatchViewModel.SubmitCommand"/>
    /// (which marks <see cref="DiscoveryEscapeHatchViewModel.Result"/> true after
    /// persisting the entered paths) and closes.
    /// </summary>
    private void Submit_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.SubmitCommand.Execute(null);
        Close();
    }

    /// <summary>Cancel: runs the VM's Cancel command (marks Result false) and
    /// closes without a write.</summary>
    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel?.CancelCommand.Execute(null);
        Close();
    }

    /// <summary>
    /// A row's Browse button. The button carries the field's
    /// <see cref="DiscoveryBrowseKind"/> as its <c>CommandParameter</c>; this
    /// opens the matching picker, takes the first selection, and sets the row's
    /// <c>Value</c>.
    /// </summary>
    private async void Browse_Click(object? sender, RoutedEventArgs e)
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
}
