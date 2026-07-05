using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// A minimal yes/no confirmation dialog. Caller sets the message via
/// <see cref="SetMessage"/> then awaits <see cref="Window.ShowDialog(Avalonia.Controls.Window)"/>;
/// <see cref="Result"/> holds the outcome (<c>true</c> = confirm). When
/// <see cref="ShowCancel"/> is <c>false</c>, the Cancel button is hidden + the
/// dialog collapses to a single-button alert (used by the launch-error alert;
/// see <c>DialogService.ShowAlertAsync</c>).
/// </summary>
public partial class ConfirmDialog : Window
{
    /// <summary><c>true</c> when the user confirmed; <c>false</c> otherwise.</summary>
    public bool Result { get; private set; }

    /// <summary>
    /// Whether the Cancel button is visible. Default <c>true</c> (the
    /// confirmation mode). When <c>false</c>, the dialog is a single-button
    /// alert: Cancel is collapsed + OK is the only affordance. Set before
    /// <c>ShowDialog</c> (the alert reuses the confirm chrome). Applied to the
    /// button on <see cref="OnOpened"/> (the named control is resolved then).
    /// </summary>
    public bool ShowCancel { get; set; } = true;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>Sets the explanatory message shown above the buttons.</summary>
    public void SetMessage(string message) => MessageText.Text = message;

    /// <summary>
    /// Applies <see cref="ShowCancel"/> to the Cancel button's visibility once
    /// the window is open + its content is realized. (The named CancelButton is
    /// part of the XAML content, so it is resolved here rather than in the
    /// constructor before the dialog is shown.)
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (this.GetControl<Button>("CancelButton") is { } cancel)
        {
            cancel.IsVisible = ShowCancel;
        }
    }

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
