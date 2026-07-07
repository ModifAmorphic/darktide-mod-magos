using System.Globalization;

namespace Modificus.Curator.Mods;

/// <summary>
/// Pure URL → canonical <see cref="ModSource"/> parsers. UI-agnostic + unit-
/// tested: the import modal collects URLs (typed or pasted) and the parser
/// turns them into the canonical identity the model stores (Nexus mod id;
/// GitHub owner/repo). Never throws: malformed input returns <c>false</c> and
/// the caller shows a validation message.
/// </summary>
/// <remarks>
/// <para>Accepted shapes:</para>
/// <list type="bullet">
/// <item><c>TryParseNexus</c>: <c>https://www.nexusmods.com/warhammer40kdarktide/mods/{id}</c>
/// (with/without trailing slash + query string) or a plain integer string
/// (<c>"12345"</c>).</item>
/// <item><c>TryParseGitHub</c>: <c>https://github.com/{owner}/{repo}</c>
/// (with/without <c>.git</c> + trailing slash + query string). Requires at
/// least two path segments.</item>
/// </list>
/// <para>
/// Host matching is ordinal ignore-case (so <c>GITHUB.COM</c> still parses);
/// owner/repo are kept verbatim (no lowercasing) so the canonical identity
/// matches what GitHub would report. Whitespace is trimmed before parsing.</para>
/// </remarks>
public static class ModSourceParser
{
    /// <summary>
    /// Parses a Nexus Mods URL (or a plain mod id) into a <see cref="NexusSource"/>.
    /// Returns <c>false</c> on any malformed input; never throws.
    /// </summary>
    /// <example>
    /// <code>
    /// // https://www.nexusmods.com/warhammer40kdarktide/mods/12345  -> NexusSource(12345)
    /// // https://www.nexusmods.com/warhammer40kdarktide/mods/12345/  -> NexusSource(12345)
    /// // https://www.nexusmods.com/warhammer40kdarktide/mods/12345?tab=files -> NexusSource(12345)
    /// // 12345  -> NexusSource(12345)
    /// </code>
    /// </example>
    public static bool TryParseNexus(string input, out NexusSource source)
    {
        source = default!;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();

        // Plain integer: accept as the mod id directly (the modal may collect
        // either a full URL or just the id; both are unambiguous for Nexus).
        if (TryParseModId(trimmed, out var plainId))
        {
            source = new NexusSource { ModId = plainId };
            return true;
        }

        // Otherwise it must be a well-formed Nexus URL with the expected host
        // + a trailing /mods/{id} segment.
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsNexusHost(uri.Host))
        {
            return false;
        }

        // Segments: "/", "warhammer40kdarktide/", "mods/", "12345" (or "12345/").
        // We need: the Darktide game slug at index 1, "mods" at index 2, + the
        // id at index 3. Validating the game slug rejects a pasted URL for the
        // wrong game (the model is Darktide-only).
        var segments = uri.Segments;
        if (segments.Length < 4)
        {
            return false;
        }

        const string darktideSlug = "warhammer40kdarktide";
        if (!string.Equals(Uri.UnescapeDataString(segments[1]).TrimEnd('/'), darktideSlug, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(Uri.UnescapeDataString(segments[2]).TrimEnd('/'), "mods", StringComparison.Ordinal))
        {
            return false;
        }

        var idSegment = Uri.UnescapeDataString(segments[3]).TrimEnd('/');
        if (!TryParseModId(idSegment, out var modId))
        {
            return false;
        }

        source = new NexusSource { ModId = modId };
        return true;
    }

    /// <summary>
    /// Parses a GitHub repo URL into a <see cref="GitHubSource"/>. Returns
    /// <c>false</c> on any malformed input (wrong host, too few path segments);
    /// never throws.
    /// </summary>
    /// <example>
    /// <code>
    /// // https://github.com/owner/repo        -> GitHubSource(owner, repo)
    /// // https://github.com/owner/repo/       -> GitHubSource(owner, repo)
    /// // https://github.com/owner/repo.git    -> GitHubSource(owner, repo)
    /// // https://github.com/owner/repo.git/   -> GitHubSource(owner, repo)
    /// </code>
    /// </example>
    public static bool TryParseGitHub(string input, out GitHubSource source)
    {
        source = default!;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IsGitHubHost(uri.Host))
        {
            return false;
        }

        // Segments: "/", "owner/", "repo" (+ optional trailing slash / sub-paths
        // which we ignore: only the first two segments are the identity).
        var segments = uri.Segments;
        if (segments.Length < 3)
        {
            return false;
        }

        var owner = Uri.UnescapeDataString(segments[1]).TrimEnd('/');
        var repo = Uri.UnescapeDataString(segments[2]).TrimEnd('/');
        // Strip the optional ".git" suffix (clone URLs end in owner/repo.git).
        // Suffix-specific, not char-trimming: a hypothetical repo named "foo.git"
        // would be over-tripped by a char-set trim, so this is explicit.
        const string gitSuffix = ".git";
        if (repo.EndsWith(gitSuffix, StringComparison.Ordinal))
        {
            repo = repo[..^gitSuffix.Length];
        }

        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            return false;
        }

        source = new GitHubSource { Owner = owner, Repo = repo };
        return true;
    }

    // ---- helpers -----------------------------------------------------------

    private static bool TryParseModId(string input, out int id)
    {
        // int.TryParse is culture-invariant + rejects sign/space; Nexus ids are
        // positive integers. A leading "+" is rejected by NumberStyles.None.
        return int.TryParse(
            input,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out id)
            && id > 0;
    }

    private static bool IsNexusHost(string host) =>
        string.Equals(host, "www.nexusmods.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "nexusmods.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitHubHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);
}
