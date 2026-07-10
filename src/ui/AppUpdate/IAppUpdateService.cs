namespace Modificus.Curator.UI.AppUpdate;

/// <summary>
/// The result of a Curator self-update availability check. A plain data record
/// that exposes no Velopack types, so the UI layer can consume it without a
/// hard dependency on the update engine.
/// </summary>
/// <param name="TargetVersion">The available update's version, as a string
/// (the Velopack-resolved <c>TargetFullRelease.Version</c> rendered with
/// <c>ToString()</c>). The UI shows this next to the current version.</param>
/// <param name="Notes">The release notes for the target version (Velopack's
/// <c>NotesMarkdown</c>), or <c>null</c> when the release published none. The
/// UI may render this in the update notice.</param>
public sealed record AppUpdateInfo(string TargetVersion, string? Notes);

/// <summary>
/// Checks for, downloads, and applies Curator's own application updates (the
/// Velopack-managed installer on Windows). The shape mirrors
/// <see cref="Modificus.Curator.Integrations.IUpdateCheckService"/>: a
/// best-effort availability check that never throws to the caller for
/// non-cancellation failures, plus a state-holding
/// <see cref="LastCheckResult"/> / <see cref="UpdatePendingRestart"/> surface
/// published under a lock together with the <see cref="UpdateStateChanged"/>
/// event. The download and apply steps are user-initiated and DO surface their
/// failures (a checksum mismatch or a locked-file error is something the user
/// needs to see), so they propagate from those two methods.
/// </summary>
/// <remarks>
/// <para>
/// <b>Conditional availability.</b> Self-update is meaningful only when the
/// running app is itself a Velopack install (a packaged Windows build). When it
/// is not, <see cref="IsUpdateSupported"/> is <c>false</c>: the check returns
/// <c>null</c>, <see cref="CurrentVersion"/> is <c>null</c>, and the download /
/// apply steps are not driven by the UI. A no-op implementation is registered
/// in that case, so every consumer can talk to this interface unconditionally
/// and gate its affordances on <see cref="IsUpdateSupported"/>.</para>
/// <para>
/// <b>State holding, lock-protected write, lock-free read.</b>
/// <see cref="LastCheckResult"/> and <see cref="UpdatePendingRestart"/> are
/// written from a background task (the check runs inside a thread-pool task) and
/// read by the UI thread. The write is taken under an internal lock together
/// with the <see cref="UpdateStateChanged"/> invocation, so a subscriber
/// observes the values that were just published. Reads are lock-free:
/// reference assignment is atomic on every target runtime, so a lock-free read
/// can at worst observe a one-check-stale value, corrected on the next
/// <see cref="UpdateStateChanged"/>.</para>
/// <para>
/// <b>Best-effort check, never throws (except cancellation).</b> A transient
/// network failure or a GitHub rate limit during
/// <see cref="CheckForUpdatesAsync"/> is swallowed and logged; the prior
/// <see cref="LastCheckResult"/> is left unchanged and no event is raised (the
/// caller has nothing to gain from catching). Cancellation
/// (<see cref="OperationCanceledException"/>) propagates, so a cancelled check
/// is not misreported as "no updates".</para>
/// <para>
/// <b>User-initiated download / apply DO surface failures.</b>
/// <see cref="DownloadUpdatesAsync"/> and <see cref="ApplyUpdatesAndRestart"/>
/// are triggered by an explicit user action, so the errors that can occur there
/// (a checksum mismatch, an update-lock contention, an IO failure) propagate to
/// the caller rather than being swallowed. This is the deliberate contrast with
/// the silent startup check.</para>
/// </remarks>
public interface IAppUpdateService
{
    /// <summary>
    /// <c>true</c> when self-update is meaningful for this build (the running
    /// app is a Velopack install and the update manager initialized). The UI
    /// gates the entire update surface (notice, download button, apply) on this
    /// so a non-Velopack build (Linux, a dev run) simply shows nothing.
    /// </summary>
    bool IsUpdateSupported { get; }

    /// <summary>
    /// The currently installed app version, as a string, or <c>null</c> when
    /// self-update is unsupported or the installed version cannot be resolved.
    /// The UI shows this alongside <see cref="AppUpdateInfo.TargetVersion"/> so
    /// the user can compare.
    /// </summary>
    string? CurrentVersion { get; }

