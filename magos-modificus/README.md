# Magos Modificus

The mod-manager application for darktide-mod-magos — the user-facing layer on
top of the Enginseer runtime. It owns profiles, mod staging, load order,
dependency resolution, mod-source integrations (Nexus Mods, GitHub Releases,
Steam), and the "Launch Darktide" button that invokes the Enginseer launcher.

> **Status: Phase 0 scaffold.** The project layout, DI composition, structured
> logging, global config schema/loader, and a bare UI window are in place.
> Library implementations come in later phases — Profiles is implemented
> (Phase 1); the other domain libraries are currently stubs (interfaces + DI
> registration only). Target architecture:
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
  general/                        Magos.Modificus.General  cross-cutting infra: logging, config loader, DI
  config/                         Magos.Modificus.Config   the MagosConfig schema + defaults (POCO)
  profiles/                       Magos.Modificus.Profiles          implemented (Phase 1)
  integrations/                   Magos.Modificus.Integrations      stub
  steam/                          Magos.Modificus.Steam             stub
  enginseer-client/               Magos.Modificus.EnginseerClient   stub (launch façade)
  launcher/                       Magos.Modificus.Launcher          stub (slim Steam-shortcut launcher)
  tests/
    Magos.Modificus.General.Tests/  xUnit tests for the general library
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

The bare Phase-0 window displays the loaded config values, and the startup log
lines (`Magos Modificus starting`, `Config loaded …`, `DI wired …`) go to the
console and to the configured log file.

## Test

```sh
dotnet test magos-modificus/magos-modificus.sln --configuration Release
```

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
