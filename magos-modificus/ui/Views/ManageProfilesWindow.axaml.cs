using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// The "Manage profiles…" modal dialog. Its <c>DataContext</c> is a
/// <see cref="ViewModels.ManageProfilesViewModel"/> (set by
/// <see cref="Dialogs.DialogService"/>). CRUD is applied immediately per
/// action; "Done" simply closes the window.
/// </summary>
public partial class ManageProfilesWindow : Window
{
    public ManageProfilesWindow()
    {
        InitializeComponent();
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => Close();
}
