# Magos Modificus

The mod-manager application for darktide-mod-magos — the user-facing layer on
top of the Enginseer runtime. It owns profiles, mod staging, load order,
dependency resolution, mod-source integrations (Nexus Mods, GitHub Releases,
Steam), and the "Launch Darktide" button that invokes the Enginseer launcher.

> **Status: Phases 0–2 complete; Phase 3 Track A in progress.** The foundation +
> all backend libraries are implemented: Profiles, Steam, Integrations,
> Enginseer-client (Phase 1) + SharedMods (Phase 2). Phase 3 Track A wires the
> UI: milestone 1 landed the app shell (top bar + status strip, live
> profile/game-running state) and milestone 2 makes the profile controls work
> (dropdown switch + persisted active profile + a "Manage profiles…" create /
> rename / delete dialog, switch-blocked-while-running). Mod-list UI (Track B)
> and Launch behavior (Track C) are still pending; the Launcher is a stub
> (Phase 5). Target architecture:
> [`../docs/architecture/MAGOS-MODIFICUS.md`](../docs/architecture/MAGOS-MODIFICUS.md).

## Tech stack

- **.NET 10 (LTS)** — target framework `net10.0`.
- **Avalonia 12** — native cross-platform UI.
- **CommunityToolkit.Mvvm** — MVVM (source generators).
- **Microsoft.Extensions.DependencyInjection** — DI.
- **Microsoft.Extensions.Logging + Serilog** — structured logging (console +
  file sinks), config-honored level/file.
- **Microsoft.Extensions.Configuration** — JSON config → `MagosConfig`.
- **xUnit** — tests.

## Project layout

```
magos-modificus/
  magos-modificus.sln            solution root
  Directory.Build.props           shared MSBuild properties (net10.0, nullable)
  config.example.json             sample global config (schema reference)
  ui/                             Magos.Modificus.UI       Avalonia executable + DI composition root
                                                            (Phase 3 Track A: shell + profile management)
  general/                        Magos.Modificus.General  cross-cutting infra: logging, config loader,
                                                            app-state store, DI
  config/                         Magos.Modificus.Config   the MagosConfig schema + defaults (POCO)
  profiles/                       Magos.Modificus.Profiles          implemented (Phase 1 + Phase 2 staging)
  shared-mods/                    Magos.Modificus.SharedMods        implemented (Phase 2)
  integrations/                   Magos.Modificus.Integrations      implemented (Phase 1 — GitHub Releases client)
  steam/                          Magos.Modificus.Steam             implemented (Phase 1 — discovery + IsGameRunning)
  enginseer-client/               Magos.Modificus.EnginseerClient   implemented (Phase 1 — launch façade)
  launcher/                       Magos.Modificus.Launcher          stub (slim Steam-shortcut launcher; Phase 5)
  tests/
    Magos.Modificus.General.Tests/         xUnit tests for the general library
    Magos.Modificus.Profiles.Tests/        xUnit tests for the profiles library (incl. staging)
    Magos.Modificus.SharedMods.Tests/      xUnit tests for the shared-mod store + allocation
    Magos.Modificus.Integrations.Tests/    xUnit tests for the GitHub Releases client
    Magos.Modificus.Steam.Tests/           xUnit tests for discovery + IsGameRunning
    Magos.Modificus.EnginseerClient.Tests/ xUnit tests for the launch façade (dual-purpose: dotnet test / dotnet run smoke harness)
    Magos.Modificus.UI.Tests/              xUnit tests for the shell + manage-profiles view models
```

Each library exposes an `Add<Library>()` extension method on
`IServiceCollection`; the UI composition root (`ui/MagosComposition.cs`) calls
them all. See the architecture doc for each library's domain responsibility.

## Build

Requires the **.NET 10 SDK**. From the repo root:

```sh
dotnet build magos-modificus/magos-modificus.sln --configuration Release
```

## Run

```sh
dotnet run --project magos-modificus/ui --configuration Release
```

The window shows the top bar (app title, profile dropdown + "Manage profiles…"
gear, Launch Darktide) and the status strip (Darktide running indicator). The
profile dropdown switches the active profile (persisted across restarts via
`IAppStateStore`); "Manage profiles…" opens the create / rename / delete dialog.
The startup log lines (`Magos Modificus starting`, `Config loaded …`) go to the
console and to the configured log file.

## Test

```sh
dotnet test magos-modificus/magos-modificus.sln --configuration Release
```

## Storage model (shared-first)

Mods are stored **shared-first** across profiles — a profile uses the global
shared copy when its version policy is compatible, and takes a profile-local
(diverged) copy only when policies diverge. **Symlinks** project the resolved
set into the staged mod root at launch (download once, store once — copying
would defeat the shared-mod purpose). On-disk layout:

```
<SharedModsFolder>/                  # the global shared store (MagosConfig)
  shared-manifest.json               # ISharedModStore: [{Name, Policy, ActualVersion, Path}]
  <mod-name>/                        # the shared mod files (one copy, shared)
<ProfilesBaseFolder>/<guid>/
  profile.json                       # metadata + mod list (entries carry a Policy)
  mods/<mod-name>/                   # a profile's diverged copy of a mod (Phase 4 acquires)
  staged/                            # the staged mod root = the --mod-path (REGENERATED each launch)
    <mod-name>                       #   symlink → shared <mod-name> OR mods/<mod-name>
    mods.lst                         #   successfully-staged enabled mods, in order
```

`Profiles` owns the staging seam (`ProfileService.PrepareModRoot` clears +
rebuilds `staged/`, then writes `mods.lst`); `SharedMods` owns the shared
manifest + the version-policy model + the allocation logic. Allocation
(Share/Diverge) is by policy **intent**, not current version — see
[`../docs/architecture/MAGOS-MODIFICUS.md`](../docs/architecture/MAGOS-MODIFICUS.md)
(Shared mod storage).

## Configuration

One global config file (JSON), loaded by
`general/ConfigLoader.cs` onto a fully-defaulted `MagosConfig`. The default
location is `<app-data>/Magos Modificus/config.json`
(`%LOCALAPPDATA%` on Windows, `~/.local/share` on Linux). Every field has a
platform-appropriate default, so the app runs with no file present; specify only
what you want to override. See [`config.example.json`](./config.example.json)
for the schema.

| Field                  | Default                                         |
| ---------------------- | ----------------------------------------------- |
| `Logging:Level`        | `Information`                                   |
| `Logging:LogFile`      | `<app-data>/Magos Modificus/logs/magos.log`     |
| `ProfilesBaseFolder`   | `<app-data>/Magos Modificus/profiles`           |
| `SharedModsFolder`     | `<app-data>/Magos Modificus/shared-mods`        |
| `EnginseerRuntimeDir`  | `<app-data>/Magos Modificus/enginseer`          |

Per-profile settings live with the profile, not in the global config.
