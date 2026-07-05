# Nxm (`Magos.Modificus.Nxm`): reference

> The `nxm://` scheme-handler plumbing: URL parsing, length-prefixed IPC framing
> between the tiny handler exe and the running app, the Magos-side accept loop +
> router, pluggable handler seams (no-op in Stage 1), the OS scheme-handler
> registration service, and the testable relay helper the handler exe calls.
> Stage 1 of Phase 4. Status: implemented (Phase 4 Stage 1). Stage 2 (OAuth)
> and Stage 3 (mod download / acquisition) plug real handlers into the seams
> shipped here.

## Public surface

### URL types + parser

Pure, no I/O. Mirrors the `ModSourceParser` style: a static `TryParse` that
returns `false` on malformed input and never throws.

```csharp
public abstract record NxmUrl(string Raw);

public sealed record NxmModDownloadUrl(
    string Raw, string Game, int ModId, int FileId,
    string? Key, long? Expires, long? UserId) : NxmUrl(Raw);

public sealed record NxmOAuthCallbackUrl(
    string Raw, string Code, string State) : NxmUrl(Raw);

public sealed record NxmCollectionUrl(
    string Raw, string Game, string CollectionId, int Revision) : NxmUrl(Raw);

public static class NxmUrlParser
{
    public static bool TryParse(string raw, [MaybeNullWhen(false)] out NxmUrl url);
}
```

- `NxmModDownloadUrl`: `nxm://<game>/mods/<modId>/files/<fileId>` with optional
  `key`, `expires` (epoch seconds), `user_id` query parameters. The "Mod manager
  download" button on a Nexus Mods file page produces one. `modId`/`fileId` must
  be positive integers; empty/non-numeric query values parse to `null` rather
  than rejecting the whole URL.
- `NxmOAuthCallbackUrl`: `nxm://oauth/callback?code=<code>&state=<state>`. Both
  `code` and `state` are required and must be non-empty.
- `NxmCollectionUrl`: `nxm://<game>/collections/<id>/revisions/<rev>`. Parsed so
  the router can log "unsupported in v1" rather than "unknown URL".
- The scheme is matched case-insensitively. Anything that does not match one of
  the three shapes fails to parse.

### IPC framing

Length-prefixed UTF-8 framing for the one-message-per-connection IPC protocol.
AOT-safe: only raw byte / UTF-8 IO.

```csharp
public static class NxmIpcFraming
{
    public const int MaxPayloadBytes = 8 * 1024;   // 8 KiB cap

    public static Task WriteUrlAsync(Stream stream, string url, CancellationToken ct = default);
    public static Task<string?> ReadUrlAsync(Stream stream, CancellationToken ct = default);
}

public sealed class NxmIpcFramingException : Exception;  // malformed frame
```

Frame layout: `[4 bytes: little-endian uint32 payload length N][N bytes: UTF-8
URL string]`. `WriteUrlAsync` throws `NxmIpcFramingException` if the UTF-8
encoding exceeds the 8 KiB cap. `ReadUrlAsync` returns the decoded URL, or
`null` for a clean close before any bytes (no message), and throws
`NxmIpcFramingException` on a malformed length prefix or a mid-frame close.

### IPC server

```csharp
public sealed class NxmIpcServer : IDisposable, IAsyncDisposable
{
    public const string DefaultPipeName = "Magos.Nxm";

    public NxmIpcServer(INxmRouter router, ILogger<NxmIpcServer> logger, string? pipeName = null);
    public void Bind();                              // single-instance claim
    public Task RunAsync(CancellationToken ct);      // accept loop until cancelled
}

public sealed class NxmSingleInstanceException : Exception  // from Bind()
{
    public string PipeName { get; }
}
```

- **Fixed pipe name `Magos.Nxm`.** No per-user suffix (single-user gaming-PC
  context).
