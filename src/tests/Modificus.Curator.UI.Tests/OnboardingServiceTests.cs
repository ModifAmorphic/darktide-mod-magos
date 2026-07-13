using Microsoft.Extensions.Logging;
using Modificus.Curator.General;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Behaviors of the first-run <see cref="OnboardingService"/>: the no-op when
/// onboarding is already complete, the two Welcome choices (Continue vs. Set up
/// Nexus), persistence-before-Integrations ordering, the close == Continue
/// equivalence, and the in-process one-shot guard.
/// </summary>
/// <remarks>
/// Uses the shared <see cref="FakeAppStateStore"/> + <see cref="FakeDialogService"/>
/// doubles. The Integrations-open delegate is a recording stub (no real shell
/// involved) so the tests can assert whether + when + how many times it ran.
/// </remarks>
public sealed class OnboardingServiceTests
{
    private static readonly ILogger<OnboardingService> Logger = NullLogger<OnboardingService>.Instance;

    [Fact]
    public async Task Already_completed_is_a_noop()
    {
        var state = new FakeAppStateStore { OnboardingCompleted = true };
        var dialogs = new FakeDialogService();
        var integrationsRuns = 0;
        Func<Task> openIntegrations = () => { integrationsRuns++; return Task.CompletedTask; };

        var service = new OnboardingService(state, dialogs, openIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(0, dialogs.WelcomeCalls);
        Assert.Equal(0, integrationsRuns);
        // Remains complete.
        Assert.True(state.OnboardingCompleted);
    }

    [Fact]
    public async Task Continue_persists_and_does_not_open_integrations()
    {
        var state = new FakeAppStateStore(); // OnboardingCompleted defaults false
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.Continue,
        };
        var integrationsRuns = 0;
        Func<Task> openIntegrations = () => { integrationsRuns++; return Task.CompletedTask; };

        var service = new OnboardingService(state, dialogs, openIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted
        Assert.Equal(0, integrationsRuns); // no Integrations
    }

    [Fact]
    public async Task SetUpNexus_persists_before_opening_integrations_once()
    {
        var state = new FakeAppStateStore(); // OnboardingCompleted defaults false
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.SetUpNexus,
        };
        var integrationsRuns = 0;
        bool? completedWhenIntegrationsRan = null;
        Func<Task> openIntegrations = () =>
        {
            integrationsRuns++;
            // Capture the persisted state at the moment Integrations is opened.
            completedWhenIntegrationsRan = state.OnboardingCompleted;
            return Task.CompletedTask;
        };

        var service = new OnboardingService(state, dialogs, openIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted
        Assert.Equal(1, integrationsRuns); // opened exactly once
        // Ordering guarantee: onboarding was ALREADY persisted when Integrations
        // began, so canceling Integrations can never cause Welcome to repeat.
        Assert.True(completedWhenIntegrationsRan);
    }

    [Fact]
    public async Task Close_result_behaves_as_continue()
    {
        // The default WelcomeResult on FakeDialogService is Continue, which is
        // also what ESC / title-bar close / window close map to (the
        // WelcomeWindow default Result). This mirrors a close without an
        // explicit Continue click.
        var state = new FakeAppStateStore();
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.Continue, // the close equivalent
        };
        var integrationsRuns = 0;
        Func<Task> openIntegrations = () => { integrationsRuns++; return Task.CompletedTask; };

        var service = new OnboardingService(state, dialogs, openIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted even on close
        Assert.Equal(0, integrationsRuns); // no Integrations on close
    }

    [Fact]
    public async Task Repeated_call_in_same_process_is_a_noop()
    {
        // The in-process guard suppresses a second show even if the persisted
        // flag could not be written (best-effort persistence).
        var state = new FakeAppStateStore();
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.Continue,
        };
        var integrationsRuns = 0;
        Func<Task> openIntegrations = () => { integrationsRuns++; return Task.CompletedTask; };

        var service = new OnboardingService(state, dialogs, openIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();
        Assert.Equal(1, dialogs.WelcomeCalls);

        // Second call in the same process: no-op.
        await service.ShowWelcomeIfFirstRunAsync();
        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.Equal(0, integrationsRuns);
    }

    [Fact]
    public async Task SetUpNexus_integrations_failure_does_not_crash_or_unpersist()
    {
        // The Integrations flow is the shell's; if it throws, onboarding is
        // already persisted so the Welcome will not repeat. The exception is
        // swallowed so startup continues.
        var state = new FakeAppStateStore();
        var dialogs = new FakeDialogService
        {
            WelcomeResult = WelcomeChoice.SetUpNexus,
        };
        Func<Task> openIntegrations = () => Task.FromException(new InvalidOperationException("boom"));

        var service = new OnboardingService(state, dialogs, openIntegrations, Logger);

        await service.ShowWelcomeIfFirstRunAsync();

        Assert.Equal(1, dialogs.WelcomeCalls);
        Assert.True(state.OnboardingCompleted); // persisted before the failure
    }
}
