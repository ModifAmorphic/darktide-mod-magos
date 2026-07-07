namespace Modificus.Curator.Integrations;

/// <summary>
/// A GitHub repository identity -- the owner + name that identify a repo on the
/// REST API. For example, the Darktide-Mod-Framework repo is
/// <c>new GitHubRepo("Darktide-Mod-Framework", "DMF")</c>.
/// </summary>
public sealed record GitHubRepo(string Owner, string Name);

/// <summary>
/// A published GitHub release: its tag, display name, publish time, and the
/// downloadable assets attached to it. Immutable.
/// </summary>
public sealed record GitHubRelease(
    string TagName,
    string Name,
    DateTimeOffset PublishedAt,
    IReadOnlyList<GitHubReleaseAsset> Assets);

/// <summary>
/// A single downloadable asset attached to a release. <see cref="BrowserDownloadUrl"/>
/// is the absolute URL served (and streamable) by GitHub's CDN.
/// </summary>
public sealed record GitHubReleaseAsset(string Name, long Size, Uri BrowserDownloadUrl);
