# Magos Modificus — architecture

**Magos Modificus** is the user-facing mod manager app for darktide-mod-magos —
the second of the project's two components, sitting on top of the Enginseer
runtime. It owns everything user-facing: profile management, mod staging, load
order, dependency resolution, mod-source integrations (GitHub Releases, Nexus
Mods, Steam), and the "Launch Darktide" button that invokes the Enginseer
launcher. Enginseer does the injection + mod loading; Magos Modificus owns the
management experience around it.

> **Status: Phases 0–3 complete.** The foundation (.NET 10 + Avalonia 12 layout,
> DI composition, structured logging, global config schema/loader) plus the
> backend libraries are built: Profiles, Steam, Integrations, Enginseer-client
> (Phase 1) + Mods (Phase 2). The Phase 3 UI is in place across all four tracks:
> Track A (app shell + profile management), Track D (global Preferences + i18n),
> Track B (the mod-list UI + local import), and Track C (Launch wiring + Settings
> window + discovery escape-hatch). The app is user-usable: create profiles,
> import mods, manage the mod list, configure Settings, and launch modded
> Darktide. The **Launcher** is a stub (Phase 5). It builds on the
> [Enginseer runtime](https://github.com/ModifAmorphic/darktide-enginseer)
> (separate repo).

## In scope for this document

- The component's role, technology choices, and project layout.
- The domain-library breakdown and each library's responsibilities.
- The Enginseer contract Magos consumes (the stable surface it builds against).
- The profiles model, mod storage, mod sources, and the launch flow on Windows and Linux.
- The v1 scope cut.

## Out of scope (handled elsewhere)

- The Enginseer runtime internals: see the
  [darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer) repo.
- The mod loader ↔ DMF integration: darktide-enginseer.
- The load-order file contract (`mods.lst`): Magos authors `mods.lst` and
  Enginseer consumes it; the contract is specified in darktide-enginseer.

## Technology

- **C# / .NET 10 (LTS)** + **Avalonia 12** for a native cross-platform UI.
- **DI** via `Microsoft.Extensions.DependencyInjection`; **logging** via
  `Microsoft.Extensions.Logging` (structured). Mandated across all components.
- SOLID / DRY; libraries namespaced by domain.

## Project layout

All Magos Modificus projects live under `magos-modificus/`, each component in
its own subfolder:

```
magos-modificus/
  ui/                     the Avalonia app (UI only — no direct data access)
  general/                cross-cutting infra (DI, logging, config, primitives)
  config/                 the global config schema + defaults
  profiles/               Profiles library — profile data, staging, mods.lst
  mods/                   Mods library — unified mod repository (IModRepository) + version-policy + source models
  steam/                  Steam library — Steam/Darktide/Proton discovery + IsGameRunning
  integrations/           Integrations library — GitHub Releases client (Nexus = Phase 4)
  enginseer-client/       Enginseer-client library — the launch façade
  launcher/ (optional)    slim profile launcher — launches a profile without
                          the UI (entry point for Steam non-steam shortcuts); Phase 5
  nxm/                    Nxm library: nxm:// scheme-handler plumbing (URL parser, IPC
                          server, single-instance guard, router + handler seams, OS
                          registrar, relay helper); Phase 4 Stage 1
  nxm-handler/            the OS-registered nxm:// scheme handler (native-AOT console
                          exe; relays the raw URL to running Magos, or cold-starts it)
  tests/                  xUnit test projects per library
```

The UI **never** touches files, directories, APIs, or any data directly —
neither reads nor writes. Every data operation goes through a backend library.
The UI is purely presentation + orchestration of library calls.

## Libraries (by domain)

Each library exposes interfaces namespaced by domain, used by both the UI and
other libraries. SOLID: libraries accept interfaces or primitives, not concrete
UI models.

| Library | Owns |
| --- | --- |
| **Enginseer** | All interaction with the Enginseer runtime. v1 façade only: assemble launcher args, invoke, track process exit. (Live-control — status / hot-reload / live enable-disable — is a future Enginseer contract expansion; out of v1.) |
| **Profiles + Settings** | Profile data, files, directories; global/system settings (logging, profile base folder, mod repository); resolves each profile mod's version policy to a repository version folder; materializes the profile mod root + writes `mods.lst` at launch. |
| **Integrations** | External-service calls: Nexus Mods (primary user-mod source), GitHub Releases, local install. Nexus API key / OIDC, version checks, downloads / updates. |
| **Steam** | Steam operations outside Enginseer: locate Steam (`libraryfolders.vdf`), Darktide install + compatdata, Proton version; add / remove non-steam shortcuts; detect whether the game is running. Owns the Linux discovery + escape hatch (see [Launch](#launch)). |
| **General** | Cross-cutting infra: DI composition, structured logging, configuration, shared primitives. |

## Composition & startup

The composition root is `ui/MagosComposition.cs` — a static `Build()` that
constructs and returns the application `IServiceProvider`. The UI **never**
touches files, directories, or APIs directly; every data operation flows
through a registered library interface. The UI registers only its own surface
(main window + view model) — no data access.

`MagosComposition.Build()` runs this sequence, in order:

1. **Config loader**: `new ConfigLoader()`, registered as the live-read
   `IConfigLoader` singleton (one shared instance). The startup snapshot is a
   one-off `Load()` to build the logger; every consumer thereafter re-reads the
   current disk state per op via `IConfigLoader.Load()` (config is tiny; a
   startup cache would only create staleness for the Settings window + mod-repo
   relocation, which write config at runtime; #31).
2. **Build the logger**: `LoggingBootstrap.CreateLoggerFactory(config)`
   (Serilog console + file, level-honored, truncated on startup). Both config
   and the logger are constructed **outside** DI because DI itself needs them.
3. **Compose services**: `new ServiceCollection()`, then the `Add<Library>()`
   extensions in their real order:
   - `AddSingleton<IConfigLoader>(loader)`: pre-registered before `AddGeneral`
     so the same live-read instance is shared.
   - `AddGeneral(loggerFactory)`: registers the logger factory, `AddLogging()`,
     and the runtime app-state store. `MagosConfig` is intentionally NOT
     registered as a singleton (config is read live via `IConfigLoader`).
   - `AddMods()`: the unified mod repository + import service (called explicitly here and
     idempotently again inside `AddProfiles()`, so the store is discoverable at
     the root and `IProfileService` always resolves its staging dependency).
   - `AddProfiles()`: profile service + the `SymlinkCreator` staging seam.
    - `AddIntegrations()`: the typed GitHub HTTP client + the typed Nexus v1
      HTTP client (Phase 4 Stage 2) + the Nexus auth service + the OAuth token
      store + the loopback `IBrowser`.
   - `AddSteam()`: Steam discovery + the platform process-lookup seam.
   - `AddEnginseerClient()`: the launch façade + the process-launcher seam.
   - `AddLauncher()`: the slim profile launcher stub (Phase 5).
   - `AddSingleton<MainWindow>()` + `AddSingleton<MainViewModel>()`: the UI
     surface.
4. **Build**: `BuildServiceProvider()`.
5. **Startup prune**: `ModCleanup.PruneUnreferenced` runs once (best-effort,
   logged + swallowed on failure) to drop repository versions no profile
   references + empty containers.
6. **Startup discovery**: `ISteamService.Discover()` runs once (best-effort +
   non-blocking, logged + swallowed on failure) to validate + heal + persist the
   discovery overrides up front, so the Settings window shows resolved paths
   rather than blanks. Missing fields block launch (re-checked at launch), not
   app startup.

**The DI contract:** each library exposes one `Add<Library>()` extension and
accepts only interfaces or primitives (never concrete UI models). Supporting
services and injectable seams are registered with `TryAdd` — `SteamDiscoveryOptions`,
`ISteamRegistryReader`, `IProcessLookup` (Steam), `SymlinkCreator` (Profiles),
`IProcessLauncher` (Enginseer-client), `IModRepository` (Mods) — so tests
and hosts can pre-register overrides (e.g. the Steam fixture's fakes, or a
throwing `SymlinkCreator` to exercise the failure path) and have them survive the
`Add<Library>()` chain. `TryAdd` is specifically load-bearing for `AddProfiles()`,
which calls `AddMods()` unconditionally: a plain `AddSingleton` there would
clobber a pre-registered mock.

**Per-profile vs global:** global, system-level settings live in `MagosConfig`
(one config file under the OS local-app-data dir); per-profile settings (mods,
load order, per-mod policies) live with the profile, not in the global config.

Per-library public surfaces — interfaces, key types, exact DI registrations —
are documented under [Reference — Magos Modificus](../reference/magos-modificus/).

## The Enginseer contract Magos consumes

Stable surface (Enginseer is built; this is the boundary Magos builds against):

- **Invocation:** subprocess `magos_launcher.exe`, precedence **flag > env > default**.
- **Flags:** `--game-binary <path>` (required) · `--mod-path <path>` (the mod
  root) · `--log-file <path>` · `--log-level <level>` · `--steam-app-id <id>`
  (default `1361210`).
- **The mod root (`--mod-path`) is what Magos owns and writes:** DMF (when
  installed) + user mods + `mods.lst` (the load-order file).
- **`mods.lst`:** one mod folder name per line, in load order. **Regenerated by
  Magos on every launch** from the profile's mod list (a projection, not a
  source of truth). Enable/disable is by omission — disabled mods aren't
  listed. DMF is a normal first entry (Magos writes it first via dependency
  resolution). Missing/empty file → Enginseer loads nothing (graceful).
- **Two roots (Magos must respect):** the mod root is Magos-owned; the loader
  root (`<dll-dir>/mod_loader/`) is Enginseer-owned and untouchable.
- **What Enginseer does NOT do (so Magos must):** load-order computation,
  dependency resolution, profile/staging management, platform plumbing on
  Linux.
- **Live control:** none in v1 — launch is fire-and-forget; the launcher exits
  after resume. Status / hot-reload / live enable-disable are a tracked future
  Enginseer contract expansion (GitHub issue), not v1.

See the
[Enginseer runtime docs](https://github.com/ModifAmorphic/darktide-enginseer)
for the full contract (env-var table, logging, the hook-ready handshake).

## Profiles

- A profile owns its own mods, mod settings, and load order. All settings
  except global are per-profile.
- The profile's mod root is what Magos passes to Enginseer as `--mod-path`;
  Magos writes `mods.lst` into it on each launch.
- **DMF on profile creation:** the new-profile flow offers "add latest DMF?"
  (default yes). If accepted, DMF is added to the profile's mod list like any
  mod (sourced per the open DMF-sourcing decision — see
  [Mod sources / integrations](#mod-sources--integrations); it is **not**
  settled as GitHub Releases). DMF is a normal mod with exactly two
  exceptions: (1) the creation-time prompt; (2) DMF is never auto-placed by an
  Enginseer-side rule — Magos writes it first in `mods.lst` because dependency
  resolution puts it there. Beyond those, DMF is fully user-controllable (a
  user could remove / disable / reorder it and break dependent mods —
  sharp-tools philosophy; Magos does not hard-lock it).
- Mods are stored **once, in a unified repository** keyed by `(source, identity)`
  per UUID container. Profiles reference a mod by `(containerId, policy)` and
  store no mod files of their own. See [Mod repository](#mod-repository).

## Mod repository

Mods are stored once, in a unified repository keyed by **one UUID container per
`(source-type, identity)`**. A container holds zero or more **opaque-ID version
subfolders**, indexed by a per-container `container.json` manifest. Profiles
reference a mod by `(containerId, policy)` and resolve the version folder at
stage time, so a profile never stores mod files of its own. This keeps one copy
of each mod + version, organized so they don't collide and can associate to
profiles.

Container identity by source: **Nexus** by `ModId`, **GitHub** by
`Owner`/`Repo`, **Untracked** (local) by `Name`. Different source-types are
separate namespaces, so an untracked "WeaponTweaks" and a Nexus "WeaponTweaks"
are distinct containers, and a Nexus mod and a GitHub mod never collide or share.
The container directory is **UUID-named (opaque)**, and its path is **derived**
(`<ModsFolder>/<containerUUID>/`), never stored absolute.

Each version is a subfolder with an **opaque unique ID**; the raw version tag
lives only in the manifest (for display + pin resolution), never as a folder
name. **`isLatest` is a flag on one version entry**, not a duplicate folder:
moving latest is a one-field manifest edit, and profiles resolve dynamically at
stage time (no rescanning profiles, no disk duplication).

Each profile entry carries a **version policy**: **pinned** (frozen to a specific
imported version) or **latest (auto-update)** (tracks the newest release). At
stage time:

| Profile policy | Resolved version |
| --- | --- |
| latest | the container's `isLatest` version folder |
| pinned `<versionId>` | the version whose `Folder` matches the pin's `VersionId` |
| pinned `<orphan id>` | skip + warn (no version matches) |

The pin is a **foreign key** (`PinnedPolicy.VersionId`) to a `ModVersion.Folder`
(the opaque version-folder ID the repository mints), not a version string. The
repository is the single source of truth for version details: the readable
release tag lives only on `ModVersion.VersionString` (for display). A pin
references a version row, so it always resolves to a real imported version or is
an orphan (skip + warn); a "phantom" pin to a version that was never imported
cannot be expressed (the policy editor offers only the container's actual
versions, and `SetModPolicy` rejects an unknown id). GitHub release tags and
Nexus file versions are arbitrary strings (not SemVer); there is no version
ordering at this layer, and "newer" is decided later (Phase 4) by fetching the
latest release tag and checking string inequality.

Each container also carries a **source** (Untracked / Nexus / GitHub) so a
pinned version is legible ("WeaponTweaks *(GitHub owner/repo)* pinned to
`1.2`"). The UI collects URLs; the model stores the canonical identity (Nexus
mod id; GitHub owner/repo) via a pure parser. Local / untracked mods use the
`UntrackedSource` source (dedup by name).

**Import flow:** adding a mod to the active profile goes through
`IModImportService` (the UI never touches the filesystem). The import service
validates the source structure (the source must contain exactly one base
directory with a matching `<base>.mod` descriptor inside it), then resolves (or
creates) the container for the source + extracts a `.zip` / copies a folder into
the repository-managed opaque version folder via
`IModRepository.AddVersion`. The validated base folder is **preserved** under
`<versionFolder>/<base>/` (the folder import copies the folder itself, not its
contents; the zip is validated to have a single top-level folder before
extraction). Container dedup: Untracked by name, Nexus by mod id, GitHub by
owner/repo. Version dedup: re-importing the same tag reuses its folder
(refreshed); a new tag creates a new version + flips `isLatest`. The service
returns `(containerId, versionString)`; the caller then adds the profile
reference via `IProfileService.AddMod`. Remote acquisition (Nexus / GitHub API
clients, auto-fetch) stays in Phase 4.

**Base-name collision hard-block:** two mods with the same base folder name
can't coexist in one profile (the mod loader can't tell them apart). Before
importing, the add flow peeks the base name (`IModImportService.GetBaseName`)
and the would-be container (`IModImportService.FindExistingContainer`), then
asks `IProfileService.GetBaseNameCollision` whether any existing profile mod (a
different container) resolves to the same base name. On a hit, the import is
**refused**: nothing is created. The would-be container is excluded, so a re-add
of a mod already in the profile (same container, `AddMod` idempotent) is not a
collision.

**Staging:** at launch (alongside regenerating `mods.lst`), Magos materializes
the profile's mod root (the `--mod-path` dir) by, for each enabled mod,
discovering its base folder name (the single subdirectory inside the resolved
version folder) and symlinking `staged/<baseName>` to
`<versionFolder>/<baseName>/`. The base name, not the container's display name,
is the link + `mods.lst` name: mods bake their folder name into their code, so
the link must carry the base name for the mod's hardcoded paths to resolve. Like
`mods.lst`, the mod root is a projection of the profile's mod-list metadata,
regenerated each launch. Staging is a simple loop: base-name collisions are
blocked at import time, so staging never sees two mods with the same base folder
name in normal use. **Symlinks, never copies**: the repository holds the files;
`staged/` is a symlink projection.

**Startup cleanup:** `ModCleanup.PruneUnreferenced` runs once after composition,
dropping version folders no profile references + removing empty containers.
Keeps the on-disk tree in sync with what the profiles actually use.

**Relocation:** because paths are derived, changing `<ModsFolder>` is a physical
move of the tree plus a config update. `IModRepository.Relocate` owns the move +
config save + rescan as one atomic operation (rolling the move back on save
failure so files + config can never disagree); no manifest rewriting, no drift
detection. The Settings window's Storage section is the UI for it.

## Mod sources / integrations

- **Nexus Mods** — the primary source for user mods (most Darktide mods live
  there). Nexus API key or OIDC; version checks; downloads / updates.
- **GitHub Releases** — a source for mods that publish there; no auth required
  for public releases (version checks + downloads).
- **Local** — manually-installed mods (the user supplies the files).
- **DMF specifically** — the new-profile prompt offers to add it (most mods
  depend on it, so this is the common case; DMF isn't mandatory, so the prompt
  is an offer, not a requirement). **DMF sourcing is an OPEN decision
  (Phase 4):** the original plan (fetch from GitHub Releases, keyless) is broken
  — DMF's GitHub repo has no releases/tags; its canonical releases are on
  NexusMods. The lean is to require a Nexus API key be configured, or have the
  user download DMF manually. Bundling DMF with Magos is rejected
  (modding-community norms + Nexus rules). Resolution deferred to Phase 4. (See
  [Profiles](#profiles).)
- Per-mod: auto-update override (overrides the global setting); version pinning.
- **Import / Export** — profile import / export.

## nxm:// scheme handler

The `nxm://` URL scheme is how the "Mod manager download" button on a Nexus Mods
file page reaches a mod manager, and how an OAuth callback from a browser-based
authorize flow reaches it. Magos registers a tiny OS-level handler that captures
those clicks and relays them to the running app. Stage 1 of Phase 4 ships the
plumbing (the handler exe, the IPC channel, the URL parser and router, and the OS
registration service); Stage 2 (OAuth) and Stage 3 (mod download and acquisition)
drop real handlers into the routed seams.

The two reference implementations in the ecosystem (NexusMods.App and
ModOrganizer2) both use a small separate handler exe rather than the main app,
because the OS invokes the handler on every `nxm://` click and the main app's
startup is too heavy for a relay-then-exit, and spawning a second full app
instance to do single-instance detection and IPC is fragile given Magos's
singleton services (`IModRepository` scans and writes manifests; `ConfigLoader`
does atomic config writes). Magos follows the same pattern.

### Two-process model: dumb handler exe, smart IPC server

Two projects implement the path:

- **`Magos.NxmHandler`** (`magos-modificus/nxm-handler/`): the OS-registered
  scheme handler. A native-AOT console exe whose `Program.cs` is one line. It
  does no parsing and carries no DI graph; it forwards the raw URL string over
  the fixed named pipe (`Magos.Nxm`), or (cold start) launches Magos and retries
  the pipe until it comes up. AOT keeps it tiny and fast (tens of ms to start),
  and the relay + framing + parser stay trim-friendly so the trimmer drops
  everything else from the handler's closure.
- **`Magos.Modificus.Nxm`** (`magos-modificus/nxm/`): the library. URL types and
  parser, length-prefixed IPC framing, the Magos-side IPC server, the
  single-instance guard, the router and handler seams, the OS registrar, and the
  testable relay helper the handler exe calls.

The handler is deliberately dumb: it forwards the raw URL, and Magos owns URL
semantics. This keeps the OS-invoked path fast and puts all routing logic where
it is unit-testable.

The IPC protocol is one framed UTF-8 message per connection: a 4-byte
little-endian length prefix plus the URL payload, capped at 8 KiB. The handler
opens, sends one message, and closes; the server reads one message, routes it,
and disconnects (reusing the same server instance for the next client, so the
pipe name stays claimed for the app's lifetime).

### Single-instance: process enumeration, not the pipe bind

Magos enforces single-instance before binding the IPC pipe. The check lives in
`SingleInstanceGuard` and works by **process enumeration**: it asks
`Process.GetProcessesByName` for live processes sharing the current process's
name (excluding self by PID), and throws `NxmSingleInstanceException` if any
other process remains. The composition root catches the exception and exits
before any window shows.

This is deliberately decoupled from the pipe bind. The pipe bind is not a
reliable cross-platform single-instance claim: on Linux the transport is a Unix
domain socket, and two processes can both bind the same path. A probe-as-client
(the alternative the original spec considered) works but adds a startup tax on
Linux because the probe pends when no server exists. Process enumeration directly
answers "is another Magos already running?", is fast, unprivileged (no
elevation), and is decoupled from the IPC transport.

Accepted v1 race: two instances starting within milliseconds could both
enumerate, both see no other, both proceed. For a desktop double-launch (seconds
apart, not microseconds) this is negligible; a cross-process mutex or lock-file
on top is not worth the complexity for v1.

### Pipe bind: separate, non-fatal

With single-instance handled separately, the pipe bind is its own check with its
own graceful outcome. `NxmIpcServer.Bind` runs single-instance first (fatal on
collision), then constructs the `NamedPipeServerStream`. If construction throws
`IOException` (a real pipe problem: leftover socket, permissions; not another
instance, which the first check settled), the server logs a warning and
**continues running degraded**, with `IsBound` false and no accept loop. nxm
click-to-download is unavailable that session; everything else (profiles, mods,
launch) is unaffected. The composition root starts the accept loop only when
`IsBound` is true.

### Cold-start path

When the handler is invoked and Magos is not running, the handler launches the
sibling Magos exe (no args) and retries the pipe every 250ms up to 30s. Once
Magos binds the pipe, the handler connects, delivers the URL, and exits. **Magos
has no `--nxm` arg and no cold-start branch;** its startup is untouched by
Stage 1, and the handler owns the entire cold-start orchestration. If the
sibling Magos exe is missing, the handler logs to stderr and exits non-zero
without retrying (there is nothing to retry against, and a headless handler
never raises a desktop dialog).

### OS scheme-handler registration

A single `INxmHandlerRegistrar` interface with two platform implementations,
selected by runtime OS at DI time (mirroring `IPlatformLaunchStrategy`,
`IProcessLookup`, and `SteamRegistryReader`):

- **Windows** writes `HKCU\Software\Classes\nxm` (per-user, no elevation) so the
  OS launches the handler exe for `nxm://` clicks.
- **Linux** writes `~/.local/share/applications/magos-nxm-handler.desktop` plus a
  best-effort `xdg-mime default` invocation (the `.desktop` file is the source of
  truth most desktops honor; a missing `xdg-mime` is logged, not thrown).

Stage 1 ships the **service** only. The user-facing registration behavior (auto
on first run vs. Settings toggle vs. manual) is deferred to a later stage.

### Stage 2 and Stage 3 seams

The router dispatches parsed mod-download URLs to one handler interface:
`INxmModDownloadHandler` (mod downloads). Stage 1 ships a no-op default that
logs the parsed URL at Information; Stage 3 registers a real mod-download
handler (the acquisition flow downloads via the Nexus client and imports into
the unified repository). It is drop-in: the no-op default is registered with
plain `AddSingleton`, and MS DI resolves the last registration, so a later
`AddSingleton<INxmModDownloadHandler, RealImpl>()` after `AddNxm()` supersedes
the default.

**Stage 3's handler lives in the UI assembly.** The `NxmModDownloadHandler`
coordinates UI concerns (the active-profile session, the error dialog, the
UI-thread marshaling), so it sits in `Magos.Modificus.UI.Nxm` alongside its
dependencies. The reusable backend core, `IModAcquisitionService` (download +
extract + place), lives in Integrations and is what the handler calls to do
the actual work. Placing the handler in Integrations would create a dependency
cycle (Integrations cannot reference the UI assembly).

**Stage 2 removed the OAuth-callback seam.** Stage 1 originally shipped an
`INxmOAuthCallbackHandler` for an `nxm://oauth/callback` URL kind, expecting
the OAuth flow to ride on `nxm://`. Stage 2 corrects that: Nexus OAuth in Magos
uses a **loopback HTTP redirect** (`http://127.0.0.1:<port>/callback`, the RFC
8252 standard, MO2's pattern), independent of the `nxm://` handler. The
`nxm://oauth/callback` URL shape is still recognized by the parser (so it
parses cleanly rather than classifying as unknown), but the router logs it as
"handled by the loopback listener, not the nxm handler" and drops it. In normal
operation no such URL is delivered over IPC because the loopback listener
receives the callback, not the nxm handler.

### Nexus authentication (Phase 4 Stage 2)

Nexus Mods auth has two user-facing paths, both surfaced in the Integrations
dialog: **OAuth** (the primary, a loopback OIDC flow via
`Duende.IdentityModel.OidcClient`) and **API key** (the alternative, validated
against `GET /v1/users/validate.json`). The user's explicit choice is stored in
`NexusConfig.AuthMethod` (`None` / `OAuth` / `ApiKey`); there is no fallback.
Switching methods clears the other method's credentials (clean transition, no
stale leftovers). Sign-out resets to `None`.

**OAuth (loopback, RFC 8252).** `NexusOAuthTokenStore` owns an `OidcClient`
configured with `Authority = "https://users.nexusmods.com/oauth"`,
`ClientId = "magos-modificus"` (a build-time const, not config or env var),
`Scope = "openid profile email"`, and a `LoopbackBrowser` (an `IBrowser` impl).
The browser pre-grabs an ephemeral loopback port (exposed as `RedirectUri`),
the service passes it to `OidcClientOptions.RedirectUri`, OidcClient builds the
authorize URL with PKCE S256 (PKCE is automatic in OidcClient 7.x; there is no
`Policy.RequirePKCE` flag), the browser opens it via
`Process.Start(UseShellExecute=true)` (correct here, opening a URL via the OS
shell-open), the user consents, the loopback `HttpListener` receives the
callback, OidcClient exchanges the code for tokens, the store persists them.
Three-minute flow timeout; on expiry the service surfaces "Login timed out".

**API key.** `NexusAuthService.LoginWithApiKeyAsync` does a speculative write
(`AuthMethod = ApiKey` + the key) so the v1 client's auth factory picks it up,
calls `INexusClient.ValidateAsync`, and reverts on failure (the user keeps their
prior session). On success the display name + premium state come from the
validate response.

**401-reactive refresh.** The OAuth auth message factory refreshes via the
token store's `RefreshAsync` (OidcClient's refresh API + persisted new tokens)
on the first 401, serialized through a semaphore so concurrent 401s coalesce
into one refresh. The client retries the failed request once with the new
access token. The API-key factory has no refresh; a 401 surfaces "API key
invalid/expired" (no OAuth fallback). Refresh is reactive (matches MO2), not
proactive.

**App-identification headers** on every Nexus request: `Application-Name`,
`Application-Version`, `Protocol-Version: 1.0.0`, `User-Agent` (the MO2/NMA
convention).

**Rate limits** are parsed from the `x-rl-*` response headers
(`x-rl-daily-limit` / `x-rl-daily-remaining` / `x-rl-daily-reset` /
`x-rl-hourly-limit` / `x-rl-hourly-remaining` / `x-rl-hourly-reset`) into a
`NexusRateLimits` carried on every `Response<T>`. Stage 4 (update-check)
consumes them to back off; Stage 2 just parses + logs them. A 429 (or a 403
with `*-remaining: 0`) throws `NexusRateLimitException`.

**v1 endpoints** (grounded against NMA's `NexusApiClient.cs` + node-nexus-api,
mirroring that shape; v3 is Experimental for the surfaces we need, so v1 only):

- `GET /v1/users/validate.json` (API-key validate)
- `GET /oauth/userinfo` on the OAuth base URL (user info)
- `GET /v1/games/{domain}/mods/updated.json?period={1d|1w|1m}` (recent updates)
- `GET /v1/games/{domain}/mods/{modId}/files/{fileId}/download_link.json`
  (premium download links); same endpoint with `?key={nxmKey}&expires={epoch}`
  for free users
- `GET /v1/games/{domain}/mods/{modId}.json` (mod info)
- `GET /v1/games/{domain}/mods/{modId}/files.json` (mod files; unwrapped from
  `{"files":[...]}`)

Per-library public surfaces, exact signatures, and DI registration are documented
in [integrations reference](../reference/magos-modificus/integrations.md) and
[nxm reference](../reference/magos-modificus/nxm.md).

### Nexus mod acquisition (Phase 4 Stage 3)

When a user clicks "Mod manager download" on a Nexus file page, the Stage 1
handler exe relays the `nxm://` URL to the running app, the router dispatches
it, and the Stage 3 `NxmModDownloadHandler` orchestrates the download + import
into the active profile. The reusable core is `IModAcquisitionService`
(Integrations), which the handler calls and Stage 5's per-mod update button will
also call.

**Acquisition flow** (`ModAcquisitionService.AcquireFromNexusAsync`):

1. Resolve download links via `INexusClient.DownloadLinksAsync`. The free-user
   overload (with `nxmKey` + `nxmExpires` from the URL) is used when both are
   present; the premium (auth-only) overload otherwise. The **first** CDN link
   is used (Nexus returns them in priority order).
2. Resolve metadata: `GetModInfoAsync` for the mod name, `ListModFilesAsync` +
   match by `fileId` for the version string. **No degraded fallback**: a metadata
   failure surfaces a clear error and nothing partial lands (a mod stored under
   its id as a name is worse than a clean failure).
3. Download to `Path.GetTempFileName()` via a plain `HttpClient` + the 81920-byte
   buffered copy + `IProgress<long>` pattern. The temp file is always deleted
   (success or failure).
4. Import via `IModImportService.Import(tempPath, modName, NexusSource{ModId},
   version)`, which dedups by `NexusSource.ModId` (find-or-create container) +
   adds the version + flips `IsLatest`. Returns `(containerId, versionId)`.

**Handler checks** (`NxmModDownloadHandler.HandleAsync`): auth configured
(`AuthMethod != None`; the nxm key/expires is NOT a substitute for auth) and
active profile set (`IProfileSession.ActiveProfileId != null`). On success,
`IProfileService.AddMod(profileId, containerId, ModVersionPolicy.Latest)`. On
any failure (not cancellation), `IDialogService.ShowAlertAsync` with the error
message, marshaled to the UI thread via the injectable `invokeOnUi` seam
(production: `Dispatcher.UIThread.InvokeAsync`).

## Mod list (main view)

- Per-mod: enable / disable, remove, update (when the source reports a newer
  version), pin to version, per-mod auto-update override. The pinned-vs-latest
  policy drives version resolution at stage time (see [Mod repository](#mod-repository)).
- Auto-sort (dependency-driven; toggleable); manual reorder in the sequential
  view overrides auto-sort.
- When DMF is installed, it appears as a protected first entry (locked first
  by dependency resolution; updateable).
- **Hot-reload** — tied to the Enginseer live-control contract; out of v1.
- **Dependency view** — out of v1 (uncertain value; revisit later).
- **Conflict detection** — out of v1.

## Launch

The launch path **diverges by OS**. In both cases Magos resolves the profile,
writes `mods.lst` into the profile's mod root, then invokes the Enginseer
launcher with `--game-binary`, `--mod-path`, `--log-file`, `--log-level`.

### Windows (trivial)

Magos is a native .NET process; `magos_launcher.exe` is a native Windows
binary. The Enginseer library assembles the args and `Process.Start`s the
launcher directly. No Proton, no prefix, no path translation.

### Linux (native Magos + Proton-at-launch)

Magos runs **natively** on Linux (not Proton-wrapped). `magos_launcher.exe` is
a Windows binary, so to run it Magos invokes it under **Proton**, using
**Darktide's own compatdata** as the prefix — required, because the launcher
`CreateProcess`es Darktide, so the two must share the prefix.

**The constraint that shapes the design:** Proton reads
`STEAM_COMPAT_DATA_PATH` from the environment *before* `magos_launcher.exe`
runs, to decide which prefix to use. By the time the launcher executes it's
already inside that prefix; it cannot relocate itself, and Darktide inherits
the prefix regardless. So the compatdata must be set by whoever invokes Proton
— it is not passable as a launcher flag. **Magos sets both
`STEAM_COMPAT_DATA_PATH` (the Wine prefix) and
`STEAM_COMPAT_CLIENT_INSTALL_PATH` (the Steam install dir) in the environment
when it invokes Proton.** (The live-validated working invocation set both env
vars.) Steam discovers both; Enginseer-client sets both.

Responsibilities:

- **Steam library — discovery + escape hatch + fail-fast:**
  - Locate Steam (default `~/.local/share/Steam`; read `libraryfolders.vdf`
    for multi-library; handle non-default + Flatpak Steam installs).
  - Find Darktide's install (Windows in-prefix path) + its compatdata
    (`steamapps/compatdata/1361210/`).
  - Find the Proton version Darktide is configured to use (Steam's bundled
    Proton, or `compatibilitytools.d/` custom builds like ProtonUp-GE).
  - **Escape hatch:** when auto-discovery can't resolve any of the above,
    prompt the user for the missing path(s). This is possible only because
    discovery lives in the UI app, not in Enginseer (which has no UI and could
    only fail with a log line). Validate at Magos startup / profile setup so
    misconfiguration surfaces early (fail-fast), not at launch.
- **Enginseer library — invocation:**
  - Translate the profile's native mod-path → `Z:\...` (and confirm
    `--game-binary` is the in-prefix Windows path).
  - Assemble the launcher args.
  - `Process.Start` with `STEAM_COMPAT_DATA_PATH = <compatdata>` and
    `STEAM_COMPAT_CLIENT_INSTALL_PATH = <steam-install>` in env,
    command = `<proton> run <runtime-dir>/magos_launcher.exe <args>`.

**Enginseer is unchanged on Linux** — no Linux helper, no Steam/Proton
discovery, no new flag. It remains the Windows launcher + shell + mod_loader,
run under Proton with `STEAM_COMPAT_DATA_PATH` + `STEAM_COMPAT_CLIENT_INSTALL_PATH`
set by Magos.

**Known characteristic (not a defect):** when Magos launches directly, Steam
isn't supervising the session (no overlay / playtime tracking). The **Steam
non-steam shortcut** path is the answer for users who want full Steam
integration — see below.

### Launch wiring + Settings + escape-hatch (Phase 3 Track C)

The shell's `LaunchCommand` invokes `IEnginseerLaunchService.Launch(activeProfileId)`
(gated by `CanLaunch`: a profile is selected and the game is not running) and
branches on `LaunchResult.Status`:

- **`Launched`**: a brief localized status note ("Launched 'X'") + an immediate
  `IsGameRunning` refresh (the session's `Refresh`), so the running indicator +
  launch-availability react at once rather than waiting for the next poll.
- **`DiscoveryIncomplete`**: opens the focused escape-hatch dialog (below) with
  `LaunchResult.MissingDiscoveryFields`. **No auto-retry:** the user submits the
  paths, closes the dialog, and clicks Launch again. This avoids a loop if the
  user cannot get the paths right; the escape-hatch is a form, not a retry.
- **`Error`**: a modal alert surfacing `LaunchResult.Message`.

A new top-bar **Settings** button (gear) opens the **Settings window** with two
sections, both persisting through `IConfigLoader` (read live, so the next
`Discover()` / launch picks them up):

- **Discovery:** the four user-override paths (`MagosConfig.Discovery`).
  `SteamService.Discover()` runs the **validate + heal + persist** pipeline:
  each platform-relevant override is checked on disk (existing = valid, kept);
  missing/non-existent fields are healed from the platform discoverer (one run
  when any field needs healing) + the healed values are persisted back (only
  the healed fields; valid fields are preserved). On Windows the compatdata +
  Proton rows are hidden (Linux-only). When every field is valid the discoverer
  is skipped entirely (fast path).
- **Storage:** the mod-repository location (`ModsFolder`). Changing it runs the
  **atomic relocate** on `IModRepository.Relocate`, which owns the move + config
  save + rescan as one operation (rolling the move back on save failure so files
  + config can never disagree). After the Settings dialog closes, the shell
  reloads the mod list so the rescanned index is reflected in the rows.

The **escape-hatch dialog** is the focused form shown on `DiscoveryIncomplete`.
It shows inputs **only for the missing fields**, pre-filled with the current
config value (or blank). Submit does one read-modify-save of the entered paths
into `Discovery.User*Path`, then closes (no retry). A friendly header explains
auto-discovery could not resolve everything.

Both the Settings discovery rows and the escape-hatch rows are driven by a
shared **`DiscoveryField` descriptor** (one source of truth for the field
metadata: canonical name, human label, browse kind (folder/file), current value,
setter). The canonical names match `DiscoveryResult`'s field names, which are
what `LaunchResult.MissingDiscoveryFields` carries, so the escape-hatch shows
exactly the fields launch reported missing.

### Steam non-steam shortcuts

A shortcut added to Steam that launches Darktide with a specific profile, so
Steam supervises the session (overlay, playtime). Created from Magos against
the currently-selected profile.

- The shortcut's launch options bake in the resolved paths (compatdata, runtime
  dir, mod-path) at creation time, so firing the shortcut needs no rediscovery.
- The optional **slim profile launcher** is the shortcut's target: a thin
  native binary that accepts a profile argument and does what the Launch button
  does. It reuses the Magos Steam library for discovery and the Enginseer
  library for invocation.

## Configuration

One global config file for system-level settings (structured — e.g. JSON or
TOML):

- Log file location + level (the log is **truncated on each manager startup** — no rolling/retention/backup, matching the `magos_launcher` pattern).
- Profiles base folder (where profiles, mods, and settings are stored).
- Mods folder (the global mod store; see
  [Mod repository](#mod-repository)).
- Enginseer runtime dir (where `magos_launcher.exe` + `magos_shell.dll` +
  `mod_loader/` live).

Per-profile settings live with the profile, not in the global config.

## v1 scope

**In v1:**

- Profiles (create / edit / remove / switch — switch blocked while the game is
  running).
- Mod list: enable / disable / remove, update indicators, version pinning,
  per-mod auto-update override, auto-sort + manual sequential reorder.
- Mod storage (unified repository keyed by `(source, identity)`, version resolution by policy).
- Mod sources: Nexus Mods (primary) + GitHub Releases + local; DMF via the
  open sourcing decision (Phase 4 — see Mod sources).
- Launch Darktide (Windows trivial; Linux native + Proton-at-launch +
  discovery + escape hatch).
- Steam non-steam shortcuts.
- Global config + per-profile settings.
- DMF new-profile prompt.

**Out of v1 (deferred):**

- Enginseer live-control (status / hot-reload / live enable-disable) — awaits
  an Enginseer IPC contract expansion.
- Dependency-view mod list.
- Conflict detection.

## Open / future

- **Enginseer live-control contract** — the IPC / status surface that would
  enable hot-reload, live enable/disable, and in-Magos status display. Tracked
  as a GitHub issue on Enginseer; when it lands, the Magos Enginseer library
  grows from a launch façade to a richer client, and the UI's mod list gains
  live controls.
- **Slim profile launcher** — built alongside the Steam-shortcut feature;
  reuses the Steam + Enginseer libraries.
- **Distribution / packaging** (.NET self-contained, AppImage, distro
  packages, etc.) — undecided; a release/delivery concern, not architectural.

## References

- [darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer): the
  runtime Magos builds on (launcher/shell contract, mod loader, `mods.lst`
  contract, two-roots model).
- `docs/architecture/README.md`: the Magos Modificus architecture + component
  model.
- `AGENTS.md`: agent orientation + conventions.
