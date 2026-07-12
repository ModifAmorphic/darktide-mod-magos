namespace Modificus.Curator.General;

/// <summary>
/// Persists non-critical **runtime application state**: values that capture
/// "where the app left off" (e.g. the last-selected profile, the last update
/// check timestamp) rather than user system settings. Backed by a small JSON
/// file kept under the app-data dir, separate from
/// <see cref="Config.CuratorConfig"/> (which holds system settings only). Kept
/// deliberately narrow on purpose: the three state values (<see cref="ActiveProfileId"/>,
/// <see cref="LastUpdateCheckUtc"/>, and <see cref="ManualRefreshTimestamps"/>)
/// share a tiny dedicated store that is the honest model + keeps the settings
/// schema pure.
/// </summary>
/// <remarks>
/// <para><b>First-run safe:</b> a missing or corrupt state file never throws;
/// reads just return <c>null</c>. Writes are best-effort; runtime app-state is
/// non-critical, so a persistence failure (unwritable dir, full disk) is
/// swallowed rather than crashing the app mid-interaction.</para>
/// </remarks>
public interface IAppStateStore
{
    /// <summary>
    /// The last-chosen active profile id, or <c>null</c> when none is recorded.
    /// Reading returns the persisted value (or <c>null</c> on first run /
    /// corrupt file); assigning persists the value immediately.
    /// </summary>
    Guid? ActiveProfileId { get; set; }

    /// <summary>
    /// The UTC timestamp of the last update check that fired (any trigger), or
    /// <c>null</c> when none has been recorded. Reading returns the persisted
    /// value (or <c>null</c> on first run / corrupt file); assigning persists
    /// immediately. The update-check runner seeds its interval gate from this so
    /// a rapid open/close loop cannot burn an API call per launch.
    /// </summary>
    DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// The timestamps of successful manual "check now" refreshes within the
    /// rolling 1-hour throttle window, or <c>null</c> when none are recorded.
    /// Reading returns the persisted list (or <c>null</c> on first run / corrupt
    /// file); assigning persists immediately. Used by <c>UpdateCheckRunner</c> to
    /// carry the manual throttle's sliding window across restarts.
    /// </summary>
    IReadOnlyList<DateTimeOffset>? ManualRefreshTimestamps { get; set; }
}
