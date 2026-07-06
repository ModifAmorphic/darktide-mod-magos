# Integrations (`Magos.Modificus.Integrations`) — reference

> External mod-source clients. Phase 1 shipped the read-only GitHub Releases
> client. Phase 4 Stage 2 adds the Nexus Mods v1 client + the OAuth/API-key
> auth machinery. Phase 4 Stage 3 adds the mod acquisition service (download +
> extract + place). Status: implemented (Phase 4 Stage 3).

## GitHub client (Phase 1)

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

### Key GitHub types

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

### Typed GitHub exceptions

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

## Nexus client + auth (Phase 4 Stage 2)

### `INexusClient`

The Nexus Mods v1 REST API client. Auth is per-request via the auth message
factory selector (which reads `NexusConfig.AuthMethod` live); the parsed rate
limits are carried on every response.

```csharp
public interface INexusClient
{
    Task<Response<ValidateInfo>> ValidateAsync(CancellationToken ct = default);              // API key
    Task<Response<OAuthUserInfo>> GetOAuthUserInfoAsync(CancellationToken ct = default);     // OAuth
    Task<Response<ModUpdate[]>> ModUpdatesAsync(string gameDomain, NexusPeriod period, CancellationToken ct = default);
    Task<Response<DownloadLink[]>> DownloadLinksAsync(string gameDomain, int modId, int fileId, CancellationToken ct = default);                                  // premium
    Task<Response<DownloadLink[]>> DownloadLinksAsync(string gameDomain, int modId, int fileId, string nxmKey, long expiresEpoch, CancellationToken ct = default); // free user
    Task<Response<ModInfo>> GetModInfoAsync(string gameDomain, int modId, CancellationToken ct = default);
    Task<Response<ModFile[]>> ListModFilesAsync(string gameDomain, int modId, CancellationToken ct = default);
}
```

- `ValidateAsync` — hits `GET /v1/users/validate.json` (API-key validate).
- `GetOAuthUserInfoAsync` — hits `GET /oauth/userinfo` on the OAuth base URL.
- `ModUpdatesAsync` — hits `GET /v1/games/{domain}/mods/updated.json?period={1d|1w|1m}`.
- `DownloadLinksAsync` (premium) — hits `GET /v1/games/{domain}/mods/{modId}/files/{fileId}/download_link.json`.
- `DownloadLinksAsync` (free user, with `nxmKey` + `expiresEpoch`) — same
  endpoint with `?key={nxmKey}&expires={epoch}`.
- `GetModInfoAsync` — hits `GET /v1/games/{domain}/mods/{modId}.json`.
- `ListModFilesAsync` — hits `GET /v1/games/{domain}/mods/{modId}/files.json`
  and unwraps the `{"files":[...]}` envelope to the array.

Every method throws `NexusApiException` on a non-2xx; `NexusRateLimitException`
on a rate-limit signal (429, or 403 with `x-rl-*-remaining: 0`);
`NexusNotAuthenticatedException` when `AuthMethod == None` or the selected
method has no usable credentials.

**401-reactive refresh + retry-once.** On a 401, the client asks the auth
factory to refresh (OAuth) or give up (API key, None). On a successful refresh
the request is retried once with the new credentials. A second 401 surfaces as
`NexusApiException` (no infinite retry loop).

### Response wrapper + rate limits

```csharp
public sealed record Response<T>(T Data, NexusRateLimits RateLimits);

public sealed record NexusRateLimits(
    int DailyLimit, int DailyRemaining, DateTimeOffset? DailyReset,
    int HourlyLimit, int HourlyRemaining, DateTimeOffset? HourlyReset);
```

`NexusRateLimits` is parsed from the `x-rl-*` response headers (mirrors NMA's
`ResponseMetadata.FromHttpHeaders`). Missing/unparseable headers yield `0` /
`null` for that field (never throws). Stage 4 (update-check) consumes them to
back off; Stage 2 just parses + logs them.

### Key Nexus types

