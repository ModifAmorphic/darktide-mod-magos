# AGENTS.md — darktide-mod-magos

> Orientation for any agent working in this repo. Read this first. This file
> is for **agents**, not humans — the human-facing entry point is `README.md`.

## What this is

**darktide-mod-magos** is the Magos Modificus mod manager for Warhammer 40,000:
Darktide (.NET 10 + Avalonia 12), built through Phase 3 (the app is user-usable;
Phase 4 + 5 remain). It launches the game modded via the
[Enginseer runtime](https://github.com/ModifAmorphic/darktide-enginseer) (DLL
injection: no game-directory footprint, no bundle-database patching; the runtime
is a separate repo) and stays out of the way for vanilla play (launch from Steam
= unmodified game). See `docs/architecture/` for the architecture.

## Baseline (read before planning)

The POC (on the `poc` branch) is a capability proof and reference, **not** a
pre-release of production code. Production is built ground-up with
testability, review, and production-readiness as first-class goals. The POC
carries forward proof of feasibility only; it does not carry forward code.
Requirements, architecture, and technology choices are made fresh. (Runtime +
game-binary constraints now live with the runtime, in
[darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer).)

## Repository state

- **`main`** — production. Magos Modificus is built through Phase 3 (all four
  tracks merged: Track A the app shell + profile management, Track D global
  Preferences + i18n, Track B the mod-list UI + local import, Track C the Launch
  flow + Settings window + discovery escape-hatch) + Phase 4 Stages 1-4 (the
  nxm:// scheme handler, Nexus auth + Integrations dialog, mod acquisition, and
  the update-check service). The app is user-usable: create profiles, import
  mods (folder/`.zip`, Nexus/GitHub/Untracked), manage the mod list
  (enable/disable/reorder/policy/remove), configure Settings (discovery paths +
  mod-repo location), and launch modded Darktide. The Launcher is a stub
  (Phase 5). Backend libraries: Profiles, Mods (the unified mod repository),
  Steam, Integrations, Enginseer-client, General. The Enginseer runtime is a
  separate repo
  ([darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer));
  this repo holds Magos Modificus only.
