namespace Modificus.Curator.General;

/// <summary>
/// Persists non-critical **runtime application state**: values that capture
/// "where the app left off" (e.g. the last-selected profile) rather than user
/// system settings. Backed by a small JSON file kept under the app-data dir,
/// separate from <see cref="Config.CuratorConfig"/> (which holds system settings
/// only). Kept deliberately narrow on purpose: when the only state is
/// <see cref="ActiveProfileId"/>, a tiny dedicated store is the honest model
/// and keeps the settings schema pure.
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
}
