namespace Modificus.Curator.RelayClient;

/// <summary>
/// Process-launch abstraction used by <see cref="IRelayLaunchService"/> to
/// spawn <c>modificus_relay.exe</c> -- directly on Windows, under <c>proton run</c>
/// on Linux. Abstracted so the launch path is deterministic and mockable in
/// tests: the real <see cref="ProcessLauncher"/> (<c>Process.Start</c>) would
/// spawn a real process and fail against a CI runner with no game install.
/// </summary>
/// <remarks>
/// Mirrors the Steam library's <c>IProcessLookup</c> pattern: the injectable
/// seam is the side-effect, leaving the service under test as pure argument
/// assembly + decision logic.
/// </remarks>
public interface IProcessLauncher
{
    /// <summary>
    /// Starts the process described by <paramref name="request"/> (executable
    /// path, argv-form arguments, and requested environment mutations) and
    /// returns without waiting. Returns <c>true</c> if the process was started;
    /// <c>false</c> if it could not be started (file not found, permission
    /// denied, etc. -- never throws for those).
    /// </summary>
    /// <param name="request">The immutable launch description. The
    /// implementation must apply each argument verbatim (no re-shelling or
    /// concatenation), strip every key in
    /// <see cref="ProcessLaunchRequest.EnvironmentVariablesToRemove"/> from the
    /// inherited environment, then apply
    /// <see cref="ProcessLaunchRequest.EnvironmentOverrides"/> (overrides win
    /// when a key appears in both sets).</param>
    bool Start(ProcessLaunchRequest request);
}