- **`poc`** — historical proof-of-concept, reference only. Not built upon.
- Development is branch + PR; no unreviewed merges to `main` (reviewed +
  covered + qa'd + CI green).

## Directory structure (current `main`)

```
magos-modificus/        Magos Modificus — the mod manager app (.NET 10 + Avalonia 12)
  magos-modificus.sln   solution root (classic .sln)
  Directory.Build.props  shared MSBuild props (net10.0, nullable, implicit usings)
  ui/                   Magos.Modificus.UI — the Avalonia executable + DI composition root
                          (Phase 3 Track A: shell + profile management: dropdown switch,
                          persisted active profile, create/rename/delete dialog;
                          Phase 3 Track D: global Preferences (theme + font scale + language)
                          via `IPreferencesService` + the i18n infrastructure: `Strings.resx`
                          + `LocalizationService` for dynamic culture switching;
                          Phase 3 Track B: the mod-list UI;
                          Phase 3 Track C: Launch wiring + Settings window +
                          discovery escape-hatch over the shared `Settings/DiscoveryField`
                          descriptor + `DiscoveryConfig`/`SteamService.Discover()` validate+heal+persist +
                          `IModRepository.Relocate/Rescan`;
                          Phase 4 Stage 1: `AddNxm()` + `StartNxmServer` (single-instance via
                          `SingleInstanceGuard` process enumeration, separate from the `Magos.Nxm`
                          pipe bind which degrades gracefully on IOException; a second Magos exits
                          via `NxmSingleInstanceException` -> `Environment.Exit(1)` before the
                          window shows);
                          Phase 4 Stage 2: the Integrations dialog (Nexus-only) + its
                          `OpenIntegrationsCommand` on the shell (left of the profiles button),
                          wired through `IDialogService.ShowIntegrationsAsync` -> `IntegrationsViewModel`
                          -> `INexusAuthService` (OAuth loopback + API-key validate + sign-out) +
                          the running-state gate (auth controls disable while Darktide runs);
                          Phase 4 Stage 3: `IModAcquisitionService` (download + extract + place
                          orchestrator in Integrations) + the real `NxmModDownloadHandler` (in UI,
                          coordinating IDialogService + IProfileSession + Dispatcher.UIThread) that
                          replaces the Stage 1 no-op via DI last-registration-wins, registered after
                          AddNxm() in MagosComposition;
                          Phase 4 Stage 4: `UpdateCheckRunner` (ui/Session/) the
                          UI-layer glue that fires `IUpdateCheckService.CheckAsync`
                          fire-and-forget on profile load (startup-with-restored-id
                          + active-profile switch via IProfileSession.PropertyChanged
                          filtered to ActiveProfileId), registered + started
                          best-effort from MagosComposition)
  general/              Magos.Modificus.General — cross-cutting infra (logging bootstrap,
                          config loader, app-state store, AddGeneral() DI ext)
  config/               Magos.Modificus.Config — the MagosConfig schema + defaults (POCO),
                        including the NexusConfig slot under Integrations (Phase 4 Stage 2:
                        AuthMethod {None,OAuth,ApiKey}, ApiKey, OAuth tokens, base URLs)
  profiles/             Magos.Modificus.Profiles — profile data model, persistence,
                        container-based staging (ProfileService.PrepareModRoot
                        discovers each enabled mod's base folder name inside the
                        resolved version folder + symlinks staged/<baseName> ->
                        <versionFolder>/<baseName>/, then writes mods.lst; the
                        base name, not the container's display name, is the link
                        + mods.lst name) + SetModPolicy transitions + the
                        import-time base-name collision hard-block
                        (GetBaseNameCollision; two same-folder mods can't coexist
                        in a profile) + the auto-sort seam
                        (IModOrderResolver/IdentityModOrderResolver, identity stub now;
                        real dependency-driven resolver later) + ModCleanup (the startup
                        prune orchestration)
  mods/          Magos.Modificus.Mods — the unified mod repository
                        (IModRepository: UUID containers per (source, identity),
                        opaque-ID version subfolders, per-container container.json
                        manifests, in-memory index rebuilt from a scan, PruneUnreferenced
                        GC at startup) + the version-policy model (ModVersionPolicy:
                        PinnedPolicy/LatestPolicy; PinnedPolicy pins by VersionId, a foreign
                        key to ModVersion.Folder, so the repo is the sole source of truth for
                        version details) + the
                        mod-source provenance model (ModSource: UntrackedSource/
                        NexusSource/GitHubSource + ModSourceParser URL parsing) + the
                        local-import service (IModImportService: folder/archive ->
                        container/version; content-based archive detection via
                        SharpCompress (zip/7z/rar/...) not extension, traversal-safe
                        per-entry extraction with AssertSafePath guard; validates the
                        source has exactly one base dir with a matching <base>.mod +
                        preserves the base folder under <versionFolder>/<base>/;
                        exposes GetBaseName + FindExistingContainer peeks for the
                        collision block).
  integrations/         Magos.Modificus.Integrations — GitHub Releases client
                        (IGitHubClient: ListReleases/GetLatestRelease/DownloadAssetAsync
                        via IHttpClientFactory, typed exceptions, optional PAT)
                        + the Nexus Mods v1 client + auth (Phase 4 Stage 2:
                        INexusClient over the v1 REST endpoints with per-request
                        auth via INexusAuthMessageFactory selector — ApiKey /
                        OAuth / None factories, the latter doing 401-reactive
                        refresh; NexusAuthService the OAuth loopback + API-key
                        validate + sign-out orchestrator; NexusOAuthTokenStore
                        owns the OidcClient + token persistence; LoopbackBrowser
                        the IBrowser impl with an HttpListener on an ephemeral
                        port; Duende.IdentityModel.OidcClient 7.1.0 for the
                        OAuth machinery; client_id is the build-time const
                        "magos-modificus";
                        Phase 4 Stage 3: IModAcquisitionService the download +
                        extract + place orchestrator over INexusClient +
                        IModImportService + a plain HttpClient for the CDN
                        download; AcquireFromNexusAsync resolves the download
                        links, fetches name + version metadata, downloads to
                        temp, then imports via IModImportService.Import;
                        Phase 4 Stage 4: IUpdateCheckService the Nexus-only
                        update-check service (1 ModUpdatesAsync call per check,
                        intersected with the profile's LatestPolicy+NexusSource
                        mods; compares LatestFileUpdateUtc against the imported
                        version's ImportedAt; rate-limit-aware with the all-zero
                        Unknown guard; LastResult + CheckCompleted event for
                        Stage 5 badges; Integrations now references Profiles,
                        acyclic, for IProfileService.GetModList)
  steam/                Magos.Modificus.Steam — Steam + Darktide + Proton discovery
                        (multi-library + compatdata), IsGameRunning (WinProcessLookup
                        via process comm on Windows; LinuxProcessLookup via /proc
                        argv[0] under Proton — selected once by DI), injectable seams
  enginseer-client/     Magos.Modificus.EnginseerClient — the v1 launch façade
                        (IEnginseerLaunchService.Launch → LaunchResult; Windows: direct
                        launcher Process.Start; Linux: proton run with both STEAM_COMPAT_*
                        env + Z:\-translated paths)
  launcher/             Magos.Modificus.Launcher — stub (slim profile launcher exe;
                          the Steam non-steam-shortcut target)
  nxm/                  Magos.Modificus.Nxm — the nxm:// scheme-handler plumbing
                        (Phase 4 Stage 1): NxmUrlParser (mod-download / oauth-callback /
                        collection URL types), NxmIpcFraming (length-prefixed UTF-8 frames),
                        SingleInstanceGuard (the process-enumeration single-instance check,
                        with an injectable enumerator seam), NxmIpcServer (the named-pipe
                        server; Bind runs two SEPARATE checks: SingleInstanceGuard first
                        (fatal NxmSingleInstanceException on collision), then the pipe bind
                        which degrades gracefully on IOException; accept loop Disconnects
                        between clients), INxmRouter + no-op INxmModDownloadHandler
                        default (Stage 3 registers the real handler via AddSingleton
                        last-wins, in MagosComposition after AddNxm()), the OS
                        scheme-handler registrar
                        (INxmHandlerRegistrar: WindowsNxmHandlerRegistrar writes
                        HKCU\Software\Classes\nxm; LinuxNxmHandlerRegistrar writes a .desktop
                        file + xdg-mime default), + NxmHandlerRelay (the testable core the
                        handler exe calls: hot-path IPC delivery + cold-start launch+retry,
                        UseShellExecute=false on both OSes). AOT-friendly (IsAotCompatible;
                        only raw byte/UTF-8 IO in the handler path).
  nxm-handler/          Magos.Modificus.NxmHandler — the OS-registered nxm:// scheme handler
                        (console exe, native AOT). Program.cs is one line: NxmHandlerRelay.RunAsync.
                        Forwards the raw URL to running Magos over the fixed pipe, or (cold start)
                        launches Magos (no args) + retries the pipe ~250ms/30s, then delivers.
  tests/
    Magos.Modificus.General.Tests/         xUnit tests for the general library
    Magos.Modificus.Profiles.Tests/        xUnit tests for the profiles library (incl. staging)
    Magos.Modificus.Mods.Tests/      xUnit tests for the mod repository + import
    Magos.Modificus.Integrations.Tests/    xUnit tests for the GitHub Releases client
                                          + the Nexus client (against a fake HttpMessageHandler),
                                          the auth factories (apikey / OAuth / None + selector),
                                          the OAuth flow scripted with a fake IBrowser + stub
                                          discovery+token endpoint (via the OidcClient backchannel
                                          seam), the LoopbackBrowser/HttpListener against an
                                          ephemeral port, the NexusConfig JSON round-trip, and the
                                          ModAcquisitionService (download + extract + place against
                                          a fake INexusClient + fake IModImportService + stub CDN)
                                          + the UpdateCheckService (Nexus-only
                                          update check against a fake INexusClient +
                                          fake IProfileService + fake IModRepository)
    Magos.Modificus.Steam.Tests/           xUnit tests for discovery + IsGameRunning
    Magos.Modificus.EnginseerClient.Tests/ xUnit tests for the launch façade (dual-purpose:
                                            `dotnet test` = xUnit; `dotnet run` = composition smoke harness)
    Magos.Modificus.UI.Tests/              xUnit tests for the shell + manage-profiles
                                            view models (profile CRUD/switch, active-profile
                                            persist, switch-blocked-while-running; dialog via
                                            an injectable IDialogService seam; + the
                                            NxmModDownloadHandler auth/profile gates + error
                                            wiring against in-memory fakes)
    Magos.Modificus.Nxm.Tests/             xUnit tests for the nxm library (parser, framing,
                                            IPC server resilience, SingleInstanceGuard, router,
                                            relay helper, Linux registrar, AddNxm wiring;
                                            serialized via DisableTestParallelization since
                                            real named pipes are an OS-level shared resource)
docs/               architecture/ + reference/ (magos-modificus/ per-library API refs)
.github/workflows/  CI: magos-build (Magos Modificus)
.gitignore          ignores .NET bin/obj, build artifacts, _local/
```
## Magos Modificus ops

Build + test the mod-manager app — run from the repo root (.NET 10 SDK required):
```sh
dotnet build magos-modificus/magos-modificus.sln --configuration Release
dotnet test  magos-modificus/magos-modificus.sln --configuration Release
dotnet run   --project magos-modificus/ui --configuration Release   # app shell window
```
- The composition root is `magos-modificus/ui/MagosComposition.cs` (loads
  config → builds the Serilog logger → wires every `Add<Library>()` → runs the
  startup `ModCleanup.PruneUnreferenced` pass + the startup
  `ISteamService.Discover()` validate/heal/persist pass).
- **Config** is `MagosConfig` (`magos-modificus/config/`) — defaults under the
  OS local-app-data dir; loaded live from JSON by `general/ConfigLoader.cs`
  (consumers inject `IConfigLoader` and re-read per op, so runtime config
  changes via the Settings window take effect immediately; #31). Missing
  file/dir → defaults (first-run safe).
- **Logging** is Serilog (console + file) bridged into
  `Microsoft.Extensions.Logging`; honors `Logging:Level` + `Logging:LogFile`.
- The backend libraries are all implemented: **Profiles** (profile data model +
  lifecycle; container-based staging, where `PrepareModRoot` discovers each
  enabled mod's base folder name inside the resolved version folder via
  `IModRepository` + symlinks `staged/<baseName>` -> `<versionFolder>/<baseName>/`,
  then writes `mods.lst`; the base name, not the container's display name, is the
  link + mods.lst name; no per-profile mod files) + the import-time base-name
  collision hard-block (`GetBaseNameCollision`; two same-folder mods can't
  coexist in a profile), **Steam** (Steam + Darktide + Proton discovery + `IsGameRunning`),
  **Integrations** (GitHub Releases client + the Nexus v1 client/auth +
  `IModAcquisitionService` the download + extract + place orchestrator +
  `IUpdateCheckService` the Nexus-only update-check service),
  **Enginseer-client** (the launch
  façade), **Mods** (the unified `IModRepository`: UUID containers per
  (source, identity), opaque-ID version subfolders, per-container
  `container.json` manifests, in-memory index rebuilt from a scan,
  `PruneUnreferenced` GC; the version-policy model `ModVersionPolicy`; the
  mod-source provenance model `ModSource`
  (`UntrackedSource`/`NexusSource`/`GitHubSource`) + `ModSourceParser`; the
  local-import service `IModImportService`). **General** carries cross-cutting
  infra: logging, `ConfigLoader`, and `AppStateStore` (the active-profile id,
  persisted to `app-state.json`). **Phase 3** (all four tracks) is done: Track A
  the shell + profile management (with an `IProfileSession` (ui/) as the single
  authority for the active profile, the switch-block gate, and the live
  running-state), Track D global Preferences + i18n infrastructure, Track B the
  mod-list UI (view mods with source/version badges, enable/disable,
  remove-with-confirm, reorder, per-mod Latest/Pinned policy, auto-sort identity
  stub, and local folder/`.zip` import via file picker + drag-and-drop, joined
  to containers via `IModRepository` by `ContainerId`), and Track C Launch
  (`LaunchCommand` -> `IEnginseerLaunchService.Launch` -> branch on
  `LaunchResult.Status` (`Launched` -> status note + immediate `IsGameRunning`
  refresh; `DiscoveryIncomplete` -> the focused discovery escape-hatch modal
  over the shared `DiscoveryField` descriptor; `Error` -> modal alert) + a
  Settings window editing `MagosConfig.Discovery` user overrides (per-field
  read-modify-save) + `ModsFolder` live-relocate via the atomic
  `IModRepository.Relocate` over the `DiscoveryConfig` +
  `SteamService.Discover()` validate+heal+persist pipeline). The **Launcher**
  is a stub (Phase 5). See `docs/architecture/MAGOS-MODIFICUS.md`.

## Key docs

- `docs/architecture/` — the Magos Modificus architecture (component model,
  the Enginseer contract Magos consumes, profiles, launch).
- `docs/reference/magos-modificus/` — per-library API reference for the Magos
  Modificus backend libraries.
- [darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer) —
  the Enginseer runtime (architecture, build, game-binary reference, mod
  loader).

## Conventions

- **Conventional Commits** (`type(scope): subject`); commit freely on feature
  branches. Branch + PR flow; no unreviewed merges to `main`.
- Don't commit secrets, the game binary, or anything under `_local/`.
- **Do not trust training data for framework/library version-specific APIs.** The
  project uses Avalonia 12.x + .NET 10, which postdate the model's training data.
  Before deciding an approach or delegating UI/framework work: determine the exact
  version in use, assess whether you are current on it, and if not, READ THE CURRENT
  DOCS (e.g. docs.avaloniaui.net) before proposing or implementing. Stale knowledge
  has bitten this project (the WPF-era `SizeToContent` toggle, `NoChrome`, and
  `CanMinimize` hiding were all wrong for Avalonia 12.x).
- **Discuss non-trivial or hacky UI/approach decisions before implementing.** Do not
  delegate or commit a workaround without surfacing it first.
- **Do not commit a change as a "fix" before the operator verifies it.** Leave fixes
  uncommitted (or clearly WIP/pending) until the operator confirms; they test on
  their own machine.
- **Be consultative on UI.** Propose UI approaches and discuss, especially
  non-obvious ones, rather than implementing unilaterally. The operator is the UI
  authority.
- **UI icons + decorative markers are drawn geometry, not Unicode glyphs.** In the
  Avalonia UI, icons are `<Path Data="…">` (standard Material/Fluent-style path
  data, dependency-free, themed via foreground) and dots/markers are `<Ellipse>`,
  never `✏`/`🗑`/`⚙`/`●` symbol/emoji glyphs (which render unreliably across
  fonts/platforms). Scoped to icons/markers; prose punctuation is covered by the
  writing convention below.
- **No em-dashes in prose** (code comments, docs, commits, chat). Em-dashes read
  as an AI-generated tell; use a comma, colon, parentheses, semicolon, or period
  instead.

## Naming convention

Keep the established thematic name, **Magos** (the app), for user-facing UI
surfaces (Magos Modificus). Use plain, descriptive names for code components
(libraries, modules, types, functions). Reserve Warhammer 40k / Adeptus
Mechanicus flavor for the UI; docs and code read as plain engineering
documentation.

- **Folders/filenames:** lowercase.
- **Prose/docs:** "Magos Modificus" is the app's public name; "the Enginseer
  runtime" / "Enginseer" refers to the separate runtime repo
  ([darktide-enginseer](https://github.com/ModifAmorphic/darktide-enginseer)).
- Don't obscure: names should be descriptive and accessible, not cryptic.

## README pattern

Docs follow a two-tier README pattern:

- **Root `README.md`** — audience is the **general / end user**: what Magos is,
  its components, and how to get it running. **No build internals.**
- **Component-dir `README.md`** (e.g. `magos-modificus/README.md`) — audience is
  **developers / power users**: build instructions, sub-component details,
  testing, links to the architecture specs.

The **root README links to** the component READMEs — it does **not** duplicate
their content. When a component gets (or changes) a README, ensure the root
links to it and that the split holds (user-facing up top, dev detail under the
component).

## Before opening a PR: keep docs current

Docs must reflect the code in the PR. Before opening a PR for any change that
affects repo structure, build, architecture, or ops, update:
- **`AGENTS.md`** (this file): directory structure, ops, architecture pointers,
  to reflect the change.
- **`README.md`** (root): if the **user-facing** structure/status changed.
  Keep it user-facing (see [README pattern](#readme-pattern)); dev/build detail
  goes in the relevant component README, and the root must link to it.
- **Component-dir `README.md`** (e.g. `magos-modificus/README.md`): for
  build/dev detail under that component; ensure the root links to it.
- **`docs/architecture/`** for any architecture change.
- **`docs/reference/magos-modificus/`**: per-library API reference. When a Magos
  Modificus library's public surface, key types, or DI registration changes,
  update its `docs/reference/magos-modificus/<library>.md` in the same PR.

Then ensure the Magos Modificus build + tests pass
(`dotnet build`/`dotnet test magos-modificus/magos-modificus.sln`). **Outdated
docs in a PR are a review blocker**, including this file.
