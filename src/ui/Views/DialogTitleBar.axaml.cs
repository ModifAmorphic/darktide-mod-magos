using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The shared custom title bar for the modal dialogs (chrome + visible title +
/// drawn close button). The title text mirrors the owning
/// <see cref="Window.Title"/> (bound in XAML); the close button closes that
/// window. See DialogTitleBar.axaml for the chrome / drag-region / theming notes.
/// </summary>
public partial class DialogTitleBar : UserControl
{
    /// <summary>
    /// Whether the drawn close button is visible. Default <c>true</c> (every
    /// existing dialog shows it). The progress-spinner dialog sets this to
    /// <c>false</c> so the user cannot dismiss an in-flight operation whose
    /// partial result would be useless (e.g. the DMF download).
    /// </summary>
    public static readonly StyledProperty<bool> ShowCloseProperty =
        AvaloniaProperty.Register<DialogTitleBar, bool>(nameof(ShowClose), defaultValue: true);

    public bool ShowClose
    {
        get => GetValue(ShowCloseProperty);
        set => SetValue(ShowCloseProperty, value);
    }

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
