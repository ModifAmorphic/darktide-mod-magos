using System.ComponentModel;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The UI-layer glue between <see cref="IProfileSession"/> (the active-profile
/// authority) and <see cref="IUpdateCheckService"/> (the Integrations update
/// check). Fires a background update check on three triggers: (1) startup, when
/// the session restores the persisted active id; (2) an active-profile switch;
/// and (3) a periodic timer (every <c>AutoUpdateCheckIntervalMinutes</c> when
/// <c>AutoUpdateCheckEnabled</c> is on). All three fire the Month-only
/// <see cref="IUpdateCheckService.CheckAsync"/> (cheap, one API call) +
/// discard the task. A fourth trigger, the manual "check now" affordance on the
/// mod list, routes through <see cref="CheckNowAsync"/> + fires the thorough
/// <see cref="IUpdateCheckService.CheckThoroughAsync"/> (the per-mod pass that
/// also catches mods outside the Month window); its <see cref="Task"/> is
/// awaitable so the list VM can drive an <c>IsCheckingNow</c> affordance while
/// it runs. This class never blocks on a check beyond the await the manual
/// trigger opts into, never surfaces its result, and never lets an unobserved
/// exception escape the thread-pool task.
/// </summary>
/// <remarks>
/// <para>
/// <b>The periodic timer is the only gated trigger.</b> Profile-load (startup +
/// switch) and <see cref="CheckNowAsync"/> always fire regardless of the
/// <c>AutoUpdateCheckEnabled</c> toggle; only the periodic timer respects it.
/// The toggle is read live on each tick so a runtime change in the Integrations
/// dialog takes effect without a restart.</para>
/// <para>
/// <b>Interval honored to the tick granularity.</b> The timer ticks at a fixed
/// fine interval (<see cref="TickInterval"/>, 1 minute) and fires a check when
/// <c>AutoUpdateCheckIntervalMinutes</c> has elapsed since the last check. This
/// decouples the timer period (fixed) from the user-configured interval (live),
/// so changing the interval in the dialog takes effect on the next tick rather
/// than requiring a restart. The last-check timestamp is shared across every
/// trigger, so a manual or profile-load check resets the periodic clock too
/// (no double-fire right after a switch).</para>
/// <para>
/// <b>Fire-and-forget by design, except the manual trigger's await.</b> The
/// periodic + profile-load checks run on a thread-pool task; this class does
/// not await them. The manual trigger's <see cref="Task"/> IS awaited by the
/// list VM's <c>CheckForUpdatesNow</c> command, but only so the command can
/// toggle <c>IsCheckingNow</c> off in its finally block; the check itself still
/// runs on a thread-pool task + never blocks the UI thread. Either way, Stage
/// 5 reads <see cref="IUpdateCheckService.LastResult"/> + subscribes to
/// <see cref="IUpdateCheckService.CheckCompleted"/> to render badges without
/// awaiting.</para>
/// <para>
/// <b>Belt-and-suspenders exception handling.</b>
/// <see cref="IUpdateCheckService.CheckAsync"/> /
/// <see cref="IUpdateCheckService.CheckThoroughAsync"/> are documented to
/// swallow their own non-cancellation failures and surface them as an empty /
/// partial <see cref="UpdateCheckResult"/>. But a fire-and-forget
/// <see cref="Task"/> must never leak an unobserved exception, so the run
/// wrapper catches regardless. <see cref="OperationCanceledException"/> is
/// expected on shutdown (not an error); anything else is logged + swallowed.</para>
/// <para>
/// <b>Lives in the UI assembly.</b> Mirrors <c>NxmModDownloadHandler</c>: the
/// glue observes a UI-layer singleton (<see cref="IProfileSession"/> lives in
/// UI) and drives an Integrations service, so it belongs on the consumer side
/// of that boundary. The composition root registers this as a singleton +
/// calls <see cref="Start"/> once after the provider is built (best-effort: a
/// wiring failure is logged + swallowed, never blocks startup).</para>
/// <para>
/// <b>Testability:</b> the periodic timer is injected as a
/// <paramref name="startTimer"/> delegate (mirrors <see cref="ProfileSession"/>),
/// and the clock is injected as <paramref name="getNow"/> so tests drive time
/// deterministically. Production wires both (a <c>DispatcherTimer</c> +
/// <see cref="DateTimeOffset.UtcNow"/>); tests pass null + a controllable clock
/// and invoke the captured tick directly.</para>
/// </remarks>
public sealed class UpdateCheckRunner
{
    /// <summary>
    /// The periodic timer's fixed tick granularity. The user-configured interval
    /// (<c>AutoUpdateCheckIntervalMinutes</c>) is honored to this granularity:
    /// the runner fires when that much time has elapsed since the last check,
    /// checked on each tick. One minute is the finest interval the user can
    /// configure, so this matches the smallest meaningful setting without
    /// burning cycles on sub-minute polling.
    /// </summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly IProfileSession _session;
    private readonly IUpdateCheckService _updateCheck;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<UpdateCheckRunner> _logger;
    private readonly Action<Action>? _startTimer;
    private readonly Func<DateTimeOffset> _getNow;

    // The last time any check fired (startup, switch, periodic, or manual).
    // Written + read on the UI thread only (the timer tick, session property
    // changes, and CheckNow all run on the UI thread; FireAndForget assigns
    // this before dispatching the thread-pool task), so no synchronization.
    private DateTimeOffset _lastCheckAt = DateTimeOffset.MinValue;

    /// <param name="session">The active-profile authority (fires on load + switch).</param>
    /// <param name="updateCheck">The Integrations update check entry point.</param>
    /// <param name="configLoader">Read live for the periodic toggle + interval.</param>
    /// <param name="logger">Structured logger for swallowed exceptions.</param>
    /// <param name="startTimer">Starts the periodic tick. Production wires this
    /// to a <c>DispatcherTimer</c> (UI thread); tests pass null and invoke the
    /// captured tick directly.</param>
    /// <param name="getNow">The clock, for testable interval math. Defaults to
    /// <see cref="DateTimeOffset.UtcNow"/>; tests inject a controllable clock.</param>
    public UpdateCheckRunner(
        IProfileSession session,
        IUpdateCheckService updateCheck,
        IConfigLoader configLoader,
        ILogger<UpdateCheckRunner> logger,
        Action<Action>? startTimer = null,
        Func<DateTimeOffset>? getNow = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _updateCheck = updateCheck ?? throw new ArgumentNullException(nameof(updateCheck));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startTimer = startTimer;
        _getNow = getNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Subscribes to the session's active-profile changes, starts the periodic
    /// tick, and fires an opening check if a profile was already restored at
    /// startup. Called once from the composition root after the provider is
    /// built (best-effort: failures are logged + swallowed by the caller, never
    /// blocking app startup).
    /// </summary>
    /// <remarks>
    /// <see cref="IProfileSession.ActiveProfileId"/> is restored in the
    /// session's constructor (before this runner starts), so there is no
    /// <see cref="INotifyPropertyChanged.PropertyChanged"/> event for the
    /// restore itself. The opening check is fired explicitly here when an id is
    /// already present; subsequent switches flow through
    /// <see cref="OnActiveProfileChanged"/>.</remarks>
    public void Start()
    {
        _session.PropertyChanged += OnActiveProfileChanged;
        _startTimer?.Invoke(OnTimerTick);

        // Startup-with-last-profile: the session restores the persisted active
        // id in its constructor, before this runner starts. Fire the opening
        // check explicitly so the restored profile gets checked too.
        if (_session.ActiveProfileId is Guid id)
        {
            FireAndForget(id);
        }
    }

    /// <summary>
    /// The manual "check now" trigger (the mod-list header refresh button). Fires
    /// an immediate <em>thorough</em> check for the active profile (the per-mod
    /// pass that also catches mods outside the Month window), awaitable so the
    /// caller (the list VM's <c>CheckForUpdatesNow</c> command) can drive an
    /// <c>IsCheckingNow</c> affordance while it runs. No-op (returns
    /// <see cref="Task.CompletedTask"/>) when no profile is active. Resets the
    /// periodic clock (the shared <c>_lastCheckAt</c>) so the next periodic tick
    /// respects the configured interval from this check.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when the thorough check
    /// finishes (success or failure). Never faults: the inner try/catch swallows
    /// the check's exceptions (mirrors the fire-and-forget path's exception
    /// safety). The caller awaits it to drive an <c>IsCheckingNow</c> spinner;
    /// it carries no result (the result lands via
    /// <see cref="IUpdateCheckService.CheckCompleted"/>).</returns>
    public Task CheckNowAsync()
    {
        if (_session.ActiveProfileId is Guid id)
        {
            return RunAsync(id, thorough: true);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The periodic tick. Reads the config live (toggle + interval), gates on an
    /// active profile, and fires a check only when the configured interval has
    /// elapsed since the last check. Skipped entirely (no check, no clock reset)
    /// when the toggle is off or no profile is active.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread (production wires a <c>DispatcherTimer</c>); tests
    /// invoke the captured tick directly. The config read is cheap (a tiny JSON
    /// file re-read each tick, the established live-read pattern).</remarks>
    private void OnTimerTick()
    {
        var nexus = _configLoader.Load().Integrations.Nexus;
        if (!nexus.AutoUpdateCheckEnabled)
        {
            return;
        }

        if (_session.ActiveProfileId is not Guid id)
        {
            return;
        }

        var intervalMinutes = nexus.AutoUpdateCheckIntervalMinutes < 1
            ? 1
            : nexus.AutoUpdateCheckIntervalMinutes;
        if (_getNow() - _lastCheckAt < TimeSpan.FromMinutes(intervalMinutes))
        {
            return;
        }

        FireAndForget(id);
    }

    /// <summary>
    /// The <see cref="IProfileSession.PropertyChanged"/> handler. Fires a check
    /// only for an <see cref="IProfileSession.ActiveProfileId"/> change to a
    /// non-null id; ignores every other property (notably
    /// <see cref="IProfileSession.IsRunning"/>, which the polling timer drives
    /// every few seconds).
    /// </summary>
    private void OnActiveProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The polling timer raises IsRunning changes every few seconds; those
        // carry no profile-load signal. Only an ActiveProfileId change is a
        // load, and only when the new id is non-null (a null id means the
        // active profile was cleared by delete-of-active).
        if (e.PropertyName != nameof(IProfileSession.ActiveProfileId))
        {
            return;
        }

        if (_session.ActiveProfileId is Guid newId)
        {
            FireAndForget(newId);
        }
    }

    /// <summary>
    /// Fires <see cref="IUpdateCheckService.CheckAsync"/> (Month-only) on a
    /// thread-pool task and discards the returned <see cref="Task"/>. Used by
    /// the periodic + profile-load triggers (the cheap path). Never blocks the
    /// caller; never leaks an unobserved exception. Also stamps the shared
    /// <c>_lastCheckAt</c> so the periodic timer respects the configured
    /// interval from this check.
    /// </summary>
    private void FireAndForget(Guid profileId) => _ = RunAsync(profileId, thorough: false);

    /// <summary>
    /// Runs a check on a thread-pool task. <paramref name="thorough"/> selects
    /// between the Month-only <see cref="IUpdateCheckService.CheckAsync"/> (the
    /// cheap path, used by the periodic + profile-load triggers) and the
    /// per-mod <see cref="IUpdateCheckService.CheckThoroughAsync"/> (the
    /// thorough path, used by the manual "check now" affordance). The returned
    /// <see cref="Task"/> completes when the check finishes; the fire-and-forget
    /// caller discards it, the manual trigger awaits it to drive its
    /// <c>IsCheckingNow</c> affordance. Never faults: the inner try/catch
    /// swallows the check's exceptions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IUpdateCheckService.CheckAsync"/> /
    /// <see cref="IUpdateCheckService.CheckThoroughAsync"/> are documented to
    /// catch their own non-cancellation failures and return an empty / partial
    /// <see cref="UpdateCheckResult"/>. The outer try/catch here is
    /// belt-and-suspenders: a fire-and-forget <see cref="Task"/> whose only
    /// awaited operation throws must not surface that as an unobserved
    /// exception. <see cref="OperationCanceledException"/> is swallowed silently
    /// (it would fire on shutdown if a cancellation token were wired through).
    /// Any other exception is logged. The check is never retried here; the next
    /// trigger fires the next check.</para>
    /// </remarks>
    private async Task RunAsync(Guid profileId, bool thorough)
    {
        // Shared across every trigger: the periodic timer computes the
        // elapsed-since-last-check from this stamp. Set BEFORE dispatching so
        // the periodic tick cannot double-fire between this assignment and the
        // thread-pool task's actual start.
        _lastCheckAt = _getNow();

        try
        {
            await Task.Run(async () =>
            {
                if (thorough)
                {
                    await _updateCheck.CheckThoroughAsync(profileId).ConfigureAwait(false);
                }
                else
                {
                    await _updateCheck.CheckAsync(profileId).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Defensive only: no cancellation token is wired today (the
            // runner uses the default token), so this does not fire in
            // production. Swallowed silently regardless.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Update check for profile {Profile} threw unexpectedly.", profileId);
        }
    }
}
