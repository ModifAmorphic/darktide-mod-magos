using System.Globalization;
using System.Text.RegularExpressions;

namespace Modificus.Curator.Steam;

/// <summary>
/// Minimal parser for Steam's <c>libraryfolders.vdf</c>. The file maps numbered
/// library entries to their root <c>"path"</c> values; this extracts those
/// paths in document order -- enough to drive multi-library Darktide discovery
/// without pulling in a heavyweight VDF dependency.
/// </summary>
/// <remarks>
/// <para>VDF stores Windows paths with C-style escapes (<c>\\</c> for a single
/// backslash); the parser unescapes <c>\\</c> → <c>\</c> and <c>\"</c> →
/// <c>"</c>. Linux Steam writes forward slashes (no escapes).</para>
/// <para>The match is case-insensitive on the key (Steam writes lowercase
/// <c>"path"</c>) to be forgiving of hand-edited fixtures, and anchored to the
/// <c>"path"</c> key so it won't match arbitrary path-like values elsewhere in
/// the structure.</para>
/// </remarks>
internal static class LibraryFoldersVdf
{
    // Matches:  "path"   "<value>"   (whitespace between key and value, value is quote-delimited)
    private static readonly Regex PathPattern = new(
        @"""path""\s+""(?<value>(?:\\.|[^""\\])*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses <c>libraryfolders.vdf</c> content → the library root paths, in
    /// document order. Empty / whitespace input yields an empty list. Duplicate
    /// paths are preserved (the caller de-dups if it cares).
    /// </summary>
    public static IReadOnlyList<string> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        foreach (Match match in PathPattern.Matches(content))
        {
            var raw = match.Groups["value"].Value;
            var unescaped = Unescape(raw);
            if (!string.IsNullOrWhiteSpace(unescaped))
            {
                paths.Add(unescaped.Trim());
            }
        }

        return paths;
    }

    // Unescapes the minimal VDF escapes that can appear in a path value.
    private static string Unescape(string value) =>
        value
            .Replace("\\\\", "\u0000", StringComparison.Ordinal) // protect \\ first
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\u0000", "\\", StringComparison.Ordinal);
}
