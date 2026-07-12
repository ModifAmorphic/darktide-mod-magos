namespace Modificus.Curator.Steam;

/// <summary>
/// Normalizes the Windows-style Steam install path that Steam writes to the
/// registry. Steam's cross-platform client stores
/// <c>HKCU\Software\Valve\Steam\SteamPath</c> in Unix style (lowercase drive
/// letter + forward slashes, e.g. <c>c:/program files (x86)/steam</c>); this
/// helper converts it to native Windows form (uppercase drive + backslashes) at
/// the read boundary so the un-normalized value never surfaces.
/// </summary>
/// <remarks>
/// Pure string manipulation: no filesystem, no OS API. The implementation is
/// platform-neutral on purpose so it is directly unit-testable on any OS
/// without tripping the CA1416 platform analyzer. It is deliberately kept off
/// <c>SteamRegistryReader</c> (which is annotated
/// <c>[SupportedOSPlatform("windows")]</c>) so the test project can call it on
/// Linux CI without analyzer friction.
/// </remarks>
internal static class SteamPathNormalizer
{
    /// <summary>
    /// Uppercases a leading lowercase ASCII drive letter (e.g. <c>c:</c> to
    /// <c>C:</c>) and swaps every <c>/</c> for <c>\</c>. Idempotent: an
    /// already-normalized path is returned unchanged. Null / empty are returned
    /// as-is.
    /// </summary>
    internal static string? NormalizeWindowsPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]) && char.IsLower(path[0]))
            path = char.ToUpperInvariant(path[0]) + path.Substring(1);
        return path.Replace('/', '\\');
    }
}
