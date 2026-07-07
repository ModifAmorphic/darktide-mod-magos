using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.Nxm;

/// <summary>
/// Windows <see cref="INxmHandlerRegistrar"/>. Writes
/// <c>HKCU\Software\Classes\nxm</c> (per-user, no elevation) so the OS launches
/// the handler exe for <c>nxm://</c> clicks. Mirrors the
/// <see cref="SupportedOSPlatformAttribute"/> pattern from
/// <c>SteamRegistryReader</c>: the class is annotated
/// <c>[SupportedOSPlatform("windows")]</c> and is registered ONLY on Windows
/// by <c>AddNxm()</c>; resolving <see cref="INxmHandlerRegistrar"/> on Linux
/// fails fast.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsNxmHandlerRegistrar : INxmHandlerRegistrar
{
    private const string ClassesNxmKey = @"Software\Classes\nxm";
    private const string CommandSubKey = @"shell\open\command";
    private const string FriendlyName = "URL:Nexus Mods Link";

    private readonly string _handlerExePath;
    private readonly ILogger<WindowsNxmHandlerRegistrar> _logger;

    public WindowsNxmHandlerRegistrar(string handlerExePath, ILogger<WindowsNxmHandlerRegistrar> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlerExePath);
        _handlerExePath = handlerExePath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    [SupportedOSPlatform("windows")]
    public bool IsRegistered()
    {
        var command = ReadCommandValue();
        return !string.IsNullOrEmpty(command) && PointsAtHandler(command);
    }

    /// <inheritdoc />
    [SupportedOSPlatform("windows")]
    public void Register()
    {
        // HKCU\Software\Classes\nxm: (Default) = friendly name, URL Protocol = "".
        using var nxmKey = Registry.CurrentUser.CreateSubKey(ClassesNxmKey);
        nxmKey.SetValue(null, FriendlyName);
        nxmKey.SetValue("URL Protocol", string.Empty);

        // HKCU\Software\Classes\nxm\shell\open\command: (Default) = "<exe>" "%1".
        using var commandKey = nxmKey.CreateSubKey(CommandSubKey);
        commandKey.SetValue(null, FormatCommand());

        _logger.LogInformation("Registered nxm:// handler at {Exe}.", _handlerExePath);
    }

    /// <inheritdoc />
    [SupportedOSPlatform("windows")]
    public void Unregister()
    {
        // Deletes the whole nxm tree under HKCU\Software\Classes. Idempotent on
        // an absent key (throws only on permission failures, which we surface).
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(ClassesNxmKey, throwOnMissingSubKey: false);
            _logger.LogInformation("Unregistered nxm:// handler.");
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Failed to delete the nxm registry tree (best-effort).");
            throw;
        }
    }

    private string? ReadCommandValue()
    {
        try
        {
            using var commandKey = Registry.CurrentUser.OpenSubKey(
                Path.Combine(ClassesNxmKey, CommandSubKey));
            return commandKey?.GetValue(null) as string;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private bool PointsAtHandler(string commandValue)
    {
        // The command is stored as "<exe-path>" "%1". A loose contains check
        // is robust to quoting/whitespace variants without parsing the quoted
        // form brittlely.
        return commandValue.Contains(_handlerExePath, StringComparison.OrdinalIgnoreCase);
    }

    private string FormatCommand() => $"\"{_handlerExePath}\" \"%1\"";
}