```csharp
public sealed class ValidateInfo          // API-key validate response
{
    public long UserId { get; set; }
    public string Key { get; set; }
    public string Name { get; set; }
    public bool IsPremium { get; set; }
    public bool IsSupporter { get; set; }
    public string Email { get; set; }
    public Uri? ProfileUrl { get; set; }
}

public sealed class OAuthUserInfo          // OAuth userinfo response
{
    public string Sub { get; set; }
    public string Name { get; set; }
    public Uri? Avatar { get; set; }
    public NexusMembershipRole[] MembershipRoles { get; set; }
    public bool IsPremium { get; }         // premium or lifetimepremium role
}

public enum NexusMembershipRole { Member, Supporter, Premium, LifetimePremium }

public sealed class ModUpdate              // one entry in /mods/updated.json
{
    public long ModId { get; set; }
    public long LatestFileUpdate { get; set; }            // Unix seconds
    public DateTimeOffset LatestFileUpdateUtc { get; }
    public long LatestModActivity { get; set; }           // Unix seconds
    public DateTimeOffset LatestModActivityUtc { get; }
}

public sealed class DownloadLink           // CDN link from download_link.json
{
    public string Name { get; set; }
    public string ShortName { get; set; }
    public Uri Uri { get; set; }
}

public sealed class ModInfo { /* mod_id, name, summary, version, domain_name, ... */ }
public sealed class ModFile { /* file_id, file_name, name, version, size, ... */ }

public enum NexusPeriod { Day, Week, Month }  // period=1d|1w|1m
```

### Typed Nexus exceptions

```csharp
public class NexusApiException : Exception            // unsealed
{
    public int StatusCode { get; }
    public NexusApiException(int statusCode, string message);
}

public sealed class NexusRateLimitException : NexusApiException
{
    public NexusRateLimits? Limits { get; }
}

public sealed class NexusNotAuthenticatedException : Exception;  // AuthMethod == None
```

### Auth message factories

The auth headers are applied per-request by a factory selected live by
`NexusConfig.AuthMethod`. The selection is explicit, **no fallback**: each
method's credentials are required, and the matching inner factory surfaces a
clear error when they are missing.

```csharp
public interface INexusAuthMessageFactory
{
    ValueTask<HttpRequestMessage> CreateAsync(HttpMethod method, Uri uri, CancellationToken ct);
    ValueTask<bool> OnUnauthorizedAsync(CancellationToken ct);   // refresh (OAuth) or give up (ApiKey/None)
    ValueTask<bool> IsAuthenticatedAsync(CancellationToken ct);
}

internal sealed class ApiKeyMessageFactory : INexusAuthMessageFactory;     // apikey: <key>
internal sealed class OAuth2MessageFactory : INexusAuthMessageFactory;     // Authorization: Bearer + 401-reactive refresh
internal sealed class NoneMessageFactory : INexusAuthMessageFactory;       // no auth, IsAuthenticated=false
internal sealed class NexusAuthMessageFactorySelector : INexusAuthMessageFactory;  // picks by AuthMethod
```

The OAuth factory depends on `INexusTokenStore` (the small read-only token view
+ refresh), which is implemented by `NexusOAuthTokenStore` (the OAuth session +
refresh orchestrator, separate from `NexusAuthService` to break the DI cycle).

### Nexus auth orchestrator

```csharp
public interface INexusAuthService
{
    Task<NexusAuthResult> LoginWithOAuthAsync(CancellationToken ct = default);
    Task<NexusAuthResult> LoginWithApiKeyAsync(string apiKey, CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
    Task<NexusAuthState?> GetCurrentStateAsync(CancellationToken ct = default);
}

public sealed record NexusAuthResult       // Integrations-dialog auth action result
{
    public bool IsSuccess { get; init; }
    public string? Name { get; init; }
    public bool? IsPremium { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record NexusAuthState(NexusAuthMethod Method, string? Name, bool? IsPremium);

public sealed class NexusOAuthTokenStore : INexusTokenStore;   // OidcClient + token persistence + loopback login
```

