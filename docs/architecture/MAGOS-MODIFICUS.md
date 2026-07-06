# Magos Modificus — architecture

**Magos Modificus** is the user-facing mod manager app for darktide-mod-magos —
the second of the project's two components, sitting on top of the Enginseer
runtime. It owns everything user-facing: profile management, mod staging, load
order, dependency resolution, mod-source integrations (GitHub Releases, Nexus
Mods, Steam), and the "Launch Darktide" button that invokes the Enginseer
launcher. Enginseer does the injection + mod loading; Magos Modificus owns the
management experience around it.

> **Status: Phases 0–3 complete; Phase 4 in progress (Stages 1–4 done).** The
> foundation (.NET 10 + Avalonia 12 layout, DI composition, structured logging,
> global config schema/loader) plus the backend libraries are built: Profiles,
> Steam, Integrations, Enginseer-client (Phase 1) + Mods (Phase 2). The Phase 3
> UI is in place across all four tracks: Track A (app shell + profile
> management), Track D (global Preferences + i18n), Track B (the mod-list UI +
> local import), and Track C (Launch wiring + Settings window + discovery
> escape-hatch). Phase 4 adds the Nexus integration: the nxm:// scheme handler
> (Stage 1), Nexus auth + Integrations dialog (Stage 2), mod acquisition
> (Stage 3), and the update-check service (Stage 4). The app is user-usable:
> create profiles, import mods, manage the mod list, configure Settings, and
> launch modded Darktide. The **Launcher** is a stub (Phase 5). It builds on the
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
creates) the container for the source + extracts the archive / copies a folder
into the repository-managed opaque version folder via
`IModRepository.AddVersion`. Archive detection is content-based (via
SharpCompress's `ArchiveFactory`, reading magic bytes, not the extension), so
zip, 7z, rar, and the other SharpCompress formats all flow through one path;
extraction is traversal-safe (per-entry `WriteToDirectory`, directory entries
skipped, a defense-in-depth `AssertSafePath` containment check per file entry,
no `SymbolicLinkHandler`). The validated base folder is **preserved** under
`<versionFolder>/<base>/` (the folder import copies the folder itself, not its
contents; the archive is validated to have a single top-level folder before
extraction). Container dedup: Untracked by name, Nexus by mod id, GitHub by
owner/repo. Version dedup: re-importing the same tag reuses its folder
(refreshed); a new tag creates a new version + flips `isLatest`. The service
returns `(containerId, versionString)`; the caller then adds the profile
reference via `IProfileService.AddMod`. Remote acquisition (Nexus / GitHub API
clients, auto-fetch) is Phase 4 Stage 3; the acquisition service downloads the
archive to a temp path preserving the real Nexus `file_name` extension, then
hands it to the import service.

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

The `nxm://` URL scheme is how the "Mod manager download" button on a Nexus
Mods file page reaches Magos. Magos registers a tiny native-AOT handler exe
that the OS invokes on each `nxm://` click; it forwards the raw URL over a
named pipe to the running app (or cold-starts Magos and retries), where the URL
is parsed, classified, and dispatched to a pluggable handler. Single-instance is
enforced by process enumeration before the pipe bind, and the pipe bind is a
separate, non-fatal check that degrades gracefully; the OS registration is
auto-applied on startup. Full detail (the two-process model, the cold-start
path, single-instance enforcement, pipe-bind behavior, OS registration, and URL
routing) is in [nxm:// scheme handler architecture](nxm-scheme-handler.md); the
public surface is in [nxm reference](../reference/magos-modificus/nxm.md).

## Nexus authentication

Nexus Mods auth has two user-facing paths, both surfaced in a separate
Integrations dialog: **OAuth** (the primary, a loopback OIDC flow via
`Duende.IdentityModel.OidcClient`, RFC 8252) and **API key** (the alternative,
validated against `GET /v1/users/validate.json`). The user's explicit choice is
stored in `NexusConfig.AuthMethod` (`None` / `OAuth` / `ApiKey`); the v1 client's
auth factory is selected by that flag with no fallback, and switching methods
clears the other method's credentials. OAuth tokens are persisted in `MagosConfig`
with 401-reactive refresh. Full detail (the loopback flow, the Integrations
dialog, auth-factory selection, token persistence, the OAuth client_id, rate
limits, and the v1 endpoints) is in
[Nexus authentication architecture](nexus-authentication.md); the public surface
is in [integrations reference](../reference/magos-modificus/integrations.md).

## Mod acquisition

When a user clicks "Mod manager download" on Nexus, the
[nxm handler](nxm-scheme-handler.md) relays the URL and the Stage 3
`NxmModDownloadHandler` orchestrates the download and import into the active
profile. The reusable core is `IModAcquisitionService` (Integrations): it
resolves the CDN download links, fetches mod metadata, downloads to a
`.zip`-named temp file, and imports via `IModImportService`. The handler (in the
UI assembly, not Integrations, because it coordinates UI-only services) checks
auth and an active profile, calls the service, registers the mod with
`LatestPolicy`, refreshes the mod list, and surfaces errors via
`ShowAlertAsync`. Stage 5's per-mod update button calls the same service. Full
detail (the acquisition flow, the handler checks, the UI-assembly placement, and
startup OS registration) is in [mod acquisition architecture](mod-acquisition.md);
the public surface is in
[integrations reference](../reference/magos-modificus/integrations.md).

## Update check

Stage 4's `IUpdateCheckService` (Integrations) is the Nexus-only update check.
On profile load it calls `ModUpdatesAsync("warhammer40kdarktide", Month)` once,
intersects the response with the active profile's `LatestPolicy` +
`NexusSource` mods, and flags any whose imported version's `ImportedAt`
predates the Nexus-reported `LatestFileUpdateUtc` (the file-upload time, not
mod-page activity, which would false-positive on comments). One API call
regardless of profile size; `PinnedPolicy`, `UntrackedSource`, and
`GitHubSource` mods are skipped. Rate-limit-aware: if the response reports an
exhausted daily or hourly quota (and the limit was actually reported, guarding
against the all-zero header-absent fallback), the result is flagged
`RateLimited` and Stage 5 surfaces a "check incomplete" indicator rather than
"all up to date."

The result (`UpdateCheckResult` with per-mod `ModUpdateInfo`) is published via
`LastResult` + a `CheckCompleted` event for Stage 5's badges to consume without
re-awaiting. The check is fired fire-and-forget by `UpdateCheckRunner` (UI),
which subscribes to `IProfileSession.PropertyChanged` filtered to
`ActiveProfileId` (startup-with-restored-id + active-profile switch). No UI in
Stage 4; Stage 5 adds the per-row "update available" badges + the per-mod
update button (which calls Stage 3's `IModAcquisitionService`). The public
surface is in [integrations reference](../reference/magos-modificus/integrations.md).

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
