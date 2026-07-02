namespace Magos.Modificus.EnginseerClient;

/// <summary>
/// Process-launch abstraction used by <see cref="IEnginseerLaunchService"/> to
/// spawn <c>magos_launcher.exe</c> — directly on Windows, under <c>proton run</c>
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
    /// Starts <paramref name="filePath"/> with the given arguments and optional
    /// environment-variable overrides, fire-and-forget. Returns <c>true</c> if
    /// the process was started; <c>false</c> if it could not be started (file
    /// not found, permission denied, etc. — never throws for those).
    /// </summary>
    /// <param name="filePath">The executable to start.</param>
    /// <param name="arguments">The full argument list, already in argv form.
    /// The implementation must add each verbatim (no re-shelling or
    /// concatenation) so paths containing spaces survive unchanged.</param>
    /// <param name="environmentVariables">Additional/overriding environment
    /// variables for the child (e.g. the Steam compat vars on Linux); <c>null</c>
    /// inherits the parent's environment.</param>
    bool Start(
        string filePath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environmentVariables);
}
