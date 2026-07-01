using System.Security;
using Microsoft.Win32;

namespace Magos.Modificus.Steam;

/// <summary>
/// Windows-only <see cref="ISteamRegistryReader"/> backed by
/// <c>HKCU\Software\Valve\Steam\SteamPath</c>. Returns null on non-Windows
/// platforms (where the registry is unavailable) and swallows permission / IO
/// failures as "not found" — discovery treats a missing registry value as a
/// signal to fall back to the default path.
/// </summary>
internal sealed class SteamRegistryReader : ISteamRegistryReader
{
    private const string SteamSubKey = @"HKEY_CURRENT_USER\Software\Valve\Steam";
    private const string SteamPathValue = "SteamPath";

    public string? GetSteamPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Registry.GetValue(SteamSubKey, SteamPathValue, null) as string;
        }
        catch (SecurityException)
        {
            // No permission to read the key — fall back to the default path.
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
}
