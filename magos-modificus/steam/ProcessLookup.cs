using System.ComponentModel;
using System.Diagnostics;

namespace Magos.Modificus.Steam;

/// <summary>
/// Production <see cref="IProcessLookup"/> backed by
/// <see cref="Process.GetProcessesByName(string)"/>. Swallows enumeration
/// failures (e.g. permission denied on some Linux setups) as "not running"
/// rather than surfacing them through <see cref="ISteamService.IsGameRunning"/>.
/// </summary>
internal sealed class ProcessLookup : IProcessLookup
{
    public bool IsRunning(string processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Win32Exception)
        {
            // Process enumeration can be denied (e.g. restricted Linux runners);
            // treat as "not running" so a launch isn't blocked on a false negative.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