- **Single-instance enforcement is the pipe bind.** `Bind` creates the
  `NamedPipeServerStream` with `maxNumberOfServerInstances = 1`; if another Magos
  owns the name, the ctor throws `IOException`, surfaced as
  `NxmSingleInstanceException`. The composition root catches this and exits
  before the main window shows.
- The accept loop `WaitForConnectionAsync` → `ReadUrlAsync` →
  `router.RouteAsync` → `Disconnect` (keeps the server instance alive for the
  next client on the same pipe name, so the single-instance claim is continuous).
  Per-connection exceptions are logged and swallowed so one bad client cannot
  kill the server.

### Router + pluggable handlers

```csharp
public interface INxmRouter
{
    Task RouteAsync(string rawUrl, CancellationToken ct = default);
}

public interface INxmModDownloadHandler
{
    Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default);
}

public interface INxmOAuthCallbackHandler
{
    Task HandleAsync(NxmOAuthCallbackUrl url, CancellationToken ct = default);
}
```

The default `NxmRouter` (internal) parses the raw URL via `NxmUrlParser`,
dispatches mod-download URLs to `INxmModDownloadHandler`, OAuth-callback URLs to
`INxmOAuthCallbackHandler`, logs collection URLs as "unsupported in v1", and logs
unparseable URLs as a warning. Handler exceptions are caught at the router
boundary.

Stage 1 ships **no-op default implementations** of both handlers (internal): they
log the parsed URL at Information and return. Stage 2 replaces the OAuth handler;
Stage 3 replaces the mod-download handler.

### Handler-exe relay helper

The testable core the OS-registered handler exe calls. Every external dependency
is an injectable seam so the relay is fully testable without real processes or
pipes.

```csharp
public static class NxmHandlerRelay
{
    public static readonly TimeSpan DefaultConnectTimeout;  // 500ms
    public static readonly TimeSpan DefaultRetryInterval;   // 250ms
    public static readonly TimeSpan DefaultRetryTimeout;    // 30s

    public static Task<int> RunAsync(
        string[] args,
        Func<string, CancellationToken, Task<(bool connected, Stream? stream)>>? pipeConnect = null,
        Func<ProcessStartInfo>? launchMagosFactory = null,
        TimeSpan? retryInterval = null,
        TimeSpan? retryTimeout = null,
        CancellationToken ct = default);
}
```

Returns the process exit code (0 on success, non-zero on no-arg /
unrecoverable failure). Behavior:

1. Extract the URL (the first non-flag arg). If none, log to stderr + non-zero.
2. Try `pipeConnect`: on success, write the framed URL, close, return 0.
3. On refused connect: build a `ProcessStartInfo` via `launchMagosFactory`, start
   Magos detached (no args), retry `pipeConnect` every `retryInterval` until
   success or `retryTimeout`, then deliver + return 0.
4. On timeout: log "Magos did not start within Ns" to stderr, return non-zero.

**Cold start is owned by the handler, not Magos.** Magos has no `--nxm` arg and
no cold-start branch; the handler owns the entire orchestration.

### OS scheme-handler registration service

```csharp
public interface INxmHandlerRegistrar
{
    bool IsRegistered();
    void Register();
    void Unregister();
}
```

A single interface with two platform implementations, selected by runtime OS at
DI registration:

- **`WindowsNxmHandlerRegistrar`** (`[SupportedOSPlatform("windows")]`): writes
  `HKCU\Software\Classes\nxm` (per-user, no elevation) with
  `(Default) = "URL:Nexus Mods Link"`, `URL Protocol = ""`, and
  `HKCU\Software\Classes\nxm\shell\open\command` with
  `(Default) = "<handler-exe>" "%1"`. `Unregister` deletes the key tree.
- **`LinuxNxmHandlerRegistrar`** (`[SupportedOSPlatform("linux")]`): writes
  `~/.local/share/applications/magos-nxm-handler.desktop` (the source of truth)
  + best-effort `xdg-mime default magos-nxm-handler.desktop x-scheme-handler/nxm`
  (logged, not thrown, if `xdg-mime` is absent).

