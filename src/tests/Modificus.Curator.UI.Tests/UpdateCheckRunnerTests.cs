using System.ComponentModel;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// <see cref="UpdateCheckRunner"/> unit tests: the trigger points (startup with
/// a restored active id, an active-profile switch, the periodic timer, and the
/// manual <see cref="UpdateCheckRunner.CheckNow"/>), the non-triggers (null id,
/// non-<see cref="IProfileSession.ActiveProfileId"/> property changes, the
/// toggle off, no active profile), the interval math, and the fire-and-forget
/// exception safety (a throwing <see cref="IUpdateCheckService.CheckAsync"/> is
/// swallowed; the runner survives and keeps firing on the next switch).
/// </summary>
/// <remarks>
/// <para>
/// The runner dispatches each check on a thread-pool task (fire-and-forget), so
/// the tests coordinate timing against a recording fake service. Each assertion
/// is gated by <see cref="WaitAsync"/>, which polls the recorded call count on a
/// short delay until the expected call lands or a 2s timeout elapses (then
/// asserts the condition held), so a missing fire surfaces as a deterministic
/// test failure rather than a flaky stale-pass.</para>
/// <para>
/// The fake session is the shared <see cref="FakeProfileSession"/> from
/// <c>TestDoubles.cs</c>: its <see cref="FakeProfileSession.ActiveProfileId"/>
/// setter raises <see cref="INotifyPropertyChanged.PropertyChanged"/> via
/// <c>ObservableObject</c>'s <c>SetProperty</c>, mirroring how the real
/// <see cref="ProfileSession"/> raises it (the
/// <c>[ObservableProperty]</c> source generator).</para>
/// <para>
/// The periodic timer is injected as a capturing <c>startTimer</c> delegate so
/// the test invokes the captured tick directly (no real timer). The clock is
/// injected as a controllable <c>getNow</c> so the interval math is deterministic
/// without real-time delays.</para>
/// </remarks>
public sealed class UpdateCheckRunnerTests
{
    // ---- startup trigger ---------------------------------------------------

    [Fact]
    public async Task Start_with_active_profile_fires_check_once()
    {
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);

        runner.Start();

