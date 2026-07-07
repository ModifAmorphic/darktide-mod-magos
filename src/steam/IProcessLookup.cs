namespace Modificus.Curator.Steam;

/// <summary>
/// Process lookup used by <see cref="ISteamService.IsGameRunning"/>. Abstracted
/// so the game-running check is deterministic and mockable in tests -- the real
/// check would be non-deterministic against CI runners and platform-dependent
/// in its naming rules. Two production implementations, selected once at DI
/// registration time (<c>AddSteam()</c>) from the host OS:
/// <see cref="WinProcessLookup"/> (Windows; matches process comm via
/// <c>Process.GetProcessesByName</c>) and <see cref="LinuxProcessLookup"/>
/// (Linux; matches the <c>/proc/&lt;pid&gt;/cmdline</c> <c>argv[0]</c> stem --
/// the kernel <c>comm</c> is unreliable under Proton).
/// </summary>
public interface IProcessLookup
{
    /// <summary>
    /// True if at least one running process matches <paramref name="processName"/>
    /// (process comm on Windows; <c>argv[0]</c> stem on Linux). Never throws --
    /// process enumeration failures degrade to "not running."
    /// </summary>
    bool IsRunning(string processName);
}
