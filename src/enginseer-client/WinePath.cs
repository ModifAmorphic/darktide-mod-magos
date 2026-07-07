namespace Modificus.Curator.EnginseerClient;

/// <summary>
/// Native POSIX → Wine path translation. Wine maps the POSIX root to the
/// <c>Z:</c> drive, so an absolute POSIX path <c>/home/u/mods</c> becomes the
/// Windows path <c>Z:\home\u\mods</c> (forward slashes → backslashes, <c>Z:</c>
/// prefix). The launcher runs under Wine on Linux and needs Windows-style paths
/// for its <c>--game-binary</c> / <c>--mod-path</c> flags.
/// </summary>
internal static class WinePath
{
    /// <summary>
    /// Translates an absolute POSIX path to its Wine <c>Z:\</c> form: replace
    /// every forward slash with a backslash and prefix <c>Z:</c>. The only paths
    /// that occur (<c>--mod-path</c>, <c>--game-binary</c>) are always absolute,
    /// so the leading slash maps cleanly to the Wine drive root (e.g.
    /// <c>/home/u/mods</c> → <c>Z:\home\u\mods</c>, <c>/</c> → <c>Z:\</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="posixPath"/> is null or whitespace.</exception>
    internal static string ToWine(string posixPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(posixPath);
        var backslashed = posixPath.Replace('/', '\\');
        return "Z:" + backslashed;
    }
}
