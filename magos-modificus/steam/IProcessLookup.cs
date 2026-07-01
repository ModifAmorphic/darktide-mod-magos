namespace Magos.Modificus.Steam;

/// <summary>
/// Process lookup used by <see cref="ISteamService.IsGameRunning"/>. Abstracted
/// so the game-running check is deterministic and mockable in tests — the real
/// check (<c>Process.GetProcessesByName</c>) would be non-deterministic against
/// CI runners and platform-dependent in its naming rules.
/// </summary>
public interface IProcessLookup
{
    /// <summary>
    /// True if at least one running process matches <paramref name="processName"/>.
    /// Never throws — process enumeration failures degrade to "not running."
    /// </summary>
    bool IsRunning(string processName);
}
