namespace Magos.Modificus.Steam;

/// <summary>
/// Reads the Windows registry for the Steam install path. Abstracted so the
/// discoverer's Windows path resolution is unit-testable on Linux (where the
/// real registry is unavailable). Production implementation is
/// <c>SteamRegistryReader</c> (Windows-only; returns null elsewhere).
/// </summary>
public interface ISteamRegistryReader
{
    /// <summary>
    /// Returns the Steam install path from
    /// <c>HKCU\Software\Valve\Steam\SteamPath</c>, or null on non-Windows / if
    /// the value is absent / unreadable.
    /// </summary>
    string? GetSteamPath();
}
