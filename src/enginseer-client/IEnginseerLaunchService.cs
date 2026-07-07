namespace Modificus.Curator.EnginseerClient;

/// <summary>
/// The v1 launch façade over the Enginseer runtime. Resolves the profile +
/// Steam discovery, assembles the launcher args, and invokes
/// <c>curator_launcher.exe</c> -- directly on Windows, under <c>proton run</c> on
/// Linux. Fire-and-forget in v1: <see cref="Launch"/> starts the launcher and
/// returns; it does not track the game process.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Launch"/> resolves the profile (via
/// <c>IProfileService.PrepareModRoot</c> -- writes <c>mods.lst</c> and returns the
/// <c>--mod-path</c>) and Steam discovery (via <c>ISteamService.Discover</c>)
/// internally, so the caller just says "launch this profile."</para>
/// <para>
/// Enginseer-client does NOT prompt -- on incomplete discovery it returns
/// <see cref="LaunchStatus.DiscoveryIncomplete"/> carrying the missing field
/// names so the UI (a later phase) can drive an escape-hatch prompt.</para>
/// <para>
/// A future overload accepting a cached <c>DiscoveryResult</c> (to avoid
/// redundant discovery on repeated launches) is an intended clean addition:
/// <see cref="Launch"/> is designed so a <c>Launch(Guid, DiscoveryResult)</c>
/// sibling slots in without breaking existing callers.</para>
/// </remarks>
public interface IEnginseerLaunchService
{
    /// <summary>
    /// Launches the given profile modded. Always returns a
    /// <see cref="LaunchResult"/> (never throws for expected conditions):
    /// <list type="bullet">
    /// <item><term><see cref="LaunchStatus.Launched"/></term><description>the launcher process was started.</description></item>
    /// <item><term><see cref="LaunchStatus.DiscoveryIncomplete"/></term><description>Steam discovery is missing required fields for the current OS; <see cref="LaunchResult.MissingDiscoveryFields"/> lists them.</description></item>
    /// <item><term><see cref="LaunchStatus.Error"/></term><description>unknown profile, missing runtime dir, or process-start failure -- see <see cref="LaunchResult.Message"/>.</description></item>
    /// </list>
    /// </summary>
    LaunchResult Launch(Guid profileId);
}

/// <summary>
/// The outcome of <see cref="IEnginseerLaunchService.Launch"/>.
/// </summary>
/// <param name="Status">One of <see cref="LaunchStatus.Launched"/>,
/// <see cref="LaunchStatus.DiscoveryIncomplete"/>, <see cref="LaunchStatus.Error"/>.</param>
/// <param name="Message">Human-readable detail; populated for
/// <see cref="LaunchStatus.Error"/> (null otherwise).</param>
/// <param name="MissingDiscoveryFields">The discovery fields the current OS
/// requires but could not be resolved; populated only for
/// <see cref="LaunchStatus.DiscoveryIncomplete"/> (empty otherwise). Field names
/// mirror the <c>DiscoveryResult</c> properties so the UI can map them to a prompt.</param>
public sealed record LaunchResult(
    LaunchStatus Status,
    string? Message,
    IReadOnlyList<string> MissingDiscoveryFields);

/// <summary>
/// Coarse outcome of a launch attempt.
/// </summary>
public enum LaunchStatus
{
    /// <summary>The launcher process was started (fire-and-forget -- no game-process tracking in v1).</summary>
    Launched,

    /// <summary>Steam discovery is missing required fields for the current OS;
    /// <see cref="LaunchResult.MissingDiscoveryFields"/> lists them.</summary>
    DiscoveryIncomplete,

    /// <summary>Anything else: unknown profile, missing runtime dir, or a
    /// process-start failure. See <see cref="LaunchResult.Message"/>.</summary>
    Error,
}
