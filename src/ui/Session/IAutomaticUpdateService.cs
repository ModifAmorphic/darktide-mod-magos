using Modificus.Curator.Integrations;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The opt-in Premium automatic mod-update installer. Chained directly from
/// <see cref="UpdateCheckRunner"/> after a check completes, it sequentially
/// installs flagged updates for the active profile's Nexus Latest mods when the
/// user has enabled it AND a fresh Premium verification passes. Independent of
/// <see cref="ViewModels.ModListViewModel"/> (to avoid the existing
/// ModListViewModel -> UpdateCheckRunner dependency becoming circular) and
/// shares the global <see cref="UpdateCoordinator"/> with the manual update
/// action so the two paths never install the same mod concurrently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chained, not subscribed.</b> The runner captures the exact
/// <see cref="UpdateCheckResult"/> from the check invocation (not a potentially
/// raced <see cref="IUpdateCheckService.LastResult"/>) and awaits
/// <see cref="RunAfterCheckAsync"/>. So a manual CheckNow keeps its spinner
/// active through the installations, and an automatic trigger's check + install
/// form one ordered task (asynchronous + non-blocking to the UI, but sequential
/// within the run).</para>
/// <para>
/// <b>Gating.</b> Execution starts only when ALL hold: the result's outcome is
/// authoritative <see cref="CheckOutcome.Success"/>, the result has updates,
/// <c>NexusConfig.AutomaticUpdatesEnabled</c> is on, the active profile still
/// matches the check's profile, and a fresh
/// <see cref="INexusAuthService.GetCurrentStateAsync"/> returns
/// <see cref="NexusAuthState.IsPremium"/> == <c>true</c>. The Premium request
/// fires ONLY when a successful check found updates AND auto-update is enabled,
/// so a regular user or an empty result costs no extra API call. This is
/// independent of <c>NexusConfig.AutoUpdateCheckEnabled</c>: periodic checking
/// being off never disables automatic installation (startup + switch + manual
/// checks still drive it).</para>
/// <para>
/// <b>Per-mod revalidation + isolation.</b> Before each install, the service
/// revalidates the active profile, the mod's membership + policy, its source +
/// mod id, and that the installed version still matches the result snapshot. A
/// profile switch stops the whole batch; any other mismatch skips that mod. A
/// per-mod failure is caught + recorded; it does not abort later mods. A
/// successful install acknowledges/clears its known-update entry immediately.</para>
/// <para>
/// <b>Feedback.</b> A fully successful batch is silent. A batch with one or more
/// failures surfaces a single aggregated, localized summary alert after the
/// batch. <see cref="UpdatesApplied"/> is raised when at least one install
/// succeeded so <see cref="ViewModels.ModListViewModel"/> can reload the list
/// (the new versions + cleared flags) without the service taking a dependency on
/// it. <see cref="ModUpdateProgress"/> is raised per mod (active=true before the
/// acquisition, active=false from the per-mod finally) so the list VM can show
/// the spinner on the currently installing row; the spinner moves row by row as
/// the sequential batch advances.</para>
/// </remarks>
public interface IAutomaticUpdateService
{
    /// <summary>
    /// Raised (on the caller's thread) when at least one install in the last
    /// batch succeeded. <see cref="ViewModels.ModListViewModel"/> subscribes and
    /// reloads so the new versions + cleared flags show without the service
    /// depending on it.
    /// </summary>
    event EventHandler? UpdatesApplied;

    /// <summary>
    /// Raised (on the caller's thread) per mod during an automatic batch:
    /// <see cref="ModUpdateProgressEventArgs.IsActive"/> == <c>true</c>
    /// immediately before the per-mod acquisition attempt, <c>false</c> from the
    /// per-mod finally block (success, failure, or cancellation). Deterministic
    /// start/stop ordering per sequential item. <see cref="ViewModels.ModListViewModel"/>
    /// subscribes, marshals to the UI thread, finds the row by
    /// <see cref="ModUpdateProgressEventArgs.ContainerId"/>, and sets its
    /// <c>IsUpdating</c> so the row-level spinner (left of the Nexus badge)
    /// reflects the currently installing mod. An event for a row no longer
    /// present (after a profile switch / reload) is ignored.
    /// </summary>
    event EventHandler<ModUpdateProgressEventArgs>? ModUpdateProgress;

    /// <summary>
    /// Runs the automatic-install batch for <paramref name="result"/> scoped to
    /// <paramref name="profileId"/>, after the gates (authoritative success,
    /// updates present, automatic updates enabled, profile still active, fresh
    /// Premium verification). Sequential, one install at a time under the global
    /// <see cref="UpdateCoordinator"/>. Per-mod failures are isolated; the
    /// aggregated summary alert (if any) is shown after the batch.
    /// </summary>
    /// <param name="result">The exact result captured from the check invocation
    /// (not <see cref="IUpdateCheckService.LastResult"/>).</param>
    /// <param name="profileId">The profile the check ran against (the batch only
    /// runs when this still matches the session's active profile).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RunAfterCheckAsync(UpdateCheckResult result, Guid profileId, CancellationToken ct = default);
}

/// <summary>
/// Event payload for <see cref="IAutomaticUpdateService.ModUpdateProgress"/>:
/// which container is being installed and whether the install is active
/// (starting) or inactive (done, whatever the outcome). Immutable.
/// </summary>
/// <param name="ContainerId">The container id of the mod being installed.</param>
/// <param name="IsActive"><c>true</c> when the install is starting (raised before
/// the acquisition attempt); <c>false</c> when it finished (raised from the
/// per-mod finally block, regardless of success, failure, or cancellation).</param>
public sealed record ModUpdateProgressEventArgs(Guid ContainerId, bool IsActive);
