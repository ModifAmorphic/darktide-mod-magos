# Magos Modificus — architecture

**Magos Modificus** is the user-facing mod manager app for darktide-mod-magos —
the second of the project's two components, sitting on top of the Enginseer
runtime. It owns everything user-facing: profile management, mod staging, load
order, dependency resolution, mod-source integrations (GitHub Releases, Nexus
Mods, Steam), and the "Launch Darktide" button that invokes the Enginseer
launcher. Enginseer does the injection + mod loading; Magos Modificus owns the
management experience around it.

> **Status: Phases 0–2 complete.** The foundation (.NET 10 + Avalonia 12 layout,
> DI composition, structured logging, global config schema/loader, a bare UI
> shell) plus the backend libraries are built: Profiles, Steam, Integrations,
> Enginseer-client (Phase 1) + Mods (Phase 2). The **UI is still the bare
> Phase-0 window** (no profile/mod-management UI yet) and the **Launcher** is a
> stub (Phase 5). Next: Phase 3 (UI build-out). Enginseer (the runtime it builds
> on) is built — see `docs/architecture/ENGINSEER.md`.

## In scope for this document

- The component's role, technology choices, and project layout.
- The domain-library breakdown and each library's responsibilities.
- The Enginseer contract Magos consumes (the stable surface it builds against).
- The profiles model, mod storage, mod sources, and the launch flow on Windows and Linux.
- The v1 scope cut.

## Out of scope (handled elsewhere)

- The Enginseer runtime internals — `docs/architecture/ENGINSEER.md`.
- The mod loader ↔ DMF integration — `docs/architecture/MOD_LOADER-DMF.md`.
- The load-order file contract (`mods.lst`) — specified in `MOD_LOADER-DMF.md`
  and `enginseer/mod_loader/mod_manager.lua`; Magos authors it, Enginseer
  consumes it.

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

1. **Load config** — `new ConfigLoader().Load()` produces a fully-defaulted
   `MagosConfig` (defaults + JSON overrides; first-run safe). Logging needs this
   first.
2. **Build the logger** — `LoggingBootstrap.CreateLoggerFactory(config)`
   (Serilog console + file, level-honored, truncated on startup). Both config
   and the logger are constructed **outside** DI because DI itself needs them.
3. **Compose services** — `new ServiceCollection()`, then the `Add<Library>()`
   extensions in their real order:
   - `AddGeneral(config, loggerFactory)` — registers the config singleton, the
     logger factory, `AddLogging()`, and the config loader.
   - `AddMods()` — the unified mod repository + import service (called explicitly here and
     idempotently again inside `AddProfiles()`, so the store is discoverable at
     the root and `IProfileService` always resolves its staging dependency).
   - `AddProfiles()` — profile service + the `SymlinkCreator` staging seam.
   - `AddIntegrations()` — the typed GitHub HTTP client.
   - `AddSteam()` — Steam discovery + the platform process-lookup seam.
   - `AddEnginseerClient()` — the launch façade + the process-launcher seam.
   - `AddLauncher()` — the slim profile launcher stub (Phase 5).
   - `AddTransient<MainWindow>()` + `AddSingleton<MainViewModel>()` — the UI
     surface.
4. **Build** — `BuildServiceProvider()`.
5. **Startup prune** — `ModCleanup.PruneUnreferenced` runs once (best-effort,
   logged + swallowed on failure) to drop repository versions no profile
   references + empty containers.

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

See `docs/architecture/ENGINSEER.md` for the full contract (env-var table,
logging, the hook-ready handshake).

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
resolves (or creates) the container for the source, then extracts a `.zip` /
copies a folder into the repository-managed opaque version folder via
`IModRepository.AddVersion`. Container dedup: Untracked by name, Nexus by mod
id, GitHub by owner/repo. Version dedup: re-importing the same tag reuses its
folder (refreshed); a new tag creates a new version + flips `isLatest`. The
service returns `(containerId, versionString)`; the caller then adds the
profile reference via `IProfileService.AddMod`. Remote acquisition (Nexus /
GitHub API clients, auto-fetch) stays in Phase 4.

**Staging:** at launch (alongside regenerating `mods.lst`), Magos materializes
the profile's mod root (the `--mod-path` dir) by symlinking `staged/<displayName>`
to each enabled mod's resolved version folder. Like `mods.lst`, the mod root is
a projection of the profile's mod-list metadata, regenerated each launch.
**Symlinks, never copies** — the repository holds the files; `staged/` is a
symlink projection.

**Startup cleanup:** `ModCleanup.PruneUnreferenced` runs once after composition,
dropping version folders no profile references + removing empty containers.
Keeps the on-disk tree in sync with what the profiles actually use.

**Relocation:** because paths are derived, changing `<ModsFolder>` is a
physical move of the tree plus a config update. No manifest rewriting, no drift
detection.

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

- `docs/architecture/ENGINSEER.md` — the runtime Magos builds on; the full
  launcher/shell contract.
- `docs/architecture/MOD_LOADER-DMF.md` — the `mods.lst` contract (which Magos
  authors) + the two-roots model.
- `docs/architecture/README.md` — the project-wide architecture + component
  model.
- `AGENTS.md` — agent orientation + conventions.
