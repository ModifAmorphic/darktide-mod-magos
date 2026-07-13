using Avalonia.Controls;
using Avalonia.Interactivity;
using Modificus.Curator.UI.Dialogs;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// The first-run Welcome modal. Caller awaits
/// <see cref="Window.ShowDialog(Avalonia.Controls.Window)"/> and reads
/// <see cref="Result"/> for the user's choice. The default result is
/// <see cref="WelcomeChoice.Continue"/> so ESC, the title-bar close button, and
/// a window close all behave as Continue without Nexus (no setup).
/// </summary>
public partial class WelcomeWindow : Window
{
    /// <summary>
    /// The user's first-run Welcome choice. Defaults to
    /// <see cref="WelcomeChoice.Continue"/> (ESC / close / window-close
    /// equivalent).
    /// </summary>
    public WelcomeChoice Result { get; private set; } = WelcomeChoice.Continue;

    public WelcomeWindow()
    {
        InitializeComponent();
    }

    private void SetUpNexus_Click(object? sender, RoutedEventArgs e)
    {
        Result = WelcomeChoice.SetUpNexus;
        Close();
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        Result = WelcomeChoice.Continue;
        Close();
    }
}
