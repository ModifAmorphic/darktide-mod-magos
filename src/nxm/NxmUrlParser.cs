using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Modificus.Curator.Nxm;

/// <summary>
/// Pure <c>nxm://</c> URL parser. Mirrors the <c>ModSourceParser</c> style: a
/// static <c>TryParse</c> that returns <c>false</c> on any malformed input and
/// never throws. The router parses incoming IPC URLs through this; the handler
/// exe forwards the raw string and never inspects it.
/// </summary>
/// <remarks>
/// <para>Recognized shapes (grounded against MO2 <c>nxmurl.cpp</c> and NMA
/// <c>OAuth.cs</c>):</para>
/// <list type="bullet">
/// <item>Mod download: <c>nxm://&lt;game&gt;/mods/&lt;modId&gt;/files/&lt;fileId&gt;</c>
/// with optional <c>key</c> (string), <c>expires</c> (epoch seconds),
/// <c>user_id</c> query parameters.</item>
/// <item>OAuth callback: <c>nxm://oauth/callback?code=&lt;code&gt;&amp;state=&lt;state&gt;</c>.
/// Parsed so the router can recognize the shape (rather than classifying it as
/// unknown); the router logs + drops these. Curator OAuth uses loopback redirect
/// (RFC 8252), independent of the <c>nxm://</c> handler, so this URL kind is
/// never actually delivered over IPC in normal operation. Kept for parser
/// completeness.</item>
/// <item>Collection: <c>nxm://&lt;game&gt;/collections/&lt;id&gt;/revisions/&lt;rev&gt;</c>.</item>
/// </list>
/// <para>The scheme is matched case-insensitively. Numeric path segments must
/// be positive integers; non-numeric or non-positive values reject. Query
/// parameters that are empty or non-numeric (where a number is required) parse
/// to null rather than rejecting the whole URL.</para>
/// </remarks>
public static class NxmUrlParser
{
    private const string Scheme = "nxm";

    /// <summary>
    /// Parses a raw <c>nxm://</c> URL string. Returns <c>false</c> (with a null
    /// <paramref name="url"/>) on any malformed input; never throws.
    /// </summary>
    public static bool TryParse(string raw, [MaybeNullWhen(false)] out NxmUrl url)
    {
        url = null;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // Uri handles the nxm scheme as a generic hierarchical URI (scheme://
        // authority/path?query). Trim is faithful here: Uri never mutates the
        // Raw value, which we echo straight back onto the parsed record.
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.Segments;

        // OAuth callback: the host is "oauth", the path is "/callback".
        if (string.Equals(uri.Host, "oauth", StringComparison.OrdinalIgnoreCase))
            return TryParseOAuth(raw, segments, uri.Query, out url);

        // Otherwise the host is the game domain. The first path segment
        // disambiguates mod downloads from collections.
        if (segments.Length < 2)
            return false;

        var kind = Unescape(segments[1]).TrimEnd('/');
        if (string.Equals(kind, "mods", StringComparison.OrdinalIgnoreCase))
            return TryParseModDownload(raw, uri.Host, segments, uri.Query, out url);
        if (string.Equals(kind, "collections", StringComparison.OrdinalIgnoreCase))
            return TryParseCollection(raw, uri.Host, segments, out url);

        return false;
    }

    // ---- shape-specific parsers -------------------------------------------

    private static bool TryParseModDownload(
        string raw, string game, string[] segments, string query,
        [MaybeNullWhen(false)] out NxmUrl url)
    {
        url = null;

        // Expected layout: / mods/ <modId>/ files/ <fileId>
        if (segments.Length != 5)
            return false;
        if (!IsSegment(segments[1], "mods"))
            return false;
        if (!IsSegment(segments[3], "files"))
            return false;
        if (!TryParsePositiveInt(segments[2], out var modId))
            return false;
        if (!TryParsePositiveInt(segments[4], out var fileId))
            return false;

        ParseDownloadQuery(query, out var key, out var expires, out var userId);

        url = new NxmModDownloadUrl(raw, game, modId, fileId, key, expires, userId);
        return true;
    }

    private static bool TryParseOAuth(
        string raw, string[] segments, string query,
        [MaybeNullWhen(false)] out NxmUrl url)
    {
        url = null;

        // Expected layout: / callback
        if (segments.Length != 2)
            return false;
        if (!IsSegment(segments[1], "callback"))
            return false;

        var queryParams = ParseQuery(query);
        // Both code and state are required and must be non-empty.
        if (!queryParams.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            return false;
        if (!queryParams.TryGetValue("state", out var state) || string.IsNullOrEmpty(state))
            return false;

        url = new NxmOAuthCallbackUrl(raw, code, state);
        return true;
    }

    private static bool TryParseCollection(
        string raw, string game, string[] segments,
        [MaybeNullWhen(false)] out NxmUrl url)
    {
        url = null;

        // Expected layout: / collections/ <id>/ revisions/ <rev>
        if (segments.Length != 5)
            return false;
        if (!IsSegment(segments[1], "collections"))
            return false;
        if (!IsSegment(segments[3], "revisions"))
            return false;

        var collectionId = Unescape(segments[2]).TrimEnd('/');
        if (string.IsNullOrEmpty(collectionId))
            return false;
        if (!TryParsePositiveInt(segments[4], out var revision))
            return false;

        url = new NxmCollectionUrl(raw, game, collectionId, revision);
        return true;
    }

    // ---- query parsing ----------------------------------------------------

    private static void ParseDownloadQuery(string rawQuery, out string? key, out long? expires, out long? userId)
    {
        key = null;
        expires = null;
        userId = null;

        foreach (var (name, value) in EnumerateQuery(rawQuery))
        {
            if (string.IsNullOrEmpty(value))
                continue;

            if (string.Equals(name, "key", StringComparison.OrdinalIgnoreCase))
            {
                key = value;
            }
            else if (string.Equals(name, "expires", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseNonNegativeLong(value, out var ex))
                    expires = ex;
            }
            else if (string.Equals(name, "user_id", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseNonNegativeLong(value, out var uid))
                    userId = uid;
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(string rawQuery)
    {
        // Ordinal-ignore-case keys so callers can look up by canonical name.
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in EnumerateQuery(rawQuery))
            dict[name] = value;
        return dict;
    }

    private static IEnumerable<(string Name, string Value)> EnumerateQuery(string rawQuery)
    {
        var query = rawQuery;
        if (!string.IsNullOrEmpty(query) && query[0] == '?')
            query = query[1..];

        if (string.IsNullOrEmpty(query))
            yield break;

        foreach (var pair in query.Split('&'))
        {
            if (string.IsNullOrEmpty(pair))
                continue;

            var eq = pair.IndexOf('=');
            string name, value;
            if (eq < 0)
            {
                name = Unescape(pair);
                value = string.Empty;
            }
            else
            {
                name = Unescape(pair[..eq]);
                value = Unescape(pair[(eq + 1)..]);
            }

            if (name.Length > 0)
                yield return (name, value);
        }
    }

    // ---- helpers ----------------------------------------------------------

    private static bool IsSegment(string raw, string expected)
        => string.Equals(Unescape(raw).TrimEnd('/'), expected, StringComparison.OrdinalIgnoreCase);

    private static string Unescape(string segment)
        => Uri.UnescapeDataString(segment);

    private static bool TryParsePositiveInt(string raw, out int value)
        => int.TryParse(Unescape(raw).TrimEnd('/'), NumberStyles.None, CultureInfo.InvariantCulture, out value)
           && value > 0;

    private static bool TryParseNonNegativeLong(string raw, out long value)
        => long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
}