- `LoginWithOAuthAsync` — runs the OAuth loopback flow (browser + token exchange
  + persist), flips `AuthMethod = OAuth` (clearing any API key), fetches the
  user info via the v1 client.
- `LoginWithApiKeyAsync` — speculative-write + revert-on-failure; flips
  `AuthMethod = ApiKey` (clearing any OAuth tokens).
- `SignOutAsync` — clears OAuth tokens + API key + resets to `None`.
- `GetCurrentStateAsync` — returns the verified auth state (name + premium) for
  the Integrations dialog's status line; null when `None`. Returns an unverified
  state on a network failure rather than throwing.

### OAuth loopback browser

`LoopbackBrowser` is the production `IBrowser` (from
`Duende.IdentityModel.OidcClient`). It pre-grabs an ephemeral loopback port
(exposed as `RedirectUri`), then on `InvokeAsync` binds an `HttpListener` on
that port, opens the user's default browser at OidcClient's authorize URL via
`Process.Start(UseShellExecute=true)`, awaits the callback, and returns the
authorization response. Three-minute flow timeout; on expiry it surfaces
`BrowserResultType.Timeout`. Independent of the Stage 1 `nxm://` scheme handler
(loopback redirect, not `nxm://`).

## Mod acquisition service (Phase 4 Stage 3)

The reusable download + extract + place orchestrator. Consumed by the nxm
download handler (Stage 3) and by Stage 5's per-mod update button without
retooling: both resolve `IModAcquisitionService` and feed the returned
`(containerId, versionId)` to `IProfileService.AddMod`.

```csharp
public interface IModAcquisitionService
{
    Task<(Guid ContainerId, string VersionId)> AcquireFromNexusAsync(
        string gameDomain, int modId, int fileId,
        string? nxmKey = null, long? nxmExpires = null,
        IProgress<long>? progress = null, CancellationToken ct = default);
}
```

- `AcquireFromNexusAsync`: downloads a Nexus mod file, extracts it into the
  repository via `IModImportService.Import`, and returns the
  `(containerId, versionId)`. The caller handles profile registration. The
  interface accommodates GitHub later; only the Nexus method is implemented in
  Stage 3.

The `IProgress<long>` parameter is Stage 5's per-row progress hook (Stage 3's
nxm handler passes `null`).

### Acquisition flow (`ModAcquisitionService`)

1. **Resolve download links** via `INexusClient.DownloadLinksAsync`. If both
   `nxmKey` and `nxmExpires` are present, the **free-user** overload is used
   (the per-file token from the `nxm://` URL); otherwise the **premium**
   (auth-only) overload. The auth header is applied by the client's auth
   factory. The **first** CDN link (`result.Data[0].Uri`) is used; Nexus
   returns them in priority order.
2. **Resolve metadata** for the Import: `GetModInfoAsync` for the mod name +
   `ListModFilesAsync` + find the file with matching `fileId` for the version
   string. These are 2 API calls (3 total per acquisition, within rate limits).
   **No degraded fallback:** if the metadata fetch fails, the acquisition fails
   with a clear error (a mod stored under its numeric id as a name is worse than
   a clean failure message).
3. **Download** from the CDN URI to a `.zip`-named temp file
   (`Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".zip")`)
   using a plain `HttpClient` from `IHttpClientFactory` + the 81920-byte
   buffered copy + `IProgress<long>` pattern from
   `GitHubClient.DownloadAssetAsync`. The `.zip` extension is required so
   `IModImportService` recognizes the file as an archive (detects by extension).
   The temp file is deleted once Import returns (always, success or failure; no
   partial state).
4. **Import** via `IModImportService.Import(tempPath, modName, new NexusSource
   { ModId = modId }, version)`. The import service handles find-or-create-
   container (dedup by `NexusSource.ModId`) + add-version + `IsLatest` flip.
5. **Return** `(containerId, versionId)`.

