using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// The shared custom title bar for the modal dialogs (chrome + visible title +
/// drawn close button). The title text mirrors the owning
/// <see cref="Window.Title"/> (bound in XAML); the close button closes that
/// window. See DialogTitleBar.axaml for the chrome / drag-region / theming notes.
/// </summary>
public partial class DialogTitleBar : UserControl
{
    public DialogTitleBar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Closes the owning window. Reached from the title bar regardless of which
    /// dialog hosts it (the title bar is reused across all three).
    /// </summary>
    private void Close_Click(object? sender, RoutedEventArgs e)
        => this.FindAncestorOfType<Window>()?.Close();
}
