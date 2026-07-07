using Magos.Modificus.Mods;

namespace Magos.Modificus.Integrations;

/// <summary>
/// Checks a profile's Nexus-sourced mods for available updates. Two check
/// shapes share the same <see cref="LastResult"/> / <see cref="CheckCompleted"/>
/// surface: <see cref="CheckAsync"/> (the cheap Month-only pass, fired on
/// profile load + the periodic timer) and <see cref="CheckThoroughAsync"/> (the
/// per-mod pass the manual "check now" affordance fires, which also catches
/// mods whose latest release predates the Month window). Both intersect the
/// Nexus "recently updated" response with the profile's
/// <see cref="LatestPolicy"/> + <see cref="NexusSource"/> mods and flag the ones
/// whose imported version predates the Nexus-reported latest file update.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Nexus-only: GitHub is out of v1 (no GitHub code
/// paths anywhere in the check), and Untracked mods have no remote to query.
/// <see cref="PinnedPolicy"/> mods are frozen by definition, so they are skipped
/// too. Only <see cref="LatestPolicy"/> + <see cref="NexusSource"/> mods are
/// checked.</para>
/// <para>
/// <b>Two check shapes, one result surface.</b> The Month-only check makes
/// exactly one API call regardless of how many mods the profile has; the
/// thorough check adds one <c>ListModFilesAsync</c> call per profile mod NOT in
/// the Month response (so a mod whose latest release predates the Month window,
/// like one last updated several months ago, is still caught). The result's
/// <see cref="UpdateCheckResult.Thorough"/> flag tells the UI which shape
/// produced it, so the mod-list header can hint "recent updates only, click
/// refresh for a complete check" after a Month-only pass.</para>
/// <para>
/// <b>Best-effort, never throws to the caller.</b> The check is fire-and-forget;
/// the UI layer fires it on profile load. A transient API failure, a missing
/// auth config, or an exhausted rate limit all surface as an empty result (with
/// <see cref="UpdateCheckResult.RateLimited"/> set when applicable) rather than a
/// thrown exception; cancellation alone propagates. The caller has nothing to
/// gain from catching.</para>
/// </remarks>
public interface IUpdateCheckService
{
    /// <summary>
    /// The cheap Month-only check: queries the Nexus "recently updated"
    /// endpoint once (one API call), intersects with the profile's
    /// <see cref="LatestPolicy"/> + <see cref="NexusSource"/> mods, and flags
    /// the ones whose imported version predates the reported latest file update.
    /// Used by the periodic timer + the profile-load trigger. The result has
    /// <see cref="UpdateCheckResult.Thorough"/> = <c>false</c>: mods whose
    /// latest release predates the Month window are NOT caught here (use
    /// <see cref="CheckThoroughAsync"/> for those).
    /// </summary>
    /// <param name="profileId">The profile whose mods to check.</param>
    /// <param name="ct">Cancellation token. Honored during the Nexus API call;
    /// <see cref="OperationCanceledException"/> propagates (cancellation is not
    /// a "no updates" result). Other exceptions are caught and surfaced as an
    /// empty result.</param>
    /// <returns>The check result. Never throws for non-cancellation failures:
    /// an empty result is returned instead, and <see cref="CheckCompleted"/> is
    /// raised. A <see cref="KeyNotFoundException"/> from
    /// <c>IProfileService.GetModList</c> propagates (the caller owns passing a
    /// valid profile id).</returns>
    Task<UpdateCheckResult> CheckAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// The thorough check: does everything <see cref="CheckAsync"/> does, AND
    /// for each profile Nexus+Latest mod NOT in the Month response calls
    /// <c>ListModFilesAsync</c> to resolve the latest MAIN / non-archived file
    /// (category_id 1, newest by <see cref="ModFile.UploadedTimestamp"/>), then
    /// compares that file's upload date against
    /// <c>resolved.RemoteUploadedAt ?? resolved.ImportedAt</c>. Catches mods
    /// whose latest release predates the Month window (a Month-only check
    /// misses them). Used by the manual "check now" affordance. The result has
    /// <see cref="UpdateCheckResult.Thorough"/> = <c>true</c>.
    /// <para>
    /// Rate-limit-aware: if the Month call is rate-limited, the per-mod pass is
    /// skipped + the result has <see cref="UpdateCheckResult.RateLimited"/> =
    /// <c>true</c>. If a <c>ListModFilesAsync</c> call throws
    /// <see cref="NexusRateLimitException"/> mid-pass, the pass stops + returns
    /// what's flagged so far with <see cref="UpdateCheckResult.RateLimited"/> =
    /// <c>true</c> (partial results, not an empty result). Other per-mod
    /// failures are logged + skipped (the pass continues; one mod's transient
    /// failure must not abort the whole check).</para>
    /// </summary>
    /// <param name="profileId">The profile whose mods to check.</param>
    /// <param name="ct">Cancellation token. <see cref="OperationCanceledException"/>
    /// propagates; other exceptions are caught + surfaced as an empty / partial
    /// result.</param>
    /// <returns>The check result (partial if a mid-pass rate-limit was hit).
    /// Never throws for non-cancellation failures.</returns>
    Task<UpdateCheckResult> CheckThoroughAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// The last check result, or <c>null</c> before the first check completes.
    /// The mod-list view reads this to render badges without awaiting a check. Holds the
    /// most recent result regardless of which method (<see cref="CheckAsync"/>
    /// or <see cref="CheckThoroughAsync"/>) produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written from a background task (the fire-and-forget check invocation)
    /// and read by the UI thread. The write is taken under an internal lock
    /// together with the <see cref="CheckCompleted"/> invocation so an event
    /// subscriber observes the result that was just published; reads are
    /// lock-free. Reference assignment is atomic on every target runtime, so
    /// the lock-free read can at worst observe a one-check-stale value
    /// (corrected on the next <see cref="CheckCompleted"/>).</para>
    /// </remarks>
    UpdateCheckResult? LastResult { get; }