The CDN download uses a plain `HttpClient` (not the typed `INexusClient`)
because the CDN URL is an absolute path with the per-file token in the query
string (free users) or just the session auth (premium); no base address or
Nexus-specific headers are needed.

### nxm download handler (Phase 4 Stage 3)

The real `INxmModDownloadHandler` that replaces Stage 1's no-op default. Lives
in the UI assembly (`Magos.Modificus.UI.Nxm`), not Integrations, because it
coordinates UI concerns: it reads the active profile from `IProfileSession`
(UI), shows error dialogs through `IDialogService` (UI), and marshals those
dialogs to the UI thread via `Dispatcher.UIThread` (Avalonia). Placing it in
Integrations would create a dependency cycle (Integrations cannot reference the
UI assembly, which is its consumer). The reusable acquisition service is the
backend seam in Integrations; the handler is the thin UI-coordinating shell.

The handler's pre-flight checks + flow:

1. **Auth check** (live config read): `NexusConfig.AuthMethod != None`
   (required for every download; the `nxm://` key/expires is the per-file
   token for the free-user endpoint, NOT a substitute for auth). None ->
   `ShowAlertAsync("Nexus not configured", ...)`.
2. **Active-profile check**: `IProfileSession.ActiveProfileId != null`. null ->
   `ShowAlertAsync("No active profile", ...)`.
3. **Acquire + register + refresh**: `AcquireFromNexusAsync(url.Game,
   url.ModId, url.FileId, url.Key, url.Expires, ct: ct)` then
   `IProfileService.AddMod(profileId, containerId, ModVersionPolicy.Latest)`,
   then `ModListViewModel.Reload()` on the UI thread (via the handler's
   `refreshModList` callback) so the new mod appears immediately without a
   profile switch.
4. **On failure** (not cancellation): `ShowAlertAsync("Download failed",
   ex.Message)`. Cancellation propagates as `OperationCanceledException`.

