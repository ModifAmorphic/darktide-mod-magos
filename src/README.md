# Modificus Curator

The mod-manager application -- the user-facing layer on
top of Modificus Relay. It owns profiles, mod staging, load order,
dependency resolution, mod-source integrations (Nexus Mods, GitHub Releases,
Steam), and the "Launch Darktide" button that invokes the Relay launcher.
Target architecture:
[`../docs/architecture/MODIFICUS-CURATOR.md`](../docs/architecture/MODIFICUS-CURATOR.md).

## Tech stack

- **.NET 10 (LTS)** -- target framework `net10.0`.
- **Avalonia 12** -- native cross-platform UI.
- **CommunityToolkit.Mvvm** -- MVVM (source generators).
- **Microsoft.Extensions.DependencyInjection** -- DI.
- **Microsoft.Extensions.Logging + Serilog** -- structured logging (console +
  file sinks), config-honored level/file.
- **Microsoft.Extensions.Configuration** -- JSON config → `CuratorConfig`.
- **xUnit** -- tests.

## Project layout

```
src/
  modificus-curator.sln            solution root
  Directory.Build.props           shared MSBuild properties (net10.0, nullable)
  config.example.json             sample global config (schema reference)
  ui/                             Modificus.Curator.UI       Avalonia executable + DI composition root
                                                            (shell + profiles, Preferences, mod-list UI,
                                                            Launch + Settings)
  general/                        Modificus.Curator.General  cross-cutting infra: logging, config loader,
                                                            app-state store, DI
  config/                         Modificus.Curator.Config   the CuratorConfig schema + defaults (POCO)
  profiles/                       Modificus.Curator.Profiles          profile data + lifecycle + container-based staging
  mods/                           Modificus.Curator.Mods              unified mod repository + version-policy + source models + import
  integrations/                   Modificus.Curator.Integrations      GitHub Releases client + Nexus v1 client/auth + mod acquisition + update check
  steam/                          Modificus.Curator.Steam             Steam/Darktide/Proton discovery + IsGameRunning
  relay-client/                   Modificus.Curator.RelayClient       the launch façade
  launcher/                       Modificus.Curator.Launcher          stub (Steam non-steam-shortcut target placeholder)
  tests/
    Modificus.Curator.General.Tests/         xUnit tests for the general library
    Modificus.Curator.Profiles.Tests/        xUnit tests for the profiles library (incl. staging)
    Modificus.Curator.Mods.Tests/            xUnit tests for the mod repository + import
    Modificus.Curator.Integrations.Tests/    xUnit tests for the GitHub Releases client
    Modificus.Curator.Steam.Tests/           xUnit tests for discovery + IsGameRunning
    Modificus.Curator.RelayClient.Tests/     xUnit tests for the launch façade (dual-purpose: dotnet test / dotnet run smoke harness)
    Modificus.Curator.UI.Tests/              xUnit tests for the shell + manage-profiles + mod-list view models
```

Each library exposes an `Add<Library>()` extension method on
`IServiceCollection`; the UI composition root (`ui/CuratorComposition.cs`) calls
them all. See the architecture doc for each library's domain responsibility.

## Build

Requires the **.NET 10 SDK**. From the repo root:

```sh
dotnet build src/modificus-curator.sln --configuration Release
```

## Run

```sh
dotnet run --project src/ui --configuration Release
```

The window shows the top bar (app title, profile dropdown + "Manage profiles…"
gear, Launch Darktide) and the status strip (Darktide running indicator). The
profile dropdown switches the active profile (persisted across restarts via
`IAppStateStore`); "Manage profiles…" opens the create / rename / delete dialog.
The startup log lines (`Modificus Curator starting`, `Config loaded …`) go to the
console and to the configured log file.

## Test

```sh
dotnet test src/modificus-curator.sln --configuration Release
```

## CI and releases

The PR gate (`.github/workflows/curator-build.yml`) runs on `pull_request`
targeting `main` (and `workflow_dispatch`). There is intentionally no `push`
trigger; the release workflow handles push-to-main. It runs an Ubuntu-only
format job, then `dotnet build` and `dotnet test` on a Windows + Ubuntu
matrix that depends on the format job. For same-repo PRs the format job
runs `dotnet format` and commits any changes as
`style: dotnet format [skip ci]`; for fork PRs and `workflow_dispatch` it
runs `dotnet format --verify-no-changes`. `paths-ignore` skips release-please's
bot-authored release PRs. No build artifact is uploaded; release assets are
produced by the release workflow instead.

