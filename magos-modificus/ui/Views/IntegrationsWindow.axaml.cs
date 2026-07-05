using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Magos.Modificus.UI.ViewModels;

namespace Magos.Modificus.UI.Views;

/// <summary>
/// The Integrations modal window. Its <c>DataContext</c> is an
/// <see cref="IntegrationsViewModel"/> (set by <see cref="Dialogs.DialogService"/>).
/// Nexus-only in v1; the VM owns all auth state + the OAuth/API-key flows.
/// </summary>
/// <remarks>
/// The view is pure mechanics: the Done button closes, the API-key help link
/// opens the user's browser at the Nexus API-keys page (via
/// <see cref="Process.Start(ProcessStartInfo)"/> with <c>UseShellExecute=true</c>,
/// the same shell-open pattern the OAuth browser launcher uses). All persistence
/// + network logic lives in the (unit-tested) VM + <c>NexusAuthService</c>.
/// </remarks>
public partial class IntegrationsWindow : Window
{
    public IntegrationsWindow()
    {
        InitializeComponent();
    }

    private IntegrationsViewModel? ViewModel => DataContext as IntegrationsViewModel;

    /// <summary>
    /// Detaches the VM's subscriptions on close so the short-lived dialog VM is
    /// collectable (the session + localization service are singletons that
    /// outlive the dialog).
    /// </summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Refresh the status line on open (resolves the verified account state
        // server-side). Fire-and-forget so the window paints immediately.
        if (ViewModel is { } vm)
        {
            await vm.RefreshAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Detach();
        base.OnClosed(e);
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Opens the Nexus API-keys page in the user's default browser. The user
    /// gets their API key from there for the alternative (API-key) auth path.
    /// <c>UseShellExecute = true</c> is correct here (opening a URL via the OS
    /// shell-open), the same pattern the OAuth browser launcher uses.
    /// </summary>
    private void ApiKeyHelp_Click(object? sender, RoutedEventArgs e)
    {
        const string helpUrl = "https://www.nexusmods.com/settings/api-keys";
        try
        {
            Process.Start(new ProcessStartInfo(helpUrl) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: a shell-open failure (no default browser, headless
            // test env) is non-fatal. The button's tooltip carries the URL so
            // the user can copy it manually.
        }
    }
}