    /// <summary>
    /// The most recent check result, or <c>null</c> before the first check
    /// completes, when the last check found no update, when self-update is
    /// unsupported, or when a check failed (a failure leaves the prior value
    /// untouched). The UI reads this to render the update notice without
    /// awaiting a check.
    /// </summary>
    /// <remarks>
    /// Written under the internal state lock together with the
    /// <see cref="UpdateStateChanged"/> invocation; read lock-free (see the
    /// interface remarks on the threading model).
    /// </remarks>
    AppUpdateInfo? LastCheckResult { get; }

    /// <summary>
    /// The update that has been downloaded and is waiting to be applied on the
    /// next restart, or <c>null</c> until a download succeeds. Set by
    /// <see cref="DownloadUpdatesAsync"/> on success (to the same value as
    /// <see cref="LastCheckResult"/> at that point); cleared by nothing in this
    /// interface (the pending update is consumed by
    /// <see cref="ApplyUpdatesAndRestart"/>). The UI shows an "update ready,
    /// restart to apply" affordance when this is non-null.
    /// </summary>
    AppUpdateInfo? UpdatePendingRestart { get; }

    /// <summary>
    /// Raised (on the completing thread) when <see cref="LastCheckResult"/> or
    /// <see cref="UpdatePendingRestart"/> changes. Always raised exactly once
    /// per successful check (including the "no update available" result that
    /// clears a prior pending result) and once on a successful download, with
    /// the new values already published. Never raised on a swallowed check
    /// failure (nothing changed). The UI subscribes to refresh its notice
    /// without awaiting a check.
    /// </summary>
    event EventHandler? UpdateStateChanged;

    /// <summary>
    /// Checks GitHub releases for a newer Curator version than the installed
    /// one and, when one is available, publishes it on
    /// <see cref="LastCheckResult"/> + raises <see cref="UpdateStateChanged"/>.
    /// </summary>
    /// <param name="ct">Cancellation token. <see cref="OperationCanceledException"/>
    /// propagates (cancellation is not a "no update" result). Other exceptions
    /// are caught and logged; the prior <see cref="LastCheckResult"/> is left
    /// unchanged and no event is raised.</param>
    /// <returns>The available <see cref="AppUpdateInfo"/> when an update was
    /// found, or <c>null</c> when self-update is unsupported, no update is
    /// available, or the check failed. Never throws for non-cancellation
    /// failures (mirrors
    /// <see cref="Modificus.Curator.Integrations.IUpdateCheckService"/>.</returns>
    Task<AppUpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the update resolved by the last successful
    /// <see cref="CheckForUpdatesAsync"/> (staging it for
    /// <see cref="ApplyUpdatesAndRestart"/>). The download runs under an
    /// indeterminate modal spinner in the UI; no incremental percentage is
    /// surfaced. On success, publishes the pending update on
    /// <see cref="UpdatePendingRestart"/> + raises
    /// <see cref="UpdateStateChanged"/>.
    /// </summary>
    /// <param name="ct">Cancellation token. Honored by the underlying download
    /// call.</param>
    /// <exception cref="InvalidOperationException">Thrown when no check has
    /// resolved an update to download (including when self-update is
    /// unsupported, since the check short-circuits without populating the
    /// pending update). The UI gates the download on
    /// <see cref="IsUpdateSupported"/> and a non-null
    /// <see cref="LastCheckResult"/>, so reaching this state is a wiring
    /// mistake worth surfacing loudly.</exception>
    /// <remarks>
    /// Unlike <see cref="CheckForUpdatesAsync"/>, this method propagates
    /// non-cancellation failures (a checksum mismatch, an update-lock
    /// contention, an IO error) to the caller, because the download is a
    /// user-initiated action whose errors the user needs to see.
    /// <para>
    /// <see cref="OperationCanceledException"/> also propagates.</para>
    /// </remarks>
    Task DownloadUpdatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Exits this process and applies the downloaded update on restart. The
    /// process terminates as part of this call (Velopack relaunches the new
    /// version). No-op (logs and returns) when self-update is unsupported or no
    /// update has been downloaded.
    /// </summary>
    void ApplyUpdatesAndRestart();
}