        await WaitAsync(() => service.CallCount == 1);
        Assert.Equal(id, Assert.Single(service.Calls));
    }

    [Fact]
    public async Task Start_with_no_active_profile_does_not_fire()
    {
        // No id restored: Start() must not fire. The only fire path in Start is
        // the explicit "id already present" check, which short-circuits on
        // null. A short delay confirms no async fire lands.
        var session = new FakeProfileSession(); // ActiveProfileId stays null
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);

        runner.Start();

        await Task.Delay(50);
        Assert.Empty(service.Calls);
    }

    // ---- profile-switch trigger --------------------------------------------

    [Fact]
    public async Task Profile_switch_fires_check_for_the_new_id()
    {
        var newId = Guid.NewGuid();
        var session = new FakeProfileSession(); // null at start
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);
        runner.Start();
        await Task.Delay(50); // confirm zero startup calls (none expected)

        session.ActiveProfileId = newId; // raises PropertyChanged

        await WaitAsync(() => service.CallCount == 1);
        Assert.Equal(newId, Assert.Single(service.Calls));
    }

    [Fact]
    public async Task Switch_to_null_does_not_fire_a_second_check()
    {
        // Delete-of-active clears the selection (null). That is not a load:
        // the runner must not fire for a null id. Only the opening startup
        // call should be recorded.
        var firstId = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = firstId };
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);
        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        session.ActiveProfileId = null; // cleared, not switched

        await Task.Delay(50);
        Assert.Single(service.Calls); // still just the startup call
    }

    // ---- non-trigger -------------------------------------------------------

    [Fact]
    public async Task IsRunning_change_does_not_fire_a_check()
    {
        // The polling timer drives IsRunning changes every few seconds. Those
        // are not profile loads; the runner must ignore them. Start fires the
        // one startup check, then the IsRunning change adds nothing.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);
        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        session.IsRunning = true; // polling-timer driven, not a profile load

        await Task.Delay(50);
        Assert.Single(service.Calls); // still just the startup call
    }

    // ---- periodic timer ---------------------------------------------------

    [Fact]
    public async Task Timer_tick_fires_check_when_enabled_and_profile_active()
    {
        // The periodic tick fires a check when: the toggle is on, a profile is
        // active, and the configured interval has elapsed since the last check.
        // Start() fires the opening check (stamps _lastCheckAt = t0); the
        // first tick at t0 does not re-fire (no time elapsed); advancing the
        // clock past the interval makes the next tick fire.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, getNow: () => now);

        runner.Start();
        await WaitAsync(() => service.CallCount == 1); // the startup fire

        // Tick immediately: no interval elapsed since startup -> no new fire.
        tick();
        await Task.Delay(50);
        Assert.Single(service.Calls);

        // Advance past the default 10-minute interval -> the next tick fires.
        now = now.AddMinutes(11);
        tick();
        await WaitAsync(() => service.CallCount == 2);
    }

    [Fact]
    public async Task Timer_tick_does_not_fire_when_toggle_is_off()
    {
        // The toggle gates ONLY the periodic timer. With it off, ticks (even
        // past the interval) fire nothing. The startup check still ran.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var configLoader = new FakeConfigLoader();
        configLoader.Config.Integrations.Nexus.AutoUpdateCheckEnabled = false;
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, configLoader, () => now);

        runner.Start();
        await WaitAsync(() => service.CallCount == 1); // startup (always)

        now = now.AddMinutes(20); // well past the interval
        tick();
        await Task.Delay(50);
        Assert.Single(service.Calls); // only the startup fire
    }

    [Fact]
    public async Task Timer_tick_does_not_fire_when_no_profile_is_active()
    {
        // No active profile -> nothing to check. The periodic tick is a no-op.
        // (Start fires nothing here either, since the session has no id.)
        var session = new FakeProfileSession(); // null
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, getNow: () => now);

        runner.Start();
        await Task.Delay(50);
        Assert.Empty(service.Calls);

        now = now.AddMinutes(20);
        tick();
        await Task.Delay(50);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task Timer_tick_honors_a_runtime_interval_change_live()
    {
        // The interval is read live on each tick. Set a 10-minute interval,
        // advance 5 minutes (no fire), change the config to 4 minutes (which 5
        // now exceeds), and tick -> fires. This is what makes a dialog change
        // take effect without a restart.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var configLoader = new FakeConfigLoader(); // default 10-minute interval
        var service = new FakeUpdateCheckService();
        var now = DateTimeOffset.UtcNow;
        var (runner, tick) = BuildWithCapturedTick(session, service, configLoader, () => now);

        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        now = now.AddMinutes(5);
        tick();
        await Task.Delay(50);
        Assert.Single(service.Calls); // 5 < 10, no fire

        // Lower the interval to 4 minutes; the 5 elapsed minutes now exceed it.
        configLoader.Config.Integrations.Nexus.AutoUpdateCheckIntervalMinutes = 4;
        tick();
        await WaitAsync(() => service.CallCount == 2);
    }

    // ---- manual check now ------------------------------------------------

    [Fact]
    public async Task CheckNow_triggers_a_thorough_check_for_active_profile()
    {
        // The manual trigger (header refresh button) fires a THOROUGH check
        // right away (the per-mod pass), regardless of the toggle. Construct
        // with the profile already active so Start fires the opening Month-only
        // check; then CheckNow fires a thorough one for the SAME profile.
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);
        runner.Start();
        await WaitAsync(() => service.CallCount == 1); // the opening startup fire (Month-only)

        await runner.CheckNowAsync(); // the manual trigger (thorough)

        await WaitAsync(() => service.ThoroughCallCount == 1);
        // The Month-only path got the startup id; the thorough path got the
        // manual id (same profile).
        Assert.Equal(new[] { id }, service.Calls);
        Assert.Equal(new[] { id }, service.ThoroughCalls);
    }

    [Fact]
    public async Task CheckNow_is_a_noop_when_no_profile_is_active()
    {
        var session = new FakeProfileSession(); // null
        var service = new FakeUpdateCheckService();
        var runner = Build(session, service);

        await runner.CheckNowAsync();

        await Task.Delay(50);
        Assert.Empty(service.Calls);
        Assert.Empty(service.ThoroughCalls);
    }

    [Fact]
    public async Task CheckNow_swallowing_a_throwing_thorough_check_does_not_fault_the_awaited_task()
    {
        // The manual trigger awaits the runner's Task; a throwing
        // CheckThoroughAsync must be swallowed by the runner's belt-and-suspenders
        // catch so the awaited Task does not fault (which would surface as an
        // unobserved exception on the list VM's command).
        var id = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = id };
        var service = new FakeUpdateCheckService
        {
            ThrowOnCheck = new InvalidOperationException("boom"),
        };
        var runner = Build(session, service);

        await runner.CheckNowAsync(); // must not throw

        Assert.Equal(new[] { id }, service.ThoroughCalls);
    }

    // ---- exception safety --------------------------------------------------

    [Fact]
    public async Task CheckAsync_throwing_is_swallowed_and_runner_survives()
    {
        // The fire-and-forget Task.Run must catch a throwing CheckAsync (the
        // service is documented to self-catch, but the runner wraps it as
        // belt-and-suspenders). The throw must not escape the test thread,
        // and the runner must keep firing on the next switch (handler not dead).
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = firstId };
        var service = new FakeUpdateCheckService
        {
            ThrowOnCheck = new InvalidOperationException("boom"),
        };
        var runner = Build(session, service);

        // Start() and the switch both run on the test thread and must return
        // normally (the throw happens inside the thread-pool task, swallowed).
        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        session.ActiveProfileId = secondId; // would throw again; must not escape

        await WaitAsync(() => service.CallCount == 2);

        // Both calls were recorded (recording happens before the throw, and the
        // handler survived the first failure to fire the second).
        Assert.Equal(new[] { firstId, secondId }, service.Calls);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Builds a runner with the shared fakes + a real <see cref="FakeConfigLoader"/>
    /// + no periodic timer (the default). For startup / switch / manual tests
    /// that do not drive the periodic tick.
    /// </summary>
    private static UpdateCheckRunner Build(
        FakeProfileSession session,
        FakeUpdateCheckService service,
        FakeConfigLoader? configLoader = null,
        Func<DateTimeOffset>? getNow = null)
    {
        configLoader ??= new FakeConfigLoader();
        return new UpdateCheckRunner(
            session,
            service,
            configLoader,
            NullLogger<UpdateCheckRunner>.Instance,
            startTimer: null,
            getNow: getNow);
    }

    /// <summary>
    /// Builds a runner whose periodic-timer start delegate captures the tick
    /// callback, so the test invokes it directly. Returns the runner + the
    /// captured tick action.
    /// </summary>
    private static (UpdateCheckRunner runner, Action tick) BuildWithCapturedTick(
        FakeProfileSession session,
        FakeUpdateCheckService service,
        FakeConfigLoader? configLoader = null,
        Func<DateTimeOffset>? getNow = null)
    {
        configLoader ??= new FakeConfigLoader();
        Action? tick = null;
        var runner = new UpdateCheckRunner(
            session,
            service,
            configLoader,
            NullLogger<UpdateCheckRunner>.Instance,
            startTimer: t => tick = t,
            getNow: getNow);
        return (runner, () => tick!.Invoke());
    }

    /// <summary>
    /// Polls <paramref name="condition"/> on a short delay until it returns
    /// <c>true</c> or a 2s timeout elapses. The runner fires its check on a
    /// thread-pool task, so tests must wait for that task to actually invoke
    /// the fake service before asserting. Asserts the condition held at the
    /// end so a timeout surfaces as a deterministic failure rather than a
    /// stale-pass.
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

        Assert.True(condition(), "Timed out waiting for the runner to fire the update check.");
    }
}
