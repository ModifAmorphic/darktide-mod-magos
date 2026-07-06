using System.Collections.Concurrent;
using System.ComponentModel;
using Magos.Modificus.Integrations;
using Magos.Modificus.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.UI.Tests;

/// <summary>
/// <see cref="UpdateCheckRunner"/> unit tests: the two trigger points (startup
/// with a restored active id, and an active-profile switch), the non-triggers
/// (null id, non-<see cref="IProfileSession.ActiveProfileId"/> property
/// changes), and the fire-and-forget exception safety (a throwing
/// <see cref="IUpdateCheckService.CheckAsync"/> is swallowed; the runner
/// survives and keeps firing on the next switch).
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
        var runner = new UpdateCheckRunner(session, service, NullLogger<UpdateCheckRunner>.Instance);

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
        var runner = new UpdateCheckRunner(session, service, NullLogger<UpdateCheckRunner>.Instance);

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
        var runner = new UpdateCheckRunner(session, service, NullLogger<UpdateCheckRunner>.Instance);
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
        var runner = new UpdateCheckRunner(session, service, NullLogger<UpdateCheckRunner>.Instance);
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
        var runner = new UpdateCheckRunner(session, service, NullLogger<UpdateCheckRunner>.Instance);
        runner.Start();
        await WaitAsync(() => service.CallCount == 1);

        session.IsRunning = true; // polling-timer driven, not a profile load

        await Task.Delay(50);
        Assert.Single(service.Calls); // still just the startup call
    }

    // ---- exception safety --------------------------------------------------

    [Fact]
    public async Task CheckAsync_throwing_is_swallowed_and_runner_survives()
    {
        // The fire-and-forget Task.Run must catch a throwing CheckAsync (the
        // service is documented to self-catch, but the runner wraps it as
        // belt-and-suspenders). The throw must not escape to the test thread,
        // and the runner must keep firing on the next switch (handler not dead).
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var session = new FakeProfileSession { ActiveProfileId = firstId };
        var service = new FakeUpdateCheckService
        {
            ThrowOnCheck = new InvalidOperationException("boom"),
        };
        var runner = new UpdateCheckRunner(session, service, NullLogger<UpdateCheckRunner>.Instance);

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

/// <summary>
/// Recording <see cref="IUpdateCheckService"/> for the runner tests. Captures
/// the profile id of every <see cref="IUpdateCheckService.CheckAsync"/> call
/// (thread-safe; the runner dispatches each call on a thread-pool task).
/// <see cref="ThrowOnCheck"/>, when set, is thrown synchronously from
/// <see cref="CheckAsync"/> after the call is recorded, so the exception-safety
/// test can assert the call landed AND that the runner swallowed the throw.
/// </summary>
/// <remarks>
/// The runner never reads <see cref="IUpdateCheckService.LastResult"/> and never
/// subscribes to <see cref="IUpdateCheckService.CheckCompleted"/> (Stage 5 does
/// both), so this fake keeps those surfaces as no-ops and focuses on the call
/// recording the runner actually drives.</remarks>
internal sealed class FakeUpdateCheckService : IUpdateCheckService
{
    private readonly ConcurrentQueue<Guid> _calls = new();

    /// <summary>The number of <see cref="CheckAsync"/> calls recorded so far.
    /// Thread-safe; safe to poll from the test thread while the runner fires on
    /// a thread-pool task.</summary>
    public int CallCount => _calls.Count;

    /// <summary>The profile ids passed to <see cref="CheckAsync"/>, in call
    /// order. A snapshot (<see cref="ConcurrentQueue{T}.ToArray"/>); safe to
    /// read after <see cref="WaitAsync"/> confirms the expected count.</summary>
    public IReadOnlyList<Guid> Calls => _calls.ToArray();

    /// <summary>
    /// When set, thrown synchronously from every <see cref="CheckAsync"/> call,
    /// after the call is recorded. Lets the exception-safety test assert the
    /// call was made AND that the runner swallowed the throw.
    /// </summary>
    public Exception? ThrowOnCheck { get; set; }

    /// <inheritdoc />
    /// <remarks>Unused by the runner; kept for interface completeness.</remarks>
    public UpdateCheckResult? LastResult { get; private set; }

    /// <inheritdoc />
    /// <remarks>Unused by the runner (Stage 5 subscribes). Raised by
    /// <see cref="CheckAsync"/> to mirror the real service's contract + keep the
    /// field used.</remarks>
    public event EventHandler<UpdateCheckResult?>? CheckCompleted;

    /// <inheritdoc />
    public Task<UpdateCheckResult> CheckAsync(Guid profileId, CancellationToken ct = default)
    {
        _calls.Enqueue(profileId);

        if (ThrowOnCheck is not null)
        {
            throw ThrowOnCheck;
        }

        LastResult = new UpdateCheckResult(
            Array.Empty<ModUpdateInfo>(), DateTimeOffset.UtcNow, RateLimited: false);

        // Mirror the real service's contract: CheckCompleted is raised exactly
        // once per call (Stage 5 subscribes; the runner does not). Also keeps
        // the event field used so the compiler does not warn (CS0067).
        CheckCompleted?.Invoke(this, LastResult);

        return Task.FromResult(LastResult);
    }
}