The handler-exe path is derived from `AppContext.BaseDirectory` + the fixed
handler assembly name via `NxmHandlerPaths.GetHandlerExePath()`.

Stage 1 ships the **service** only. The user-facing registration behavior (auto
on first run vs. Settings toggle vs. manual) is deferred to Stage 6.

## DI registration

```csharp
public static IServiceCollection AddNxm(this IServiceCollection services);
```

Registers `INxmRouter` → `NxmRouter`, `NxmIpcServer`, and the no-op handler
defaults as singletons. The platform `INxmHandlerRegistrar` is selected via
`OperatingSystem.IsWindows()` / `OperatingSystem.IsLinux()` (the canonical
CA1416 guards), each behind a `[SupportedOSPlatform]`-annotated factory helper.

**Handler override convention (last registration wins).** The no-op defaults are
registered with plain `AddSingleton` (not `TryAdd`). Stage 2 / 3 register real
implementations AFTER `AddNxm()` via
`services.AddSingleton<INxmOAuthCallbackHandler, ...>()` (or the mod-download
equivalent); MS DI resolves the LAST registration, so the real handler
supersedes the no-op.

The composition root binds + starts the IPC server after building the provider:
`MagosComposition.Build()` calls `AddNxm()` during registration, then
`StartNxmServer(provider, logger)` which calls `NxmIpcServer.Bind()` (the
single-instance claim, throws `NxmSingleInstanceException` on violation) and
starts `RunAsync` on a background task. `App.OnFrameworkInitializationCompleted`
catches `NxmSingleInstanceException` around `Build()` and shuts down before the
main window shows.

## Dependencies

- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.
- **None other.** `System.IO.Pipes` is in-box for net10.0. `Microsoft.Win32.Registry`
  is in-box (Windows-only, gated by `[SupportedOSPlatform("windows")]`; no NuGet
  reference, mirroring `SteamRegistryReader`). The library is marked
  `<IsAotCompatible>true</IsAotCompatible>` so the handler exe can publish native
  AOT against it.

## Testing

`Magos.Modificus.Nxm.Tests` covers:

- **`NxmUrlParser`**: valid mod-download / OAuth-callback / collection URLs +
  malformed rejections (wrong scheme, missing/non-numeric/non-positive ids,
  OAuth missing code/state, garbage).
- **`NxmIpcFraming`**: write/read round-trip over a real named-pipe pair, the
  8 KiB cap on write + read, mid-frame close, clean-close-at-boundary `null`.
- **`NxmIpcServer`**: client message routed to the router; a garbage frame does
  not kill the server (next connection still works); a throwing router does not
  kill the server; a second `Bind` on the same pipe throws
  `NxmSingleInstanceException`.
- **`NxmRouter`**: mod-download routes to the mod handler with parsed fields;
  OAuth callback routes to the OAuth handler; collection + unparseable URLs route
  to neither; a throwing handler does not propagate.
- **`NxmHandlerRelay`**: hot-path (connect first try, no launch), cold-start
  (refuse, launch, retry, deliver), cold-start timeout, no-URL-arg, multi-arg.
- **`LinuxNxmHandlerRegistrar`** (Linux-gated): Register writes the `.desktop`
  file with the expected content; IsRegistered reflects the faked `xdg-mime`;
  Unregister removes the file; a missing `xdg-mime` is tolerated.

The assembly is annotated
`[assembly: CollectionBehavior(DisableTestParallelization = true)]`: real named
pipes are an OS-level shared resource, and serializing the suite (well under a
second) is worth the determinism.

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

The handler exe publishes native AOT: `dotnet publish magos-modificus/nxm-handler
-c Release` produces a stripped native binary.

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md).
- [integrations](integrations.md): the GitHub Releases client (Phase 1); the
  Nexus client arrives in a later Phase 4 stage and plugs the mod-download
  handler.
