using System.Text;

namespace Modificus.Curator.Steam;

/// <summary>
/// Linux <see cref="IProcessLookup"/>. Scans <c>/proc/&lt;pid&gt;/cmdline</c>
/// and compares the <c>argv[0]</c> basename-stem to the requested process name,
/// because the kernel <c>comm</c> field (what
/// <see cref="Process.GetProcessesByName(string)"/> reads on Unix, 15-char cap)
/// is unreliable under Proton: Darktide's <c>comm</c> is literally <c>main</c>,
/// which would yield a false negative while the game is running. Swallows
/// enumeration failures (permission denied, exited processes, <c>/proc</c>
/// unavailable) as "not running" rather than surfacing them through
/// <see cref="ISteamService.IsGameRunning"/>. Selected once at DI registration
/// time by <c>AddSteam()</c> for Linux hosts.
/// </summary>
internal sealed class LinuxProcessLookup : IProcessLookup
{
    /// <inheritdoc />
    public bool IsRunning(string processName)
    {
        if (string.IsNullOrEmpty(processName))
            return false;

        var ownPid = Environment.ProcessId;

        // Directory.EnumerateDirectories performs an EAGER existence/permission
        // check on /proc, so a missing/unavailable procfs raises HERE (not later,
        // during enumeration). This outer catch is load-bearing, not dead code --
        // don't "simplify" it away.
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories("/proc");
        }
        catch (DirectoryNotFoundException)
        {
            // /proc unavailable (e.g. non-procfs kernel); degrade to not-running.
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            // Only numeric /proc entries are pids; "self", "net", "fs", ... are not.
            var name = Path.GetFileName(entry);
            if (!int.TryParse(name, out var pid) || pid == ownPid)
                continue; // cheap self-exclusion

            string argv0;
            try
            {
                var cmdline = File.ReadAllBytes(Path.Combine(entry, "cmdline"));
                argv0 = FirstArgv0(cmdline);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                // Unreadable / process exited mid-read -- skip, keep scanning.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (MatchesArgv0(argv0, processName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts <c>argv[0]</c> (the first NUL-separated token) from a raw
    /// <c>/proc/&lt;pid&gt;/cmdline</c> byte buffer. Returns an empty string for
    /// kernel threads / empty cmdline (never matches via <see cref="MatchesArgv0"/>).
    /// </summary>
    private static string FirstArgv0(byte[] cmdline)
    {
        if (cmdline.Length == 0)
            return string.Empty;

        var end = Array.IndexOf(cmdline, (byte)0);
        if (end < 0)
            end = cmdline.Length;
        return end == 0 ? string.Empty : Encoding.UTF8.GetString(cmdline, 0, end);
    }

    /// <summary>
    /// True if the <c>argv[0]</c> path's basename-stem equals
    /// <paramref name="processName"/> (OrdinalIgnoreCase). Handles Windows-style
    /// (<c>S:\...\Darktide.exe</c> → <c>Darktide</c>), bare names
    /// (<c>Darktide.exe</c> → <c>Darktide</c>), and POSIX paths alike. Guards
    /// null/empty/whitespace inputs → false. Match argv[0] only -- a whole-cmdline
    /// substring match is a known false-positive trap (matches the steam.exe
    /// wrapper and the detector itself).
    /// </summary>
    /// <remarks>
    /// Under Proton/wine the launched exe's argv[0] is a <b>Windows-style</b>
    /// path (<c>S:\...\Darktide.exe</c>). <see cref="Path.GetFileNameWithoutExtension"/>
    /// only recognizes the <i>current</i> runtime's directory separators, so on
    /// Linux it would not split on backslashes and yield the wrong stem. Backslashes
    /// are normalized to slashes first so stem extraction is correct on both OSes.
    /// </remarks>
    internal static bool MatchesArgv0(string? argv0, string? processName)
    {
        if (string.IsNullOrWhiteSpace(argv0) || string.IsNullOrEmpty(processName))
            return false;

        return string.Equals(
            Path.GetFileNameWithoutExtension(argv0.Replace('\\', '/')),
            processName,
            StringComparison.OrdinalIgnoreCase);
    }
}
