using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// The "Preferences" modal dialog (theme picker / font-scale slider / language
/// dropdown). Its <c>DataContext</c> is a <see cref="ViewModels.PreferencesViewModel"/>
/// (set by <see cref="Dialogs.DialogService"/>). Each control applies + persists
/// immediately through the VM (no commit step), so "Done" just closes the window.
/// </summary>
public partial class PreferencesWindow : Window
{
    public PreferencesWindow()
    {
        InitializeComponent();
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => Close();
}
