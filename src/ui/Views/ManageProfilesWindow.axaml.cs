using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Modificus.Curator.UI.ViewModels;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The "Manage profiles…" modal dialog (editable-list pattern). Its
/// <c>DataContext</c> is a <see cref="ManageProfilesViewModel"/> (set by
/// <see cref="Dialogs.DialogService"/>). CRUD is applied immediately per action
/// (pencil inline rename / trash delete-confirm / "+ New profile" create); "Done"
/// closes the window.
/// </summary>
/// <remarks>
/// The per-row pencil / trash buttons use code-behind <c>Click</c> handlers
/// (rather than a compiled binding up to the parent VM) because reaching the
/// parent <c>DataContext</c> from inside an <c>ItemTemplate</c> is the one
/// non-trivial binding here; the handlers simply resolve the row from
/// <see cref="Avalonia.Controls.Button.DataContext"/> and forward to the
/// matching VM command. The inline-edit key gestures (Enter commit / Esc
/// cancel) + commit-on-blur live here too: they are pure view mechanics over
/// the VM's commit/cancel commands. All state + CRUD stays in the
/// (unit-tested) VM.
/// </remarks>
public partial class ManageProfilesWindow : Window
{
    public ManageProfilesWindow()
    {
        InitializeComponent();
    }

    private ManageProfilesViewModel? ViewModel => DataContext as ManageProfilesViewModel;

    /// <summary>
    /// Detaches the VM's session subscription on close so the short-lived dialog
    /// VM is collectable (the session is a singleton that outlives the dialog).
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Detach();
        base.OnClosed(e);
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => Close();

    // ---- per-row action buttons -------------------------------------------

    private void Rename_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ProfileItemViewModel item)
        {
            ViewModel?.StartRenameCommand.Execute(item);
        }
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ProfileItemViewModel item)
        {
            // AsyncRelayCommand.Execute forwards to ExecuteAsync.
            ViewModel?.DeleteProfileCommand.Execute(item);
        }
    }

    private void LaunchSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ProfileItemViewModel item)
        {
            // Async fire-and-forget: the command opens the launch-settings modal
            // (awaited inside the command). The void return mirrors the existing
            // row-action handlers; exceptions are surfaced through the dialog.
            ViewModel?.EditLaunchSettingsCommand.Execute(item);
        }
    }

    // ---- inline rename key/focus handling ---------------------------------

    private void EditBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not ProfileItemViewModel item)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                ViewModel?.CommitRenameCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.Escape:
                ViewModel?.CancelRenameCommand.Execute(item);
                e.Handled = true;
                break;
        }
    }

    private void EditBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        // commit-on-blur; no-op if already exited edit (e.g. via Enter/Esc),
        // because the edit flag has already flipped to false.
        if (sender is TextBox box && box.DataContext is ProfileItemViewModel item)
        {
            ViewModel?.CommitRenameCommand.Execute(item);
        }
    }

    // ---- add-row key/focus handling ---------------------------------------

    private void AddBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ViewModel?.CommitCreateCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                ViewModel?.CancelCreateCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void AddBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        // commit-on-blur; no-op if not adding (e.g. after Enter/Esc), because
        // IsAddingNew has already flipped to false.
        ViewModel?.CommitCreateCommand.Execute(null);
    }
}