Releases are cut by `release-please` (`.release-please-config.json` +
`.release-please-manifest.json` at the repo root; tag style `v0.1.0`, no
component prefix). When release-please creates a release, the release workflow
publishes each target as a framework-dependent, unsigned bundle, fetches the
latest Modificus Relay release (prereleases included), and uploads a GitHub
Artifact Attestation against each asset. Verify an asset's provenance with:

```
gh attestation verify <file> --repo ModifAmorphic/darktide-modificus-curator
```

The release archive layout is two top-level folders, extracted into the default
app-data root (`%LOCALAPPDATA%\Modificus Curator` on Windows,
`~/.local/share/Modificus Curator` on Linux):

- `app/` - the Curator UI + the `nxm://` handler (+ the launcher stub, pending
  later cleanup).
- `relay/` - the bundled Relay runtime, which seeds the default `RelayDir`.

A separate post-release workflow (triggered by `repository_dispatch` from the
release workflow, also runnable on manual `workflow_dispatch`) scans the
published bytes (Windows Defender `MpCmdRun` + a VirusTotal submission) and
opens a tracking issue on a hit. It is operator signal only; nothing it finds
gates a release. Releases created with `GITHUB_TOKEN` do not fire
`release: published`, which is why the AV/VT workflow runs on
`repository_dispatch` instead.

The Linux install script (`scripts/install.sh`, served from `raw/main`) installs
the latest release into `${XDG_DATA_HOME:-$HOME/.local/share}/Modificus Curator/`.
See the root [`README.md`](../README.md) for the user-facing install steps.

## Storage model (unified repository)

Mods are stored **once, in a unified repository** keyed by one UUID container per
`(source-type, identity)`, holding opaque-ID version subfolders indexed by a
per-container `container.json` manifest. Profiles reference a mod by
`(containerId, policy)` and store no mod files of their own. **Symlinks** project
the resolved set into the staged mod root at launch (store once, symlink;
copying would duplicate repository files). On-disk layout:

```
<ModsFolder>/                  # the mod repository root (CuratorConfig)
  <containerUUID>/                   # one container per (source, identity)
    container.json                   # { id, source, name, versions: [{ folder, versionString, isLatest, importedAt }] }
    <versionFolder>/                 # opaque-ID version subfolder; the mod files for that version
<ProfilesBaseFolder>/<guid>/
  profile.json                       # metadata + mod list (entries carry ContainerId + Policy)
  staged/                            # the staged mod root = the --mod-path (REGENERATED each launch)
    <baseName>                       #   symlink → <versionFolder>/<baseName>/ (Latest → isLatest; Pinned(versionId) → matching Folder); the base name, not the container display name
    mods.lst                         #   successfully-staged enabled mods, in order
```

`Profiles` owns the staging seam (`ProfileService.PrepareModRoot` clears +
rebuilds `staged/`, discovering each enabled mod's base folder name inside its
resolved version folder, then writes `mods.lst`); `Mods` owns the repository
(`IModRepository`) + the version-policy model + the source model + the
local-import service. Version resolution at stage time is by policy: Latest →
the container's `isLatest` version; Pinned(vId) → the version whose `Folder`
matches the pin's versionId (the profile references a version by id, not by
tag, so the repo stays the sole source of truth for version details). See
[`../docs/architecture/MODIFICUS-CURATOR.md`](../docs/architecture/MODIFICUS-CURATOR.md)
(Mod repository).

## Configuration

One global config file (JSON), loaded by
`general/ConfigLoader.cs` onto a fully-defaulted `CuratorConfig`. The default
location is `<app-data>/Modificus Curator/config.json`
(`%LOCALAPPDATA%` on Windows, `~/.local/share` on Linux). Every field has a
platform-appropriate default, so the app runs with no file present; specify only
what you want to override. See [`config.example.json`](./config.example.json)
for the schema.

| Field                  | Default                                         |
| ---------------------- | ----------------------------------------------- |
| `Logging:Level`        | `Information`                                   |
| `Logging:LogFile`      | `<app-data>/Modificus Curator/logs/curator.log`     |
| `ProfilesBaseFolder`   | `<app-data>/Modificus Curator/profiles`           |
| `ModsFolder`           | `<app-data>/Modificus Curator/mods`               |
| `RelayDir`             | `<app-data>/Modificus Curator/relay`              |

Per-profile settings live with the profile, not in the global config.
