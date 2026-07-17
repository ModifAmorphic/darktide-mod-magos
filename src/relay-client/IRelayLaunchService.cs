namespace Modificus.Curator.RelayClient;

/// <summary>
/// The launch façade over Modificus Relay. Resolves the profile + Steam
/// discovery, assembles the launcher args, and invokes
/// <c>modificus_relay.exe</c> -- directly on Windows, under <c>proton run</c> on
/// Linux. Fire-and-forget: <see cref="Launch"/> starts the launcher and returns;
/// it does not track the game process.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Launch"/> resolves the profile (via
/// <c>IProfileService.PrepareModRoot</c> -- writes <c>mods.lst</c> and returns the
/// <c>--mod-path</c>) and Steam discovery (via <c>ISteamService.Discover</c>)
/// internally, so the caller just says "launch this profile."</para>
/// <para>
/// Relay-client does NOT prompt -- on incomplete discovery it returns
/// <see cref="LaunchStatus.DiscoveryIncomplete"/> carrying the missing field
/// names so the caller can drive an escape-hatch prompt.</para>
/// </remarks>
public interface IRelayLaunchService
{
    /// <summary>
    /// Launches the given profile modded. Always returns a
    /// <see cref="LaunchResult"/> (never throws for expected conditions):
    /// <list type="bullet">
    /// <item><term><see cref="LaunchStatus.Launched"/></term><description>the launcher process was started.</description></item>
    /// <item><term><see cref="LaunchStatus.DiscoveryIncomplete"/></term><description>Steam discovery is missing required fields for the current OS; <see cref="LaunchResult.MissingDiscoveryFields"/> lists them.</description></item>
    /// <item><term><see cref="LaunchStatus.StagingFailed"/></term><description>the profile's mod root could not be prepared (a staging link could not be created). <see cref="LaunchResult.Message"/> carries the raised exception's body (the runtime/OS error).</description></item>
    /// <item><term><see cref="LaunchStatus.Error"/></term><description>unknown profile, missing runtime dir, or process-start failure -- see <see cref="LaunchResult.Message"/>.</description></item>
    /// </list>
    /// </summary>
    LaunchResult Launch(Guid profileId);
}

/// <summary>
/// The outcome of <see cref="IRelayLaunchService.Launch"/>.
/// </summary>
/// <param name="Status">One of <see cref="LaunchStatus.Launched"/>,
/// <see cref="LaunchStatus.DiscoveryIncomplete"/>, <see cref="LaunchStatus.StagingFailed"/>,
/// or <see cref="LaunchStatus.Error"/>.</param>
/// <param name="Message">Human-readable detail; populated for
/// <see cref="LaunchStatus.Error"/> and <see cref="LaunchStatus.StagingFailed"/>
/// (carries the raised staging exception's body; null otherwise).</param>
/// <param name="MissingDiscoveryFields">The discovery fields the current OS
/// requires but could not be resolved; populated only for
/// <see cref="LaunchStatus.DiscoveryIncomplete"/> (empty otherwise). Field names
/// mirror the <c>DiscoveryResult</c> properties so they map to a prompt.</param>
public sealed record LaunchResult(
    LaunchStatus Status,
    string? Message,
    IReadOnlyList<string> MissingDiscoveryFields);

/// <summary>
/// Coarse outcome of a launch attempt.
/// </summary>
public enum LaunchStatus
{
    /// <summary>The launcher process was started (fire-and-forget -- no
    /// game-process tracking).</summary>
    Launched,

    /// <summary>Steam discovery is missing required fields for the current OS;
    /// <see cref="LaunchResult.MissingDiscoveryFields"/> lists them.</summary>
    DiscoveryIncomplete,

    /// <summary>The profile's mod root could not be prepared: a staging link
    /// could not be created (e.g. Windows on a non-NTFS volume, or no write
    /// access to the profile's <c>staged/</c> directory). <see cref="LaunchResult.Message"/>
    /// carries the raised exception's body (a runtime/OS error, not a string we
    /// invented); the full exception is logged.</summary>
    StagingFailed,

    /// <summary>Anything else: unknown profile, missing runtime dir, or a
    /// process-start failure. See <see cref="LaunchResult.Message"/>.</summary>
    Error,
}
