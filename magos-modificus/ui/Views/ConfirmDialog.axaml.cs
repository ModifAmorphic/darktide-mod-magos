using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// A minimal yes/no confirmation dialog. Caller sets the message via
/// <see cref="SetMessage"/> then awaits <see cref="Window.ShowDialog(Avalonia.Controls.Window)"/>;
/// <see cref="Result"/> holds the outcome (<c>true</c> = confirm).
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary><c>true</c> when the user confirmed; <c>false</c> otherwise.</summary>
    public bool Result { get; private set; }

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>Sets the explanatory message shown above the buttons.</summary>
    public void SetMessage(string message) => MessageText.Text = message;

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
