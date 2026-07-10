using Modificus.Curator.UI.AppUpdate;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="AppUpdateCheckRunner"/> unit tests: <see cref="AppUpdateCheckRunner.Start"/>
/// fires exactly one self-update availability check, fire-and-forget; a throwing
/// <see cref="IAppUpdateService.CheckForUpdatesAsync"/> is swallowed (does not
/// propagate out of Start and does not leave an unobserved task exception); and
/// cancellation is swallowed silently. The runner dispatches its check on a
/// thread-pool task, so the tests coordinate timing against a recording fake
/// service (mirrors <see cref="UpdateCheckRunnerTests"/>'s pattern).
/// </summary>
/// <remarks>
/// Each assertion is gated by <see cref="WaitAsync"/>, which polls the recorded
/// call count on a short delay until the expected call lands or a 2s timeout
/// elapses (then asserts the condition held), so a missing fire surfaces as a
/// deterministic test failure rather than a flaky stale-pass. Unobserved task
/// exceptions surface via <see cref="TaskScheduler.UnobservedTaskException"/>,
/// which the exception-safety test checks after GC-collecting the faulted task.
/// </remarks>
public sealed class AppUpdateCheckRunnerTests
{
    [Fact]
    public async Task Start_fires_one_check_fire_and_forget()
    {
        var service = new FakeAppUpdateService();
        var runner = Build(service);

        // Start() must return immediately (fire-and-forget); the call lands on a
        // thread-pool task. Start must not throw.
        runner.Start();

        await WaitAsync(() => service.CheckCallCount == 1);
        Assert.Equal(1, service.CheckCallCount);
    }

    [Fact]
    public async Task Start_does_not_fire_more_than_once()
    {
        // App self-update is a single check per startup (no periodic polling).
        var service = new FakeAppUpdateService();
        var runner = Build(service);

        runner.Start();
        await WaitAsync(() => service.CheckCallCount == 1);

        // Wait beyond the fire; no second call should land.
        await Task.Delay(100);
        Assert.Equal(1, service.CheckCallCount);
    }

    [Fact]
    public async Task CheckAsync_throwing_is_swallowed_and_does_not_escape_Start()
    {
        // The fire-and-forget Task.Run must catch a throwing CheckForUpdatesAsync
        // (the service is documented to self-catch, but the runner wraps it as
        // belt-and-suspenders). The throw must not escape Start, and must not
        // surface as an unobserved task exception.
        var service = new FakeAppUpdateService
        {
            ThrowOnCheck = new InvalidOperationException("boom"),
        };
        var runner = Build(service);

        // Must not throw; the exception happens inside the thread-pool task.
        runner.Start();
        await WaitAsync(() => service.CheckCallCount == 1);

        // Drain any unobserved task exception and assert none was raised.
        await AssertNoUnobservedExceptionAsync();
        Assert.Equal(1, service.CheckCallCount);
    }

    [Fact]
    public async Task OperationCanceledException_is_swallowed_silently()
    {
        // Cancellation is expected on shutdown (not an error): it must be
        // swallowed silently, never escaping Start and never surfacing as an
        // unobserved exception.
        var service = new FakeAppUpdateService
        {
            ThrowOnCheck = new OperationCanceledException(),
        };
        var runner = Build(service);

        runner.Start();
        await WaitAsync(() => service.CheckCallCount == 1);

        await AssertNoUnobservedExceptionAsync();
        Assert.Equal(1, service.CheckCallCount);
    }

    [Fact]
    public async Task Start_does_not_fire_when_check_on_startup_is_disabled()
    {
        // The toggle gates ONLY the automatic startup check. When it is off,
        // Start() logs and returns without firing CheckForUpdatesAsync.
        var service = new FakeAppUpdateService();
        var configLoader = new FakeConfigLoader();
        configLoader.Config.AppUpdates.CheckOnStartup = false;
        var runner = Build(service, configLoader);

        runner.Start();

        // Give the (non-)fire a chance to land; the count must stay 0.
        await Task.Delay(100);
        Assert.Equal(0, service.CheckCallCount);
    }

    [Fact]
    public async Task Start_fires_when_check_on_startup_is_enabled()
    {
        // The default is enabled; confirm the toggle-on path still fires.
        var service = new FakeAppUpdateService();
        var configLoader = new FakeConfigLoader();
        configLoader.Config.AppUpdates.CheckOnStartup = true;
        var runner = Build(service, configLoader);

        runner.Start();

        await WaitAsync(() => service.CheckCallCount == 1);
        Assert.Equal(1, service.CheckCallCount);
    }

    // ---- helpers -----------------------------------------------------------

    private static AppUpdateCheckRunner Build(
        FakeAppUpdateService service, FakeConfigLoader? configLoader = null) =>
        new(service, configLoader ?? new FakeConfigLoader(), NullLogger<AppUpdateCheckRunner>.Instance);

    /// <summary>
    /// Polls <paramref name="condition"/> on a short delay until it returns
    /// <c>true</c> or a 2s timeout elapses. The runner fires its check on a
    /// thread-pool task, so tests must wait for that task to actually invoke the
    /// fake service before asserting. Asserts the condition held at the end so a
    /// timeout surfaces as a deterministic failure rather than a stale-pass.
    /// </summary>
    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (!condition())
            {
                await Task.Delay(10, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout elapsed; fall through to the assertion below.
        }

        Assert.True(condition(), "Timed out waiting for the runner to fire the self-update check.");
    }

    /// <summary>
    /// Forces the faulted task to be GC-collected + processes the
    /// <see cref="TaskScheduler.UnobservedTaskException"/> queue, then asserts no
    /// unobserved exception was recorded. The runner discards its Task (fire and
    /// forget), so any exception that escaped its belt-and-suspenders catch
    /// would surface here. A pass means the runner swallowed the exception.
    /// </summary>
    private static async Task AssertNoUnobservedExceptionAsync()
    {
        Exception? unobserved = null;
        void Handler(object? s, UnobservedTaskExceptionEventArgs e)
        {
            unobserved = e.Exception;
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            // Let the faulted task go out of scope + collect it so the
            // UnobservedTaskException event fires for any exception that escaped.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield(); // let the scheduler run its event callbacks
            Assert.Null(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }
}
