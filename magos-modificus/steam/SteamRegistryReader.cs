using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Magos.Modificus.Steam;

/// <summary>
/// Windows-only <see cref="ISteamRegistryReader"/> backed by
/// <c>HKCU\Software\Valve\Steam\SteamPath</c>. The class is annotated
/// <see cref="SupportedOSPlatformAttribute"/>("windows") to declare its
/// Windows-only nature to the platform analyzer (satisfies CA1416 at the type
/// level — no per-call runtime guard is needed). It is registered ONLY on
/// Windows hosts by <c>AddSteam()</c>; on Linux it is intentionally NOT
/// registered so resolving <see cref="ISteamRegistryReader"/> fails fast — the
/// honest outcome for a Windows-only capability rather than a silent no-op.
/// Swallows permission / IO failures as "not found" — discovery treats a missing
/// registry value as a signal to fall back to the default path.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SteamRegistryReader : ISteamRegistryReader
{
    private const string SteamSubKey = @"HKEY_CURRENT_USER\Software\Valve\Steam";
    private const string SteamPathValue = "SteamPath";

    /// <inheritdoc />
    public string? GetSteamPath()
    {
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
