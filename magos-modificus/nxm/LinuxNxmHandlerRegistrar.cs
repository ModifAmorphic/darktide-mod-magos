using System.Runtime.Versioning;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Nxm;

/// <summary>
/// Linux <see cref="INxmHandlerRegistrar"/>. Writes
/// <c>~/.local/share/applications/magos-nxm-handler.desktop</c> (the source of
/// truth most desktops honor) and best-effort runs
/// <c>xdg-mime default magos-nxm-handler.desktop x-scheme-handler/nxm</c>. The
/// <c>xdg-mime</c> invocation is best-effort: if the tool is absent the
/// <c>.desktop</c> file is still the registration; the failure is logged.
/// </summary>
/// <remarks>
/// Annotated <c>[SupportedOSPlatform("linux")]</c> and registered ONLY on Linux
/// by <c>AddNxm()</c>. Resolves the applications dir via
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> (the codebase
/// convention for per-user data). The dir + the <c>xdg</c> runner are
/// overridable for deterministic testing.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxNxmHandlerRegistrar : INxmHandlerRegistrar
{
    private const string XdgHandlerDesktopEntry = "[Desktop Entry]";

    private readonly string _handlerExePath;
    private readonly string _applicationsDir;
    private readonly Func<string, (int exitCode, string output)> _runXdg;
    private readonly ILogger<LinuxNxmHandlerRegistrar> _logger;

    public LinuxNxmHandlerRegistrar(
        string handlerExePath,
        ILogger<LinuxNxmHandlerRegistrar> logger,
        string? applicationsDir = null,
        Func<string, (int exitCode, string output)>? runXdg = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerExePath);
        _handlerExePath = handlerExePath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _applicationsDir = applicationsDir
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(_applicationsDir))
            throw new InvalidOperationException("Could not resolve the local applications directory.");
        _runXdg = runXdg ?? RunXdgDefault;
    }

    private string DesktopFilePath => Path.Combine(_applicationsDir, NxmHandlerPaths.LinuxDesktopFileId);

    /// <inheritdoc />
    [SupportedOSPlatform("linux")]
    public bool IsRegistered()
    {
        // The desktop file must exist, AND xdg-mime must report our handler as
        // the default for the nxm scheme. A present file with xdg not pointing
        // at us is "registered on disk but not active": treat as not registered
        // so the caller can re-run Register() to fix it.
        if (!File.Exists(DesktopFilePath))
            return false;

        try
        {
            var (exitCode, output) = _runXdg("query default x-scheme-handler/nxm");
            if (exitCode != 0)
                return false;
            return output.Trim().Equals(NxmHandlerPaths.LinuxDesktopFileId, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            // xdg-mime absent or failing: treat as "unknown" rather than throw.
            _logger.LogDebug(ex, "xdg-mime query failed; treating nxm handler as not registered.");
            return false;
        }
    }

    /// <inheritdoc />
    [SupportedOSPlatform("linux")]
    public void Register()
    {
        Directory.CreateDirectory(_applicationsDir);
        File.WriteAllText(DesktopFilePath, BuildDesktopEntry());

        // Best-effort: xdg-mime may be absent on minimal WMs. The .desktop file
        // is the source of truth most desktops honor; a missing xdg is logged,
        // not thrown.
        try
        {
            var (exitCode, _) = _runXdg(
                $"default {NxmHandlerPaths.LinuxDesktopFileId} x-scheme-handler/nxm");
            if (exitCode != 0)
                _logger.LogWarning(
                    "xdg-mime default returned {Exit}; the .desktop file is written regardless.", exitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "xdg-mime invocation failed (best-effort); the .desktop file is written.");
        }

        _logger.LogInformation("Registered nxm:// handler desktop file at {Path}.", DesktopFilePath);
    }

    /// <inheritdoc />
    [SupportedOSPlatform("linux")]
    public void Unregister()
    {
        if (File.Exists(DesktopFilePath))
        {
            try
            {
                File.Delete(DesktopFilePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete the nxm handler desktop file (best-effort).");
                throw;
            }
        }

        // Best-effort: ask xdg-mime to forget (it has no "unset" verb; the
        // scheme simply falls back to whatever else claims it once our file is
        // gone). We invoke query to surface the state, but do not throw.
        try
        {
            _runXdg("query default x-scheme-handler/nxm");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "xdg-mime query after unregister failed (ignored).");
        }

        _logger.LogInformation("Unregistered nxm:// handler desktop file.");
    }

    internal string BuildDesktopEntry()
    {
        // %u is the Freedesktop URL field code; the desktop environment
        // substitutes the clicked nxm:// URL for it when launching.
        return
            $"{XdgHandlerDesktopEntry}\n" +
            "Type=Application\n" +
            "Name=Magos NXM Handler\n" +
            $"Exec={FormatExec()}\n" +
            "NoDisplay=true\n" +
            "MimeType=x-scheme-handler/nxm;\n";
    }

    // The handler exe path may contain spaces; the .desktop Exec spec uses
    // quoting for the executable. Keep it simple and faithful: quote the path,
    // leave the %u field code unquoted so the shell substitutes it verbatim.
    private string FormatExec() => $"\"{_handlerExePath}\" %u";

    private static (int exitCode, string output) RunXdgDefault(string arguments)
    {
        var psi = new ProcessStartInfo("xdg-mime", arguments)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start xdg-mime.");
        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit();
        return (proc.ExitCode, output);
    }
}
