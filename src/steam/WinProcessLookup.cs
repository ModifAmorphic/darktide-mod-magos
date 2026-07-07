using System.ComponentModel;
using System.Diagnostics;

namespace Modificus.Curator.Steam;

/// <summary>
/// Windows <see cref="IProcessLookup"/>. Matches process comm via
/// <see cref="Process.GetProcessesByName(string)"/> -- the unchanged Windows
/// behavior, factored into its own implementation. Enumeration failures
/// (denied, or the runner lacks process-query rights) degrade to "not running"
/// so a launch isn't blocked on a false negative. Selected once at DI
/// registration time by <c>AddSteam()</c> for non-Linux hosts.
/// </summary>
internal sealed class WinProcessLookup : IProcessLookup
{
    /// <inheritdoc />
    public bool IsRunning(string processName)
    {
        if (string.IsNullOrEmpty(processName))
            return false;

        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch (Win32Exception)
        {
            // Process enumeration can be denied (e.g. restricted runners);
            // treat as "not running" so a launch isn't blocked on a false negative.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
