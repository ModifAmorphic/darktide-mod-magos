using Magos.Modificus.Mods;

namespace Magos.Modificus.Integrations;

/// <summary>
/// Checks a profile's Nexus-sourced mods for available updates. On
/// <see cref="CheckAsync"/>, queries the Nexus "recently updated" endpoint once
/// (one API call, regardless of how many mods the profile has), intersects the
/// result with the profile's <see cref="LatestPolicy"/> +
/// <see cref="NexusSource"/> mods, and flags the ones whose imported version
/// predates the Nexus-reported latest file update.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Nexus-only: GitHub is descoped from Phase 4 (no GitHub code
/// paths anywhere in the check), and Untracked mods have no remote to query.
/// <see cref="PinnedPolicy"/> mods are frozen by definition, so they are skipped
/// too. Only <see cref="LatestPolicy"/> + <see cref="NexusSource"/> mods are
/// checked.</para>
/// <para>
/// <b>One API call per check.</b> The Nexus v1
/// <c>GET /v1/games/{domain}/mods/updated.json?period=1m</c> endpoint lists
/// every mod for the game updated in the past month, so the check scales with
/// the response size, not the profile size. Stage 5 binds badges to
/// <see cref="LastResult"/> + <see cref="CheckCompleted"/> without re-running
/// the check.</para>
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
    /// Checks <paramref name="profileId"/>'s Nexus mods (LatestPolicy +
    /// NexusSource) for available updates. Background, non-blocking: the caller
    /// fires-and-forgets this on profile load. Returns the result (empty if no
    /// updates, rate-limited, no checkable mods, or no Nexus auth configured).
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
    /// The last check result, or <c>null</c> before the first check completes.
    /// Stage 5 reads this to render badges without awaiting
    /// <see cref="CheckAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written from a background task (the fire-and-forget
    /// <see cref="CheckAsync"/> invocation) and read by the UI thread. The write
    /// is taken under an internal lock together with the
    /// <see cref="CheckCompleted"/> invocation so an event subscriber observes
    /// the result that was just published; reads are lock-free. Reference
    /// assignment is atomic on every target runtime, so the lock-free read can
    /// at worst observe a one-check-stale value (corrected on the next
    /// <see cref="CheckCompleted"/>).</para>
    /// </remarks>
    UpdateCheckResult? LastResult { get; }

    /// <summary>
    /// Raised (on the completing thread) when a check finishes, successful or
    /// not. Stage 5 subscribes to refresh badges without awaiting
    /// <see cref="CheckAsync"/>. Always raised exactly once per
    /// <see cref="CheckAsync"/> call (including the no-auth short-circuit and
    /// the rate-limited / failure paths), with the same result that was just
    /// set on <see cref="LastResult"/>.
    /// </summary>
    event EventHandler<UpdateCheckResult?>? CheckCompleted;
}

/// <summary>
/// The result of an update check: the mods with a potential update available,
/// the check timestamp, and whether the check was rate-limited.
/// </summary>
/// <param name="Updates">The mods with a potential update available (the
/// API's latest file-update timestamp is newer than the imported version's
/// date). May be empty (no updates, or the check was short-circuited by no
/// auth / no checkable mods / a rate limit / an API failure).</param>
/// <param name="CheckedAt">When the check ran (UTC).</param>
/// <param name="RateLimited"><c>true</c> if the check was skipped because the
/// Nexus daily or hourly quota was reported exhausted. Stage 5 surfaces a
/// "check incomplete" indicator rather than "all up to date" in this case, so
/// the user understands the badges may not reflect the latest
/// state.</param>
public sealed record UpdateCheckResult(
    IReadOnlyList<ModUpdateInfo> Updates,
    DateTimeOffset CheckedAt,
    bool RateLimited);

/// <summary>
/// One mod flagged by an update check. Mirrors the identifying fields Stage 5
/// needs to render a per-row "update available" badge and drive the per-mod
/// update button (which calls <c>IModAcquisitionService</c>).
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
/// (UTC), from <see cref="ModUpdate.LatestFileUpdateUtc"/>. Stage 5 may show
/// this as "updated &lt;date&gt;" context.</param>
public sealed record ModUpdateInfo(
    Guid ContainerId,
    int ModId,
    string ModName,
    string CurrentVersion,
    DateTimeOffset? LatestUpdateAt);