    /// <summary>
    /// Raised (on the completing thread) when a check finishes, successful or
    /// not. The mod-list view subscribes to refresh badges without awaiting a check.
    /// Always raised exactly once per <see cref="CheckAsync"/> /
    /// <see cref="CheckThoroughAsync"/> call (including the no-auth
    /// short-circuit and the rate-limited / failure paths), with the same
    /// result that was just set on <see cref="LastResult"/>.
    /// </summary>
    event EventHandler<UpdateCheckResult?>? CheckCompleted;
}

/// <summary>
/// The result of an update check: the mods with a potential update available,
/// the check timestamp, whether the check was rate-limited, and whether it was
/// the thorough per-mod pass.
/// </summary>
/// <param name="Updates">The mods with a potential update available (the
/// API's latest file-update timestamp, or the latest MAIN file's upload time
/// for the thorough pass's per-mod lookups, is newer than the imported
/// version's date). May be empty (no updates, or the check was short-circuited
/// by no auth / no checkable mods / a rate limit / an API failure).</param>
/// <param name="CheckedAt">When the check ran (UTC).</param>
/// <param name="RateLimited"><c>true</c> if the check was skipped (or aborted
/// mid-pass) because the Nexus daily or hourly quota was reported exhausted.
/// The mod-list view surfaces a "check incomplete" indicator rather than "all up to date"
/// in this case, so the user understands the badges may not reflect the latest
/// state.</param>
/// <param name="Thorough"><c>true</c> if this result came from
/// <see cref="IUpdateCheckService.CheckThoroughAsync"/> (the manual "check now"
/// path that also catches mods outside the Month window); <c>false</c> if it
/// came from the Month-only <see cref="IUpdateCheckService.CheckAsync"/>. The
/// mod-list view uses this to hint "recent updates only, click refresh for a complete
/// check" after a Month-only pass, so the user understands the badges may not
/// reflect every available update.</param>
public sealed record UpdateCheckResult(
    IReadOnlyList<ModUpdateInfo> Updates,
    DateTimeOffset CheckedAt,
    bool RateLimited,
    bool Thorough);

/// <summary>
/// One mod flagged by an update check. Mirrors the identifying fields the
/// mod-list view needs to render a per-row "update available" badge and drive the
/// per-mod update button (which calls <c>IModAcquisitionService</c>).
/// </summary>
/// <param name="ContainerId">The flagged mod's container id (the join key back
/// to <see cref="ModContainer"/> and the profile entry).</param>
/// <param name="ModId">The Nexus mod id (from <see cref="NexusSource.ModId"/>).</param>
/// <param name="ModName">The flagged mod's display name (from
/// <see cref="ModContainer.Name"/>).</param>
/// <param name="CurrentVersion">The currently imported version's display tag
/// (from <see cref="ModVersion.VersionString"/>, resolved via
/// <see cref="ModContainer.ResolveVersion"/> with a <see cref="LatestPolicy"/>).</param>
/// <param name="LatestUpdateAt">The Nexus-reported latest file-update time
/// (UTC), from <see cref="ModUpdate.LatestFileUpdateUtc"/>. The mod-list view may show
/// this as "updated &lt;date&gt;" context.</param>
public sealed record ModUpdateInfo(
    Guid ContainerId,
    int ModId,
    string ModName,
    string CurrentVersion,
    DateTimeOffset? LatestUpdateAt);
