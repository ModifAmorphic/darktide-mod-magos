# Integrations (`Magos.Modificus.Integrations`) — reference

> External mod-source clients. Phase 1 ships a read-only GitHub Releases client
> (the minimum surface needed for GitHub-hosted mod sources and, later, the DMF
> new-profile prompt). Nexus Mods is Phase 4. Status: implemented (Phase 1).

## Public surface

### `IGitHubClient`

A read-only GitHub Releases client over the GitHub REST API.

```csharp
public interface IGitHubClient
{
    IReadOnlyList<GitHubRelease> ListReleases(GitHubRepo repo, CancellationToken ct = default);
    GitHubRelease? GetLatestRelease(GitHubRepo repo, CancellationToken ct = default);
    Task DownloadAssetAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        IProgress<long>? progress = null,
        CancellationToken ct = default);
}
```

- `ListReleases(repo, ct)` — a repository's published releases, newest first (per
  the GitHub API). A `404` (unknown repo) yields an **empty list**, not an
  exception. Returns up to GitHub's default page size (~30); pagination is a
  later phase. Throws `GitHubApiException` on any other non-2xx;
  `GitHubRateLimitException` when the rate limit is exhausted.
- `GetLatestRelease(repo, ct)` — the latest published release, or `null` if the
  repo has no releases or is unknown (both surface as `404`). Same exception
  behavior as `ListReleases`.
- `DownloadAssetAsync(asset, destinationPath, progress, ct)` — downloads the
  asset's bytes to `destinationPath`, reporting cumulative byte count to
  `progress` when provided. The destination's parent directory is created if
  missing. Throws `GitHubApiException` on a non-2xx (e.g. a stale asset URL);
  `GitHubRateLimitException` when rate-limited.

`ListReleases` / `GetLatestRelease` are synchronous (fully-materialized results —
the simple surface Phase 1 callers want); `DownloadAssetAsync` is async (it's a
file download).

### Key types

```csharp
public sealed record GitHubRepo(string Owner, string Name);
public sealed record GitHubRelease(
    string TagName,
    string Name,
    DateTimeOffset PublishedAt,
    IReadOnlyList<GitHubReleaseAsset> Assets);
public sealed record GitHubReleaseAsset(string Name, long Size, Uri BrowserDownloadUrl);
```

- `GitHubRepo` — repo identity. e.g. `new GitHubRepo("Darktide-Mod-Framework", "DMF")`.
- `GitHubRelease` — a published release: tag, display name, publish time, assets.
- `GitHubReleaseAsset` — a downloadable asset. `BrowserDownloadUrl` is the
  absolute URL served (and streamable) by GitHub's CDN.

### Typed exceptions

```csharp
public class GitHubApiException : Exception            // unsealed
{
    public int StatusCode { get; }
    public GitHubApiException(int statusCode, string message);
}

public sealed class GitHubRateLimitException : GitHubApiException
{
    public DateTimeOffset? ResetAt { get; }   // from X-RateLimit-Reset, or null
}
```

- `GitHubApiException` — a non-success response (other than the `404` cases the
  client maps to `null`/empty). Carries the HTTP status + the API's `message`
  field. Unsealed so callers can catch the base type to handle every GitHub API
  failure uniformly.
- `GitHubRateLimitException` — the rate limit is exhausted, detected via a
  `403`/`429` carrying `X-RateLimit-Remaining: 0`. Carries the reset time
  (`X-RateLimit-Reset`) when GitHub reports it, so callers can advise when to
  retry. `StatusCode` reflects the actual response status (403 or 429).

## DI registration

```csharp
public static IServiceCollection AddIntegrations(this IServiceCollection services);
```

Registers `IGitHubClient` → `GitHubClient` as a **typed HTTP client** via
`AddHttpClient<IGitHubClient, GitHubClient>`, configured from
`MagosConfig.Integrations.GitHub` (resolved from the container):

- **Base URL** — normalized: whitespace + trailing slashes trimmed, then one
  trailing slash re-appended so relative request URIs resolve against
  `HttpClient.BaseAddress` predictably (the classic `BaseAddress` footgun). A
  blank value falls back to the public GitHub API (`https://api.github.com/`).
- **Headers applied to every request:** `User-Agent: Magos-Modificus` (required
  by GitHub) and `Accept: application/vnd.github+json`.
- **Auth:** when `GitHubConfig.Token` is set, sent as
  `Authorization: Bearer <token>` (raises the rate limit / unlocks private
  repos); anonymous access is used otherwise.

Resolves `MagosConfig` + `ILogger<GitHubClient>` from the container.

## Dependencies

- **Magos libraries:** `config` (`MagosConfig.Integrations.GitHub`).
- **NuGet:** `Microsoft.Extensions.Http` (`AddHttpClient<TClient,TImpl>` +
  `IHttpClientFactory`), `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Magos.Modificus.Integrations.Tests` covers `GitHubClient` against a fake
`HttpMessageHandler` (`StubHttpMessageHandler`) — list/latest/download happy
paths, `404`→empty/`null`, non-2xx → `GitHubApiException`, and the
`403`/`429`+`X-RateLimit-Remaining: 0` → `GitHubRateLimitException` mapping —
plus the `AddIntegrations` DI wiring (base-URL normalization, headers, optional
Bearer token). The internal `GitHubClient` is visible to tests via
`InternalsVisibleTo`.

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) — the
  [Mod sources / integrations](../../architecture/MAGOS-MODIFICUS.md#mod-sources--integrations)
  section.
- [config](config.md) — the `GitHubConfig` schema (base URL + optional token).
