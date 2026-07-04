using Avalonia.Controls;
using Avalonia.Interactivity;
using Magos.Modificus.UI.ViewModels;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// The per-mod import modal window. Its <c>DataContext</c> is an
/// <see cref="ImportModViewModel"/> (set by
/// <see cref="Dialogs.DialogService"/>). OK runs the VM's
/// <see cref="ImportModViewModel.ConfirmCommand"/> (parses the URL to canonical
/// source + records the version; disabled until valid) and closes when a result is
/// produced; Cancel closes with a <c>null</c> result (cancels the add batch).
/// </summary>
/// <remarks>
/// All validation + parsing lives in the (unit-tested)
/// <see cref="ImportModViewModel"/>; this window only wires buttons to the VM's
/// confirm command + close. The <see cref="Dialogs.DialogService"/> reads
/// <see cref="ImportModViewModel.Result"/> after <c>ShowDialog</c> returns.
/// </remarks>
public partial class ImportModDialog : Window
{
    public ImportModDialog()
    {
        InitializeComponent();
    }

    private ImportModViewModel? ViewModel => DataContext as ImportModViewModel;

    /// <summary>
    /// OK: runs the VM's confirm command (sets <see cref="ImportModViewModel.Result"/>
    /// when the input is valid), then closes. The OK button is disabled while
    /// invalid, so the close path runs only on a real confirm; the VM re-checks
    /// <c>CanConfirm</c> defensively.
    /// </summary>
    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            Close();
            return;
        }

        vm.ConfirmCommand.Execute(null);
        if (vm.Result is not null)
        {
            Close();
        }
    }

    /// <summary>Cancel: closes with no result (<see cref="ImportModViewModel.Result"/>
    /// stays <c>null</c>), signalling the add flow to cancel the remaining
    /// batch.</summary>
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
