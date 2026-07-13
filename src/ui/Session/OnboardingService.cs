using Modificus.Curator.General;
using Modificus.Curator.UI.Dialogs;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The first-run Welcome onboarding coordinator. Shows the Welcome modal once,
/// the first time the app starts with <see cref="IAppStateStore.OnboardingCompleted"/>
/// still <c>false</c>, persists completion, and opens the Integrations dialog
/// when the user chooses "Set up Nexus". After the first run, the call is a
/// no-op for the lifetime of the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>One-shot, persisted.</b> <see cref="ShowWelcomeIfFirstRunAsync"/> reads
/// the persisted <see cref="IAppStateStore.OnboardingCompleted"/> flag; when it
/// is already <c>true</c> (a returning user, or a second call in the same
/// process after the first run persisted it) the method returns without showing
/// anything. The completion flag is persisted BEFORE the Integrations dialog is
/// opened, so canceling or closing Integrations can never cause the Welcome to
/// repeat.</para>
/// <para>
/// <b>Owner window must be open.</b> Avalonia modal dialogs require a shown
/// owner, so the shell / App wires this call after the main window opens. The
/// coordinator itself is UI-thread-affine (no <c>ConfigureAwait(false)</c>,
/// per the UI-layer rule) and stays testable through the
/// <see cref="IDialogService.ShowWelcomeAsync"/> + state-store seams.</para>
/// <para>
/// <b>The Integrations step reuses the shell's full flow.</b> The
/// <c>openIntegrations</c> delegate is supplied by the composition root and
/// resolves to the shell's <c>OpenIntegrationsAsync</c> method, so enabling the
/// <c>nxm://</c> handler inside Integrations refreshes the shell status after
/// the dialog closes. The coordinator never constructs the Integrations window
/// itself.</para>
/// <para>
/// <b>Registered as a singleton</b> so the in-process <see cref="_shown"/> guard
/// reliably suppresses a second show even if persistence is best-effort and
/// fails to record. The persisted flag is the durable signal; the in-memory
/// guard is the within-process guarantee.</para>
/// </remarks>
public sealed class OnboardingService
{
    private readonly IAppStateStore _appState;
    private readonly IDialogService _dialogs;
    private readonly Func<Task> _openIntegrations;
    private readonly ILogger<OnboardingService> _logger;

    // In-process guard: once the Welcome has been shown this session, never show
    // it again even if the persisted flag could not be written (best-effort
    // persistence). Read + written on the UI thread only (the coordinator is
    // called once at startup from the main window's Opened handler).
    private bool _shown;

    public OnboardingService(
        IAppStateStore appState,
        IDialogService dialogs,
        Func<Task> openIntegrations,
        ILogger<OnboardingService> logger)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _openIntegrations = openIntegrations ?? throw new ArgumentNullException(nameof(openIntegrations));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Shows the Welcome modal on the first run only. No-op when onboarding is
    /// already complete (persisted flag set, or already shown in this process).
    /// On a <see cref="WelcomeChoice.SetUpNexus"/> choice, persists completion
    /// first, then opens the Integrations dialog (the shell's full flow) so
    /// enabling the <c>nxm://</c> handler inside Integrations refreshes the
    /// shell status. On <see cref="WelcomeChoice.Continue"/> (explicit button,
    /// ESC, or close) persists completion and returns, leaving the user at the
    /// main window.
    /// </summary>
    public async Task ShowWelcomeIfFirstRunAsync()
    {
        // Already done: a returning user, or a second call in this process.
        // Both the persisted flag and the in-process guard suppress the show.
        if (_shown || _appState.OnboardingCompleted)
        {
            return;
        }

        _shown = true;

        var choice = await _dialogs.ShowWelcomeAsync();

        // Persist completion BEFORE opening Integrations so closing Integrations
        // (or it failing) can never cause Welcome to repeat on the next launch.
        _appState.OnboardingCompleted = true;

        if (choice == WelcomeChoice.SetUpNexus)
        {
            try
            {
                await _openIntegrations();
            }
            catch (Exception ex)
            {
                // The Integrations flow is the shell's; a failure there is
                // unexpected (the dialog is already gone). Onboarding is already
                // persisted, so log + continue rather than re-showing Welcome.
                _logger.LogError(ex, "Opening Integrations after the Welcome choice failed.");
            }
        }
    }
}