`ShowAlertAsync` marshals to the UI thread via an injectable `invokeOnUi` seam
(`Func<Func<Task>, Task>`); production wires `Dispatcher.UIThread.InvokeAsync`,
tests inject a pass-through. The handler is registered AFTER `AddNxm()` so DI
"last registration wins" supersedes the no-op default (see
[DI wiring](#di-registration)).

### OAuth constants (build-time)

`NexusOAuthConstants.ClientId` = `"magos-modificus"` (a build-time const, NOT
config and NOT an env var). `Scope` = `"openid profile email"`. Application
headers: `Application-Name: Magos-Modificus`, `Application-Version: <asm>`,
`Protocol-Version: 1.0.0`, `User-Agent: Magos-Modificus/<ver>`.

## DI registration

```csharp
public static IServiceCollection AddIntegrations(this IServiceCollection services);
```

Registers (alongside the existing GitHub typed HTTP client):

- `INexusClient` → `NexusClient` as a **typed HTTP client** via
  `AddHttpClient<INexusClient, NexusClient>`, configured from
  `MagosConfig.Integrations.Nexus.BaseUrl`.
- The auth message factories (`ApiKeyMessageFactory`, `OAuth2MessageFactory`,
  `NoneMessageFactory`) + the `INexusAuthMessageFactory` selector
  (`NexusAuthMessageFactorySelector`).
- `IBrowser` → `LoopbackBrowser` (the production loopback impl).
- `NexusOAuthTokenStore` (singleton; the OAuth token + login orchestrator),
  exposed both directly and as `INexusTokenStore`.
- `NexusAuthService` (singleton; the Integrations-dialog auth orchestrator),
  exposed both directly and as `INexusAuthService`.
- `IModAcquisitionService` -> `ModAcquisitionService` (singleton; the download +
  extract + place orchestrator over `INexusClient` + `IModImportService` + a
  plain `HttpClient` from the factory for the CDN download).

The OAuth factory's token store + the service's token store are the SAME
`NexusOAuthTokenStore` instance (matches production wiring). The store depends
only on config + the browser; the service depends on the store + the v1 client;
the client depends on the auth factory selector; the selector depends on the
inner factories; the OAuth factory depends on the small `INexusTokenStore`
view. No construction-time cycle.

`AddIntegrations()` resolves `MagosConfig` + `ILogger<>` from the container.

## Dependencies

- **Magos libraries:** `config` (`MagosConfig.Integrations.Nexus` + `.GitHub`),
  `general` (`IConfigLoader`), `mods` (`IModImportService`, `NexusSource` for
  the acquisition service).
- **NuGet:** `Microsoft.Extensions.Http` (`AddHttpClient<TClient,TImpl>` +
  `IHttpClientFactory`), `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`, `Duende.IdentityModel.OidcClient`
  7.1.0 (Apache-2.0, the FOSS-licensed OIDC client; not the dual-licensed
  IdentityServer product).
- **BCL otherwise:** `System.Net.HttpListener` (the loopback listener),
  `System.Diagnostics.Process` (the browser launcher), `System.Text.Json`
  (response parsing) — all in-box on net10.0.

## Testing

`Magos.Modificus.Integrations.Tests` covers:

- **`GitHubClient`** against a fake `HttpMessageHandler` (`StubHttpMessageHandler`)
  — list/latest/download happy paths, `404`→empty/`null`, non-2xx →
  `GitHubApiException`, rate-limit → `GitHubRateLimitException`.
- **`NexusClient`** against the same fake handler — v1 endpoint paths, response
  parsing, rate-limit header parsing, error mapping, the auth gate, and the
  401-retry-after-refresh path.
- **Auth message factories** — `ApiKeyMessageFactory` adds the `apikey` header;
  `OAuth2MessageFactory` adds `Authorization: Bearer` + refreshes on 401 (via a
  fake `INexusTokenStore`); the selector picks the right one based on the live
  `AuthMethod`; concurrent 401s coalesce into one refresh.
- **`NexusAuthService`** + **`NexusOAuthTokenStore`** — API-key validate (success
  + revert-on-failure), OAuth login (via the backchannel seam against a stub
  discovery + token endpoint), token refresh (persist), sign-out, switching
  methods clears the other method's credentials.
- **`NexusConfig` JSON round-trip** — defaults, OAuth tokens persist + reload.
- **`LoopbackBrowser`** + **`HttpListenerLoopbackListener`** — a real listener
  binds an ephemeral loopback port; an `HttpClient` simulates the browser
  redirect; the listener returns the callback query string; the friendly HTML
  response is served.
- **`AddIntegrations`** DI wiring (the existing GitHub suite + the new Nexus
  client + auth factory resolution + the acquisition service).
- **`ModAcquisitionService`** against a fake `INexusClient` + a fake
  `IModImportService` + a stub HTTP handler for the CDN download: premium vs
  free-user overload selection, first-CDN-link use, metadata resolution (name +
  version), the no-degraded-fallback error policy (metadata failure + missing
  file throw, no partial import), download failure, import-failure temp cleanup,
  progress reporting, cancellation.

The internal `NexusClient`, `NexusAuthService`, `NexusOAuthTokenStore`,
`LoopbackBrowser`, `HttpListenerLoopbackListener`, `ModAcquisitionService`, and
the auth factories are visible to tests via `InternalsVisibleTo`. The
`NxmModDownloadHandler` (UI) is tested in `Magos.Modificus.UI.Tests` (visible via
the UI project's `InternalsVisibleTo`).

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) — the
  [Mod sources / integrations](../../architecture/MAGOS-MODIFICUS.md#mod-sources--integrations)
  section + the
  [Nexus authentication](../../architecture/MAGOS-MODIFICUS.md#nexus-authentication-phase-4-stage-2)
  subsection.
- [config](config.md) — the `GitHubConfig` + `NexusConfig` schemas.
- [nxm](nxm.md) — the `nxm://` scheme handler (Stage 1), including the Stage 2
  seam cleanup. Stage 3's `NxmModDownloadHandler` replaces the no-op
  `INxmModDownloadHandler` via DI last-registration-wins.
