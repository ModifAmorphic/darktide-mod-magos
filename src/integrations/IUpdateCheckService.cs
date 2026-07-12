using Modificus.Curator.Mods;

namespace Modificus.Curator.Integrations;

/// <summary>
/// Checks a profile's Nexus-sourced mods for available updates. Both check
/// shapes (<see cref="CheckAsync"/> + <see cref="CheckThoroughAsync"/>) run the
/// same v2 GraphQL <c>modsByUid</c> batch query (1 API call for all mods) and
/// flag a mod via three tiers: tier 1 the server-computed
/// <see cref="ModUpdateStatus.ViewerUpdateAvailable"/> field (true if the mod has
/// been updated since the user last downloaded it), tier 2 a mod-level version
/// compare (the installed version vs the mod-page header version), and tier 3 a
/// latest-file-version confirmation scoped to tier-2-only flags (resolves the
/// newest MAIN file + clears the flag when it matches the installed version;
/// best-effort + cached). Tier 1 is authoritative and never second-guessed. The
/// two shapes differ only in the result's <see cref="UpdateCheckResult.Thorough"/>
/// flag (kept for interface compatibility).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Nexus-only: GitHub is out of scope (no GitHub code
/// paths anywhere in the check), and Untracked mods have no remote to query.
/// <see cref="PinnedPolicy"/> mods are frozen by definition, so they are skipped
/// too. Only <see cref="LatestPolicy"/> + <see cref="NexusSource"/> mods are
/// checked.</para>
/// <para>
/// <b>One query, all mods.</b> The v2 <c>modsByUid</c> query takes a batch of
/// mod UIDs (<c>game_id * 2^32 + mod_id</c>) and returns the update status for
/// each in a single call. There is no Month-window limitation, no client-side
/// timestamp comparison, no tolerance, and no per-mod reconciliation: the server
/// tracks the user's downloads and computes the signal directly.</para>
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
    /// The periodic check: queries the Nexus v2 GraphQL <c>modsByUid</c> batch
    /// endpoint once (one API call for all checkable mods) and flags each mod via
    /// three tiers (tier 1 <see cref="ModUpdateStatus.ViewerUpdateAvailable"/>,
    /// tier 2 a mod-level version compare, tier 3 a best-effort latest-file-version
    /// confirmation that clears tier-2-only false positives). Used by the periodic
    /// timer + the profile-load trigger. The result has
    /// <see cref="UpdateCheckResult.Thorough"/> = <c>false</c>.
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
    /// The thorough check. Runs the same v2 GraphQL <c>modsByUid</c> batch
    /// query as <see cref="CheckAsync"/> (the v2 query covers all mods in one
    /// call; there is no Month-only vs thorough distinction). Kept for
    /// interface compatibility: the result has
    /// <see cref="UpdateCheckResult.Thorough"/> = <c>true</c>. Both paths run
    /// the same query, so the flag no longer signals a coverage difference.
    /// </summary>
    /// <param name="profileId">The profile whose mods to check.</param>
    /// <param name="ct">Cancellation token. <see cref="OperationCanceledException"/>
    /// propagates; other exceptions are caught + surfaced as an empty result.</param>
    /// <returns>The check result. Never throws for non-cancellation failures.</returns>
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
/// The result of an update check: the mods with an update available, the check
/// timestamp, whether the check was rate-limited, and whether it was the
/// thorough path.
/// </summary>
/// <param name="Updates">The mods with an update available (flagged by any of the
/// three tiers: tier 1 <c>viewerUpdateAvailable</c>, tier 2 a version mismatch, or
/// a tier-2-only flag tier 3 could not clear). May be empty (no updates, or the
/// check was short-circuited by no auth / no checkable mods / a rate limit / an
/// API failure).</param>
/// <param name="CheckedAt">When the check ran (UTC).</param>
/// <param name="RateLimited"><c>true</c> if the check was skipped (or aborted)
/// because the Nexus daily or hourly quota was reported exhausted. The mod-list
/// view surfaces a "check incomplete" indicator rather than "all up to date" in
/// this case, so the user understands the badges may not reflect the latest
/// state.</param>
/// <param name="Thorough"><c>true</c> if this result came from
/// <see cref="IUpdateCheckService.CheckThoroughAsync"/> (the manual "check now"
/// path); <c>false</c> if it came from the periodic
/// <see cref="IUpdateCheckService.CheckAsync"/>. Both paths run the same v2
/// batch query (the query covers all mods regardless), so the flag no longer
/// signals a coverage difference; it is kept for interface compatibility.</param>
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
/// <param name="LatestUpdateAt">The mod's last update time on Nexus (UTC),
/// from the v2 <c>updatedAt</c> field. The mod-list view may show this as
/// "updated &lt;date&gt;" context.</param>
public sealed record ModUpdateInfo(
    Guid ContainerId,
    int ModId,
    string ModName,
    string CurrentVersion,
    DateTimeOffset? LatestUpdateAt);
