using System.ComponentModel;
using Magos.Modificus.Integrations;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI.Session;

/// <summary>
/// The UI-layer glue between <see cref="IProfileSession"/> (the active-profile
/// authority) and <see cref="IUpdateCheckService"/> (the Integrations update
/// check). Fires a background update check on the two moments a profile becomes
/// active: startup (when the session restores the persisted active id) and an
/// active-profile switch (when the user picks a different profile). The check
/// itself is fire-and-forget; this class never blocks on it, never surfaces its
/// result, and never lets an unobserved exception escape the thread-pool task.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two trigger points, no more.</b> Both the startup-with-last-profile path
/// and the profile-switch path route through the same private
/// <see cref="FireAndForget"/>. Other <see cref="IProfileSession"/> property
/// changes (<see cref="IProfileSession.IsRunning"/>, driven by the session's
/// polling timer every few seconds) are explicitly ignored: only an
/// <see cref="IProfileSession.ActiveProfileId"/> change fires a check, and only
/// when the new id is non-null. A null id means the active profile was cleared
/// by delete-of-active; there is nothing to check.</para>
/// <para>
/// <b>Fire-and-forget by design.</b> The check runs on a thread-pool task; this
/// class does not await it. Stage 5 reads
/// <see cref="IUpdateCheckService.LastResult"/> and subscribes to
/// <see cref="IUpdateCheckService.CheckCompleted"/> to render badges without
/// awaiting. A slow or stalled check must never block the UI thread or a
/// profile switch, so the call is dispatched and forgotten.</para>
/// <para>
/// <b>Belt-and-suspenders exception handling.</b>
/// <see cref="IUpdateCheckService.CheckAsync"/> is documented to swallow its own
/// non-cancellation failures and surface them as an empty
/// <see cref="UpdateCheckResult"/>. But a fire-and-forget <see cref="Task"/>
/// must never leak an unobserved exception, so <see cref="FireAndForget"/> wraps
/// the call in its own try/catch regardless.
/// <see cref="OperationCanceledException"/> is expected on shutdown (not an
/// error); anything else is logged + swallowed.</para>
/// <para>
/// <b>Lives in the UI assembly.</b> Mirrors <c>NxmModDownloadHandler</c>: the
/// glue observes a UI-layer singleton (<see cref="IProfileSession"/> lives in
/// UI) and drives an Integrations service, so it belongs on the consumer side
/// of that boundary. The composition root registers this as a singleton +
/// calls <see cref="Start"/> once after the provider is built (best-effort: a
/// wiring failure is logged + swallowed, never blocks startup).</para>
/// </remarks>
internal sealed class UpdateCheckRunner
{
    private readonly IProfileSession _session;
    private readonly IUpdateCheckService _updateCheck;
    private readonly ILogger<UpdateCheckRunner> _logger;

    public UpdateCheckRunner(
        IProfileSession session,
        IUpdateCheckService updateCheck,
        ILogger<UpdateCheckRunner> logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _updateCheck = updateCheck ?? throw new ArgumentNullException(nameof(updateCheck));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Subscribes to the session's active-profile changes and fires an opening
    /// check if a profile was already restored at startup. Called once from the
    /// composition root after the provider is built (best-effort: failures are
    /// logged + swallowed by the caller, never blocking app startup).
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

        // Startup-with-last-profile: the session restores the persisted active
        // id in its constructor, before this runner starts. Fire the opening
        // check explicitly so the restored profile gets checked too.
        if (_session.ActiveProfileId is Guid id)
        {
            FireAndForget(id);
        }
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
    /// Fires <see cref="IUpdateCheckService.CheckAsync"/> on a thread-pool task
    /// and discards the returned <see cref="Task"/>. Never blocks the caller;
    /// never leaks an unobserved exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IUpdateCheckService.CheckAsync"/> is documented to catch its
    /// own non-cancellation failures and return an empty
    /// <see cref="UpdateCheckResult"/>. The outer try/catch here is
    /// belt-and-suspenders: a fire-and-forget <see cref="Task"/> whose only
    /// awaited operation throws must not surface that as an unobserved
    /// exception (which can crash the runtime or surface noisily in the
    /// unobserved-exception handler). <see cref="OperationCanceledException"/>
    /// is swallowed silently: it would fire on shutdown if a cancellation token
    /// were wired through (none is today; the runner calls
    /// <see cref="IUpdateCheckService.CheckAsync"/> with the default token), so
    /// the catch is defensive only. Any other exception is logged. The check is
    /// never retried here; the next profile load fires the next check.</para>
    /// </remarks>
    private void FireAndForget(Guid profileId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _updateCheck.CheckAsync(profileId).ConfigureAwait(false);
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
        });
    }
}
