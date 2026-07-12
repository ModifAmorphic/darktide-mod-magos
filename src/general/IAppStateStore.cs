namespace Modificus.Curator.General;

/// <summary>
/// Persists non-critical **runtime application state**: values that capture
/// "where the app left off" (e.g. the last-selected profile, the last update-check
/// timestamp, the persisted known-update snapshots) rather than user system
/// settings. Backed by a small JSON file kept under the app-data dir, separate
/// from <see cref="Config.CuratorConfig"/> (which holds system settings only). Kept
/// deliberately narrow on purpose: the state values (<see cref="ActiveProfileId"/>,
/// <see cref="LastUpdateCheckUtc"/>, <see cref="ManualRefreshTimestamps"/>, and
/// <see cref="KnownUpdates"/>) share a tiny dedicated store that is the honest
/// model + keeps the settings schema pure.
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

    /// <summary>
    /// Profile-scoped "known update available" snapshots, keyed by profile id, or
    /// <c>null</c> when none are recorded. Reading returns the persisted map (or
    /// <c>null</c> on first run / corrupt file); assigning persists immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used by the Integrations-layer update-state store to persist flagged-update
    /// knowledge so it survives an app restart. The persistence rules (which
    /// outcomes replace, which preserve, how entries self-heal on hydration) live
    /// in the Integrations <c>IUpdateStateStore</c>; this property is the raw
    /// key-value store it reads + writes. Each profile's entry list is replaced
    /// wholesale on a write (the store does not merge at this granularity).</para>
    /// <para>
    /// <b>First-run + upgrade safe.</b> An old <c>app-state.json</c> written
    /// before this field existed deserializes it as <c>null</c> (System.Text.Json
    /// default for an absent nullable member), so a first run after upgrade sees
    /// no persisted snapshots + the next check seeds them. Existing fields
    /// (<see cref="ActiveProfileId"/>, <see cref="LastUpdateCheckUtc"/>,
    /// <see cref="ManualRefreshTimestamps"/>) are preserved on every write: the
    /// whole cached model is rewritten on each assignment.</para>
    /// </remarks>
    IReadOnlyDictionary<Guid, IReadOnlyList<KnownUpdateSnapshot>>? KnownUpdates { get; set; }
}
