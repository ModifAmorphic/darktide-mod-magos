using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The per-profile launch-settings modal window. Its <c>DataContext</c> is a
/// <see cref="LaunchSettingsViewModel"/> (set by
/// <see cref="Dialogs.DialogService"/>). Save runs the VM's
/// <see cref="LaunchSettingsViewModel.SaveCommand"/> (persists via
/// <see cref="LaunchSettingsViewModel"/>'s <c>SetLaunchSettings</c> + sets
/// <see cref="LaunchSettingsViewModel.SaveResult"/>); the window closes only on
/// a true result. Cancel / ESC / title-bar close close without saving. The
/// per-row remove buttons resolve their row from the button's
/// <see cref="Avalonia.Controls.Button.DataContext"/> and forward to the
/// matching VM command (the same pattern as ManageProfilesWindow).
/// </summary>
/// <remarks>
/// All validation + persistence lives in the (unit-tested)
/// <see cref="LaunchSettingsViewModel"/>; this window only wires buttons to the
/// VM's commands + close.
/// </remarks>
public partial class LaunchSettingsWindow : Window
{
    public LaunchSettingsWindow()
    {
        InitializeComponent();
    }

    private LaunchSettingsViewModel? ViewModel => DataContext as LaunchSettingsViewModel;

    /// <summary>
    /// Save: runs the VM's save command (sets <see cref="LaunchSettingsViewModel.SaveResult"/>
    /// on success), then closes only on a true result. The Save button is
    /// disabled while invalid, so the close path runs only on a real save; the
    /// VM re-checks <c>CanSave</c> defensively.
    /// </summary>
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            Close();
            return;
        }

        vm.SaveCommand.Execute(null);
        if (vm.SaveResult)
        {
            Close();
        }
    }

    /// <summary>Cancel: closes with <see cref="LaunchSettingsViewModel.SaveResult"/>
    /// still false (no persist).</summary>
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    // ---- per-row remove buttons -------------------------------------------

    private void RemoveEnv_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is EnvVarRow row)
        {
            ViewModel?.RemoveEnvVarCommand.Execute(row);
        }
    }

    private void RemoveArg_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is GameArgRow row)
        {
            ViewModel?.RemoveGameArgCommand.Execute(row);
        }
    }
}
