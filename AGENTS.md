# AGENTS.md -- Modificus Curator

> Orientation for any agent working in this repo. Read this first. This file
> is for **agents**, not humans -- the human-facing entry point is `README.md`.

## What this is

**Modificus Curator** is the mod manager for Warhammer 40,000:
Darktide (.NET 10 + Avalonia 12). The app is user-usable. It launches the game
modded via
[Modificus Relay](https://github.com/ModifAmorphic/darktide-modificus-relay) (DLL
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
[darktide-modificus-relay](https://github.com/ModifAmorphic/darktide-modificus-relay).)

## Repository state

- **`main`** -- production. Modificus Curator includes the app shell + profile
  management, global Preferences + i18n, the mod-list UI + local import, the
  Launch flow + Settings window + discovery escape-hatch, the nxm:// scheme
  handler, Nexus auth + Integrations dialog, mod acquisition, the update-check
  service, the mod-list update UI, and the DMF new-profile/auth install prompt.
  The app is user-usable:
  create profiles, import mods (folder/`.zip`, Nexus/GitHub/Untracked), manage
  the mod list (enable/disable/reorder/policy/remove), configure Settings
  (discovery paths + mod-repo location), and launch modded Darktide. Premium
  users additionally get per-mod one-click update on the mod list; non-premium
  users see the source-badge link + the update marker + the existing nxm
  download flow. The first time Nexus auth is configured, or whenever a new
  profile is created + set active without DMF in it, a modal prompt offers to
  add/download DMF (Darktide Mod Framework, Nexus mod 8). The Launcher is a
  stub. Backend libraries: Profiles,
  Mods (the unified mod repository), Steam, Integrations, Relay-client,
  General. Modificus Relay is a separate repo
  ([darktide-modificus-relay](https://github.com/ModifAmorphic/darktide-modificus-relay));
  this repo holds Modificus Curator only.
- **`poc`** -- historical proof-of-concept, reference only. Not built upon.
- Development is branch + PR; no unreviewed merges to `main` (reviewed +
  covered + qa'd + CI green).

## Directory structure (current `main`)

```
src/        Modificus Curator -- the mod manager app (.NET 10 + Avalonia 12)
  modificus-curator.sln   solution root (classic .sln)
  Directory.Build.props  shared MSBuild props (net10.0, nullable, implicit usings)
  ui/                   Modificus.Curator.UI -- the Avalonia executable + DI composition root
                          (shell + profile management: dropdown switch,
                          persisted active profile, create/rename/delete dialog;
                          global Preferences (theme + font scale + language)
                          via `IPreferencesService` + the i18n infrastructure: `Strings.resx`
                          + `LocalizationService` for dynamic culture switching;
                          the mod-list UI;
                          Launch wiring + Settings window +
                          discovery escape-hatch over the shared `Settings/DiscoveryField`
                          descriptor + `DiscoveryConfig`/`SteamService.Discover()` validate+heal+persist +
                          `IModRepository.Relocate/Rescan`;
                          `AddNxm()` + `StartNxmServer` (single-instance via
                          `SingleInstanceGuard` process enumeration, separate from the `Modificus.Curator.Nxm`
                          pipe bind which degrades gracefully on IOException; a second Curator exits
                          via `NxmSingleInstanceException` -> `Environment.Exit(1)` before the
                          window shows);
                          the Integrations dialog (Nexus-only) + its
                          `OpenIntegrationsCommand` on the shell (left of the profiles button),
                          wired through `IDialogService.ShowIntegrationsAsync` -> `IntegrationsViewModel`
                          -> `INexusAuthService` (OAuth loopback + API-key validate + sign-out); auth
                          controls stay usable while Darktide runs (only launch + active-profile
                          changes are blocked); the Integrations dialog also owns the explicit
                          `nxm://` handler registration (a "Nexus download links" section over
                          `INxmHandlerRegistrar`: register confirms first since it is a system-wide
                          change that can affect other mod managers; unregister only releases
                          Curator's own registration);
                          `IModAcquisitionService` (download + extract + place
                          orchestrator in Integrations) + the real `NxmModDownloadHandler` (in UI,
                          coordinating IDialogService + IProfileSession + Dispatcher.UIThread) that
                          replaces the no-op default via DI last-registration-wins, registered after
                          AddNxm() in CuratorComposition;
                          `UpdateCheckRunner` (ui/Session/) the
                          UI-layer glue that fires `IUpdateCheckService.CheckAsync`
                          fire-and-forget on profile load (startup-with-restored-id
                          + active-profile switch via IProfileSession.PropertyChanged
                          filtered to ActiveProfileId), registered + started
                          best-effort from CuratorComposition);
                          the mod-list update UI per-row update
                          signal + per-mod update button. `ModListViewModel` subscribes
                          to `IUpdateCheckService.CheckCompleted` (per-row
                          `UpdateAvailable` from `LastResult.Updates` matched by
                          ContainerId, list-level `IsRateLimited` notice + a
                          companion `IsRecentOnly`/"showing recent updates"
                          notice that fires after a Month-only check and clears
                          after a thorough one), reads
                          `INexusAuthService.GetCurrentStateAsync` once at construction
                          for the premium gate (`IsPremiumUser`, no mid-session refresh),
                          and exposes an async `UpdateCommand(row)` that calls
                          `IModAcquisitionService.AcquireLatestNexusAsync` (premium-only,
                          one-at-a-time via `AnyRowUpdating`) + an async
                          `CheckForUpdatesNowCommand` that awaits the runner's
                          thorough check (driving an `IsCheckingNow` spinner on
                          the header refresh button). The view's source badge
                          is a `HyperlinkButton` to the mod's remote page, a drawn
                          `<Ellipse>` + a `HyperlinkButton` to the mod's Nexus
                          files tab (`?tab=files`) marks flagged rows, a drawn
                          download-arrow Update button + indeterminate
                          `ProgressBar` (toggled by `IsUpdating`) live in a new
                          row column, and the rate-limit + recent-only notices
                          sit in the header. `ModItemViewModel` carries the
                          INPC state + derived `SourceUrl`/`UpdatePageUrl`/
                          `IsNexusLatest`/`CanShowUpdateButton`/`NexusModId`; a
                          `BoolAllConverter` (ui/Converters/) ANDs the row's
                          `CanShowUpdateButton` with the list VM's
                          `IsPremiumUser` for the button's `IsVisible`
                          MultiBinding. The check is split by trigger:
                          `IUpdateCheckService.CheckAsync` (Month-only, 1 API
                          call) fires on profile load + the periodic timer;
                          `IUpdateCheckService.CheckThoroughAsync` (adds a
                          per-mod `ListModFilesAsync` pass for mods the Month
                          response missed, catching mods whose latest release
                          predates the Month window) fires on the manual "check
                          now" button; both share `LastResult`/`CheckCompleted`,
                          distinguished by the result's `Thorough` flag);
                          the DMF (Darktide Mod Framework)
                          install-prompt coordinator `DmfPromptService`
                          (ui/Session/) + the modal `ProgressDialog`
                          (ui/Views/) used for its in-flight download. The
                          coordinator subscribes to
                          `IProfileService.ProfileCreated` (fires from inside
                          the ManageProfiles dialog's create) +
                          `INexusAuthService.AuthStateChanged` (fires from
                          inside the Integrations dialog's auth command),
                          records each as a pending trigger, and the shell
                          calls `ProcessPendingAsync` after those dialogs close
                          so the DMF prompt is the topmost modal at that point
                          (no dialog-on-dialog). The prompt fires for two
                          triggers when DMF is not in the active profile: (1)
                          the first time Nexus auth transitions from None to
                          configured (gated by the persisted
                          `CuratorConfig.Nexus.DmfAuthPromptShown` flag so
                          subsequent auth changes do not re-prompt), and (2)
                          every new profile that becomes active (no flag: a
                          fresh ask per profile). Three cases: DMF in the repo
                          but not the profile -> instant add (case 1); DMF not
                          in the repo + auth configured -> on confirm, premium
                          users get the in-app API download under a spinner +
                          add, non-premium users (or unknown premium state):
                          if Curator is registered as the `nxm://` handler,
                          their browser opens at DMF's Nexus files page (the
                          user clicks Download there + the handler picks up the
                          URL + adds DMF to the active profile via the standard
                          nxm flow); if Curator is not the handler, an
                          informational alert tells the user to enable nxm
                          links in Integrations or download the archive
                          manually (the API download_link endpoint is
                          premium-only, so non-premium users must visit the
                          site to mint the per-file token) (case 2); DMF not in
                          the repo + auth not configured -> informational alert
                          (case 3, only reachable from the new-profile trigger).
                          Decline is respected; DMF can be added later via the
                          normal add flow. `IDialogService.ShowProgressAsync<T>`
                          runs the supplied work under a non-closeable spinner +
                          closes it on completion; `DialogTitleBar.ShowClose`
                          (a new styled property) hides the spinner's close
                          button so the user cannot dismiss an in-flight
                          download). The shell's `ManageProfiles` command
                          brackets its `Profiles = ...` swap + `SelectedProfile
                          = ResolveActive()` re-sync under `_syncing = true`:
                          replacing the dropdown's `ItemsSource` causes the
                          ComboBox to fire spurious `SelectedItem` events (null
                          then a value match against the new collection for the
                          previously-selected name) that would otherwise land in
                          `OnSelectedProfileChanged` with the stale value +
                          revert the session via `RequestActive` (undoing the
                          active change `CommitCreate` just made inside the
                          dialog). Bracketing the swap under `_syncing` makes
                          those events no-ops)
  general/              Modificus.Curator.General -- cross-cutting infra (logging bootstrap,
                          config loader, app-state store, AddGeneral() DI ext)
  config/               Modificus.Curator.Config -- the CuratorConfig schema + defaults (POCO),
                        including the NexusConfig slot under Integrations
                        (AuthMethod {None,OAuth,ApiKey}, ApiKey, OAuth tokens, base URLs)
  profiles/             Modificus.Curator.Profiles -- profile data model, persistence,
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
  mods/          Modificus.Curator.Mods -- the unified mod repository
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
                        per-entry extraction with AssertSafePath guard; AddVersion
                        stages extraction into a temp dir + atomically swaps on
                        success so failed re-imports are non-destructive; validates the
                        source has exactly one base dir with a matching <base>.mod +
                        preserves the base folder under <versionFolder>/<base>/;
                        exposes GetBaseName + FindExistingContainer peeks for the
                        collision block).
  integrations/         Modificus.Curator.Integrations -- GitHub Releases client
                        (IGitHubClient: ListReleases/GetLatestRelease/DownloadAssetAsync
                        via IHttpClientFactory, typed exceptions, optional PAT)
                        + the Nexus Mods v1 client + auth
                        (INexusClient over the v1 REST endpoints with per-request
                        auth via INexusAuthMessageFactory selector -- ApiKey /
                        OAuth / None factories, the latter doing 401-reactive
                        refresh; NexusAuthService the OAuth loopback + API-key
                        validate + sign-out orchestrator (raises
                        AuthStateChanged on every persisted method change so
                        the UI's DmfPromptService can react to the
                        None -> configured transition); NexusOAuthTokenStore
                        owns the OidcClient + token persistence; LoopbackBrowser
                        the IBrowser impl with an HttpListener on an ephemeral
                        port; Duende.IdentityModel.OidcClient 7.1.0 for the
                        OAuth machinery; client_id is the build-time const
                        "modificus-curator";
                        IModAcquisitionService the download +
                        extract + place orchestrator over INexusClient +
                        IModImportService + a plain HttpClient for the CDN
                        download; AcquireFromNexusAsync resolves the download
                        links, fetches name + version metadata, downloads to
                        temp, then imports via IModImportService.Import;
                        AcquireLatestNexusAsync resolves the newest
                        non-archived MAIN file via ListModFilesAsync then forwards
                        to AcquireFromNexusAsync with null nxm tokens (premium
                        path); ModFile gains an `archived` bool for the filter;
                        IUpdateCheckService the Nexus-only
                        update-check service (1 ModUpdatesAsync call per check,
                        intersected with the profile's LatestPolicy+NexusSource
                        mods; compares LatestFileUpdateUtc against the imported
                        version's RemoteUploadedAt (with an ImportedAt fallback
                        for versions imported before that field existed); the
                        publish-date basis, not ImportedAt, is what catches an
                        outdated install re-acquired today (ImportedAt = now
                        would mask it); rate-limit-aware with the all-zero
                        Unknown guard; LastResult + CheckCompleted event for
                        the mod-list badges; Integrations now references
                        Profiles, acyclic, for IProfileService.GetModList)
  steam/                Modificus.Curator.Steam -- Steam + Darktide + Proton discovery
                        (multi-library + compatdata), IsGameRunning (WinProcessLookup
                        via process comm on Windows; LinuxProcessLookup via /proc
                        argv[0] under Proton -- selected once by DI), injectable seams
  relay-client/         Modificus.Curator.RelayClient -- the v1 launch façade
                        (IRelayLaunchService.Launch → LaunchResult; Windows: direct
                        launcher Process.Start; Linux: proton run with both STEAM_COMPAT_*
                        env + Z:\-translated paths)
  launcher/             Modificus.Curator.Launcher -- stub (the Steam non-steam-shortcut
                          target placeholder)
  nxm/                  Modificus.Curator.Nxm -- the nxm:// scheme-handler plumbing:
                        NxmUrlParser (mod-download / oauth-callback /
                        collection URL types), NxmIpcFraming (length-prefixed UTF-8 frames),
                        SingleInstanceGuard (the process-enumeration single-instance check,
                        with an injectable enumerator seam), NxmIpcServer (the named-pipe
                        server; Bind runs two SEPARATE checks: SingleInstanceGuard first
                        (fatal NxmSingleInstanceException on collision), then the pipe bind
                        which degrades gracefully on IOException; accept loop Disconnects
                        between clients), INxmRouter + no-op INxmModDownloadHandler
                        default (the real handler is registered via AddSingleton
                        last-wins, in CuratorComposition after AddNxm()), the OS
                        scheme-handler registrar
                        (INxmHandlerRegistrar: WindowsNxmHandlerRegistrar writes
                        HKCU\Software\Classes\nxm; LinuxNxmHandlerRegistrar writes a .desktop
                        file + xdg-mime default), + NxmHandlerRelay (the testable core the
                        handler exe calls: hot-path IPC delivery + cold-start launch+retry,
                        UseShellExecute=false on both OSes). AOT-friendly (IsAotCompatible;
                        only raw byte/UTF-8 IO in the handler path).
  nxm-handler/          Modificus.Curator.NxmHandler -- the OS-registered nxm:// scheme handler
                        (console exe, native AOT). Program.cs is one line: NxmHandlerRelay.RunAsync.
                        Forwards the raw URL to running Curator over the fixed pipe, or (cold start)
                        launches Curator (no args) + retries the pipe ~250ms/30s, then delivers.
  tests/
    Modificus.Curator.General.Tests/         xUnit tests for the general library
    Modificus.Curator.Profiles.Tests/        xUnit tests for the profiles library (incl. staging)
    Modificus.Curator.Mods.Tests/      xUnit tests for the mod repository + import
    Modificus.Curator.Integrations.Tests/    xUnit tests for the GitHub Releases client
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
    Modificus.Curator.Steam.Tests/           xUnit tests for discovery + IsGameRunning
    Modificus.Curator.RelayClient.Tests/ xUnit tests for the launch façade (dual-purpose:
                                            `dotnet test` = xUnit; `dotnet run` = composition smoke harness)
    Modificus.Curator.UI.Tests/              xUnit tests for the shell + manage-profiles
                                            view models (profile CRUD/switch, active-profile
                                            persist, switch-blocked-while-running; dialog via
                                            an injectable IDialogService seam; + the
                                            NxmModDownloadHandler auth/profile gates + error
                                            wiring + the mod-list update flow: CheckCompleted
                                            per-row state, UpdateCommand success/failure +
                                            one-at-a-time + premium gating + SourceUrl resolution;
                                            + the DmfPromptService (the three DMF cases, the
                                            new-profile + auth-configured triggers, the
                                            ask-once auth flag, the decline path, and the
                                            dialog-on-dialog avoidance), against in-memory fakes)
    Modificus.Curator.Nxm.Tests/             xUnit tests for the nxm library (parser, framing,
                                            IPC server resilience, SingleInstanceGuard, router,
                                            relay helper, Linux registrar, AddNxm wiring;
                                            serialized via DisableTestParallelization since
                                            real named pipes are an OS-level shared resource)
docs/               architecture/ + reference/ (src/ per-library API refs + the release strategy reference)
scripts/            release.env: the install manifest (RELEASE_URL +
                    PRE_RELEASE_URL Linux x64 asset URLs), written by the release
                    workflow's update-manifest job; install.sh: the Linux installer
                    served from raw/main (stable by default, prerelease opt-in via
                    --prerelease or CURATOR_PRERELEASE=1; resolves the archive from
                    scripts/release.env rather than querying the GitHub API; installs
                    into ${XDG_DATA_HOME:-$HOME/.local/share}/Modificus Curator/;
                    replaces only app/ + relay/, never the user-data root; symlinks the
                    UI into ~/.local/bin/modificus-curator). Testing overrides:
                    INSTALL_ROOT / BIN_LINK / CURATOR_REPO / CURATOR_ARCHIVE (local tar.gz
                    in place of the download, for offline extraction tests).
.github/workflows/  curator-build (the PR gate: an Ubuntu-only format job
                    auto-commits `dotnet format` as `style: dotnet format [skip ci]`
                    for same-repo PRs, verify-only for fork PRs and workflow_dispatch;
                    build + test on a Windows/Ubuntu matrix depends on the format
                    job; no artifact upload; release-please-only PRs are ignored via
                    paths-ignore; there is intentionally no push trigger),
                    release (release-please cuts the release, then per-target jobs publish
                    framework-dependent unsigned bundles as curator-<tag>-<platform>-x64.{zip,tar.gz}
                    with a top-level app/ + relay/ layout, bundle the latest Relay release
                    (prereleases included), upload a GitHub Artifact Attestation per asset via
                    actions/attest@v4, then repository_dispatch the post-release workflow; an
                    update-manifest job (after build-linux, gated on releases_created + build-linux
                    success) rewrites the matching var in scripts/release.env (RELEASE_URL for a
                    stable release, PRE_RELEASE_URL for a prerelease, selected by the release's
                    prerelease flag; the Linux tar.gz asset resolved from the release by
                    content_type==application/x-gtar) and commits it as
                    "chore(release): update install manifest [skip ci]"; the UI publish
                    targets win-x64 and linux-x64 RIDs with --self-contained false to filter native libs, and an
                    AfterTargets=Publish target strips all .pdb files so the bundles carry no debug symbols), and
                    curator-post-release-av (repository_dispatch event_type curator-release-assets-published,
                    or manual workflow_dispatch; scans the published bytes with PowerShell
                    Start-MpScan Defender scan and VirusTotal, classifies Defender results
                    explicitly as clean/detection/tool_error, submits to VirusTotal via the
                    pinned crazy-max/ghaction-virustotal@936d8c5c00afe97d3d9a1af26d017cfdf26800a2
                    action with request_rate 4, requires VIRUSTOTAL_API_KEY,
                    fails on Defender tool errors, missing Defender, VT errors, or missing VT key,
                    creates a GitHub issue with title "AV manual review for release <tag>" when VT
                    upload succeeds and returns analysis links; deduplicates against existing open
                    issues with the same title; still post-release and non-gating for publication,
                    but red means scan signal invalid or VT upload failed)
.release-please-config.json   release-please config (release-type simple, include-component-in-tag false, prerelease true)
.release-please-manifest.json release-please version manifest (the source-of-truth version; no csproj Version metadata)
.gitignore          ignores .NET bin/obj, build artifacts, _local/
```
## Modificus Curator ops

Build + test the mod-manager app -- run from the repo root (.NET 10 SDK required):
```sh
dotnet build src/modificus-curator.sln --configuration Release
dotnet test  src/modificus-curator.sln --configuration Release
dotnet run   --project src/ui --configuration Release   # app shell window
```
- The composition root is `src/ui/CuratorComposition.cs` (loads
  config → builds the Serilog logger → wires every `Add<Library>()` → runs the
  startup `ModCleanup.PruneUnreferenced` pass + the startup
  `ISteamService.Discover()` validate/heal/persist pass).
- **Config** is `CuratorConfig` (`src/config/`) -- defaults under the
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
  **Relay-client** (the launch
  façade), **Mods** (the unified `IModRepository`: UUID containers per
  (source, identity), opaque-ID version subfolders, per-container
  `container.json` manifests, in-memory index rebuilt from a scan,
  `PruneUnreferenced` GC; the version-policy model `ModVersionPolicy`; the
  mod-source provenance model `ModSource`
  (`UntrackedSource`/`NexusSource`/`GitHubSource`) + `ModSourceParser`; the
  local-import service `IModImportService`). **General** carries cross-cutting
  infra: logging, `ConfigLoader`, and `AppStateStore` (the active-profile id,
  persisted to `app-state.json`). The UI includes the shell + profile
  management (with an `IProfileSession` (ui/) as the single authority for the
  active profile, the switch-block gate, and the live running-state), global
  Preferences + i18n infrastructure, the mod-list UI (view mods with
  source/version badges, enable/disable, remove-with-confirm, reorder, per-mod
  Latest/Pinned policy, auto-sort identity stub, and local folder/`.zip` import
  via file picker + drag-and-drop, joined to containers via `IModRepository` by
  `ContainerId`), and Launch (`LaunchCommand` -> `IRelayLaunchService.Launch`
  -> branch on `LaunchResult.Status` (`Launched` -> status note + immediate
  `IsGameRunning` refresh; `DiscoveryIncomplete` -> the focused discovery
  escape-hatch modal over the shared `DiscoveryField` descriptor; `Error` ->
  modal alert) + a Settings window editing `CuratorConfig.Discovery` user
  overrides (per-field read-modify-save) + `ModsFolder` live-relocate via the
  atomic `IModRepository.Relocate` over the `DiscoveryConfig` +
  `SteamService.Discover()` validate+heal+persist pipeline). The DMF (Darktide
  Mod Framework) install-prompt coordinator `DmfPromptService` (ui/Session/)
  offers to add/download DMF on (1) the first Nexus auth None -> configured
  transition (gated by the persisted `CuratorConfig.Nexus.DmfAuthPromptShown`
  flag) + (2) every new profile that becomes active without DMF in it; the
  prompt is a modal on the main window, fired by the shell after the
  triggering ManageProfiles / Integrations dialog closes so it never nests on
  top of one. The **Launcher** is a stub. See
  `docs/architecture/MODIFICUS-CURATOR.md`.

## Key docs

- `docs/architecture/` -- the Modificus Curator architecture (component model,
  the Relay contract Curator consumes, profiles, launch).
- `docs/reference/src/` -- per-library API reference for the Modificus
  Curator backend libraries.
- [darktide-modificus-relay](https://github.com/ModifAmorphic/darktide-modificus-relay) --
  Modificus Relay (architecture, build, game-binary reference, mod
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
- **No `ConfigureAwait(false)` in UI-layer code.** It hops async continuations
  to the threadpool, breaking UI-thread affinity for `Window.ShowDialog`,
  `ObservableCollection` mutations, and `INotifyPropertyChanged` setters. The UI
  layer's convention is to stay on the captured UI context (no
  `ConfigureAwait(false)`). Only explicit background-task code uses it (e.g.
  `UpdateCheckRunner` inside a `Task.Run`), and only inside that block. This has
  bitten the project repeatedly (the Update command, LoadPremiumStateAsync, the
  CheckCompleted handler, and the DmfPromptService all shipped with it + had to
  be caught at review).
- **PR descriptions describe ONLY what was done.** Never include an "Out of
  scope" section or any list of things the PR did not do. A PR description is a
  record of the change that landed, not a contrast against everything that could
  have; listing non-actions is noise that does not help a reviewer evaluate the
  diff. State what changed + why; stop there.
- **Don't surface implementation-detail questions to the operator without
  context.** When asking the operator to weigh in, lead with the actual problem
  in plain terms (what user-visible or operational behavior is at stake), not a
  narrow implementation detail. Decide internal plumbing yourself (anything
  where the options give identical UX) and flag it in your report; do not ask.
  Reserve surfaced questions for genuine forks: irreversible choices, UI design
  shape, external dependencies, or trade-offs with real user-facing
  consequences. Test: if you cannot write a one-sentence, non-plumbing "why I am
  asking the operator this specific question," do not ask it.
- **AGENTS.md (this file) tweaks ride in the current PR.** Convention
  fine-tuning does not need its own docs PR; update AGENTS.md as part of
  whatever work is in flight.

## Naming convention

Keep the established thematic name, **Curator** (the app), for user-facing UI
surfaces (Modificus Curator). Use plain, descriptive names for code components
(libraries, modules, types, functions). Reserve Warhammer 40k / Adeptus
Mechanicus flavor for the UI; docs and code read as plain engineering
documentation.

- **Folders/filenames:** lowercase.
- **Prose/docs:** "Modificus Curator" is the app's public name; "Modificus
  Relay" / "Relay" refers to the separate runtime repo
  ([darktide-modificus-relay](https://github.com/ModifAmorphic/darktide-modificus-relay)).
- Don't obscure: names should be descriptive and accessible, not cryptic.

## README pattern

Docs follow a two-tier README pattern:

- **Root `README.md`** -- audience is the **general / end user**: what Curator is,
  its components, and how to get it running. **No build internals.**
- **Component-dir `README.md`** (e.g. `src/README.md`) -- audience is
  **developers / power users**: build instructions, sub-component details,
  testing, links to the architecture specs.

The **root README links to** the component READMEs -- it does **not** duplicate
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
- **Component-dir `README.md`** (e.g. `src/README.md`): for
  build/dev detail under that component; ensure the root links to it.
- **`docs/architecture/`** for any architecture change.
- **`docs/reference/src/`**: per-library API reference. When a Modificus
  Curator library's public surface, key types, or DI registration changes,
  update its `docs/reference/src/<library>.md` in the same PR.

Then ensure the Modificus Curator build + tests pass
(`dotnet build`/`dotnet test src/modificus-curator.sln`). **Outdated
docs in a PR are a review blocker**, including this file.

**No project phase/stage labels in committed docs or code comments.** Docs,
reference + architecture material, and code comments describe the current system
as it is, not how or when it got built. Do not write things like
"(Phase 4 Stage 4)", "new in Phase 3", or "Stage 5 adds..." in any committed
prose or comment. Those are project-management milestones, meaningless to a
reader of the current state and quickly stale to us too once the phase ships.
Describe the feature/architecture directly. If a phasing concept is genuinely
architectural (e.g. a phase *of a process* the code performs, like "the
discovery phase of launch"), that's fine; what's not fine is referencing the
build's project phases/stages. Planning history lives in `_local/` + the git
log, not in the docs or the code.
