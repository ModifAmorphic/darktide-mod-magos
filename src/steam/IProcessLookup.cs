namespace Modificus.Curator.Steam;

/// <summary>
/// Process lookup used by <see cref="ISteamService.IsGameRunning"/>. Abstracted
/// so the game-running check is deterministic: process enumeration is
/// platform-dependent in its naming rules (process comm on Windows;
/// <c>/proc/&lt;pid&gt;/cmdline</c> <c>argv[0]</c> stem on Linux, where the
/// kernel <c>comm</c> is unreliable under Proton).
/// </summary>
public interface IProcessLookup
{
    /// <summary>
    /// True if at least one running process matches <paramref name="processName"/>
    /// (process comm on Windows; <c>argv[0]</c> stem on Linux). Never throws:
    /// process enumeration failures degrade to "not running."
    /// </summary>
    bool IsRunning(string processName);
}
