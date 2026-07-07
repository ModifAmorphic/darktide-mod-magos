using Avalonia.Controls;

namespace Modificus.Curator.UI.Views;

/// <summary>
/// A button-less modal spinner shown while a short async operation (the DMF
/// download) is in flight. The caller sets the message via
/// <see cref="SetMessage"/> then awaits <see cref="Window.ShowDialog(Avalonia.Controls.Window)"/>;
/// the caller also closes the window from the work's continuation (there is no
/// user affordance to close it: a partial DMF archive is useless, so the
/// download cannot be cancelled mid-flight).
/// </summary>
/// <remarks>
/// Reuses the shared <c>DialogTitleBar</c> chrome with its close button hidden
/// (the title bar is the drag region; the user cannot dismiss the spinner).
/// Used by <c>DialogService.ShowProgressAsync</c> for the DMF download.
/// </remarks>
public partial class ProgressDialog : Window
{
    public ProgressDialog()
    {
        InitializeComponent();
    }

    /// <summary>Sets the explanatory message shown above the spinner.</summary>
    public void SetMessage(string message) => MessageText.Text = message;
}
