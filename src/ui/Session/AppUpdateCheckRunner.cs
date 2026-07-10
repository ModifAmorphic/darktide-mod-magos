using Microsoft.Extensions.Logging;
using Modificus.Curator.General;
using Modificus.Curator.UI.AppUpdate;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The UI-layer glue that fires one Curator self-update availability check on
/// startup, fire-and-forget, against <see cref="IAppUpdateService"/>. The result
/// lands through the service's
/// <see cref="IAppUpdateService.UpdateStateChanged"/> event (UI subscribers read
/// <see cref="IAppUpdateService.LastCheckResult"/>); this runner itself never
/// surfaces a result. App updates are profile-independent, so unlike
/// <see cref="UpdateCheckRunner"/> this class has no profile dependency and no
/// periodic timer: a single check per startup. The manual "check now"
/// affordance (a UI concern) calls
/// <see cref="IAppUpdateService.CheckForUpdatesAsync"/> directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gated on <c>CuratorConfig.AppUpdates.CheckOnStartup</c>.</b> The startup
/// check fires only when that toggle is <c>true</c> (the default). When it is
/// <c>false</c>, <see cref="Start"/> logs an informational line and returns
/// without firing the check, and the status-strip update notice does not appear
/// (the notice shows whenever <see cref="IAppUpdateService.LastCheckResult"/> is
/// non-null, so the manual "Check for Updates" button in Settings can still
/// populate it). The toggle is read live on startup; the manual check path is
/// unaffected (it calls <see cref="IAppUpdateService.CheckForUpdatesAsync"/>
/// directly, bypassing this runner).</para>
/// <para>
/// <b>Fire-and-forget by design.</b> <see cref="Start"/> dispatches the check on
/// a thread-pool task and discards the returned <see cref="Task"/>. This class
/// never blocks on the check, never surfaces its result, and never lets an
/// unobserved exception escape the thread-pool task.</para>
/// <para>
/// <b>Belt-and-suspenders exception handling.</b>
/// <see cref="IAppUpdateService.CheckForUpdatesAsync"/> is documented to swallow
/// its own non-cancellation failures and return null (the prior
/// <see cref="IAppUpdateService.LastCheckResult"/> state is preserved for
/// subscribers). But a
/// fire-and-forget <see cref="Task"/> must never leak an unobserved exception,
/// so the run wrapper catches regardless. <see cref="OperationCanceledException"/>
/// is expected on shutdown (not an error); anything else is logged and
/// swallowed.</para>
/// <para>
/// <b>Lives in the UI assembly.</b> Mirrors <see cref="UpdateCheckRunner"/> and
/// <c>NxmModDownloadHandler</c>: the glue drives a UI-layer singleton (the
/// <see cref="IAppUpdateService"/> is registered in the UI composition root) and
/// its result is consumed by the UI, so it belongs on the consumer side of that
/// boundary. The composition root registers this as a singleton and calls
/// <see cref="Start"/> once after the provider is built (best-effort: a wiring
/// failure is logged and swallowed, never blocks startup; the user sees nothing,
/// the update notice simply never appears).</para>
/// </remarks>
public sealed class AppUpdateCheckRunner
{
    private readonly IAppUpdateService _appUpdate;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<AppUpdateCheckRunner> _logger;

    public AppUpdateCheckRunner(
        IAppUpdateService appUpdate,
        IConfigLoader configLoader,
        ILogger<AppUpdateCheckRunner> logger)
    {
        _appUpdate = appUpdate ?? throw new ArgumentNullException(nameof(appUpdate));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Fires one self-update availability check on a thread-pool task and
    /// discards the returned <see cref="Task"/>. Called once from the
    /// composition root after the provider is built (best-effort: failures are
    /// logged and swallowed by the caller, never blocking app startup). The
    /// check result lands through the service's
    /// <see cref="IAppUpdateService.UpdateStateChanged"/> event.
    /// </summary>
    /// <remarks>
    /// Reads <c>CuratorConfig.AppUpdates.CheckOnStartup</c> live. When that is
    /// <c>false</c>, logs an informational line and returns without firing the
    /// check (the manual check path is unaffected; it calls
    /// <see cref="IAppUpdateService.CheckForUpdatesAsync"/> directly).
    /// </remarks>
    public void Start()
    {
        if (!_configLoader.Load().AppUpdates.CheckOnStartup)
        {
            _logger.LogInformation("App update startup check is disabled by config; skipping.");
            return;
        }

        _ = RunAsync();
    }

    /// <summary>
    /// Runs <see cref="IAppUpdateService.CheckForUpdatesAsync"/> on a thread-pool
    /// task. The returned <see cref="Task"/> completes when the check finishes;
    /// <see cref="Start"/> discards it. Never faults: the inner try/catch
    /// swallows the check's exceptions so a fire-and-forget task never leaks an
    /// unobserved exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IAppUpdateService.CheckForUpdatesAsync"/> is documented to
    /// catch its own non-cancellation failures and return null (the prior
    /// <see cref="IAppUpdateService.LastCheckResult"/> state is preserved for
    /// subscribers). The outer try/catch here is belt-and-suspenders: a
    /// fire-and-forget <see cref="Task"/> whose only awaited operation throws
    /// must not surface that as an unobserved exception.
    /// <see cref="OperationCanceledException"/> is swallowed silently (it would
    /// fire on shutdown if a cancellation token were wired through). Any other
    /// exception is logged.</para>
    /// <para>
    /// <see cref="ConfigureAwait"/>(false) is used ONLY inside this
    /// <see cref="Task.Run"/> block. This is the one place the project's
    /// "no ConfigureAwait(false) in UI-layer code" rule permits it: the work is
    /// explicitly a thread-pool background task with no UI-thread affinity, and
    /// the rule's narrow exception is for exactly this shape (mirrors
    /// <see cref="UpdateCheckRunner.RunAsync"/>).</para>
    /// </remarks>
    private async Task RunAsync()
    {
        try
        {
            await Task.Run(async () =>
            {
                await _appUpdate.CheckForUpdatesAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Defensive only: no cancellation token is wired today (the runner
            // uses the default token), so this does not fire in production.
            // Swallowed silently regardless.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "App self-update check threw unexpectedly.");
        }
    }
}
