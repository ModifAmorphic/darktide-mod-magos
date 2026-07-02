# Enginseer-client (`Magos.Modificus.EnginseerClient`) — reference

> The v1 launch façade over the Enginseer runtime. Resolves the profile + Steam
> discovery, assembles the launcher args, and invokes `magos_launcher.exe` —
> directly on Windows, under `proton run` on Linux. Fire-and-forget in v1: it
> starts the launcher and returns; it does not track the game process. Status:
> implemented (Phase 1).

## Public surface

### `IEnginseerLaunchService`

```csharp
public interface IEnginseerLaunchService
{
    LaunchResult Launch(Guid profileId);
}
```

`Launch(profileId)` always returns a `LaunchResult` — it never throws for
expected conditions:

- Resolves Steam discovery first (`ISteamService.Discover()`). If discovery is
  missing required fields for the current OS, returns `DiscoveryIncomplete`
  **without** writing the profile's mod root (no point writing `mods.lst` for a
  launch that won't happen) — `MissingDiscoveryFields` lists them.
- Prepares the mod root (`IProfileService.PrepareModRoot(profileId)` — writes
  `mods.lst` and returns the `--mod-path`). An unknown profile (`KeyNotFoundException`
  from PrepareModRoot) is caught and mapped to `Error`.
- Checks that the launcher exists at `<EnginseerRuntimeDir>/magos_launcher.exe`.
- Assembles the launcher args and spawns the launcher via `IProcessLauncher`
  (directly on Windows; under `proton run` on Linux).

```csharp
public sealed record LaunchResult(
    LaunchStatus Status,
    string? Message,                         // populated for Error; null otherwise
    IReadOnlyList<string> MissingDiscoveryFields);  // populated only for DiscoveryIncomplete

public enum LaunchStatus { Launched, DiscoveryIncomplete, Error }
```

- `Launched` — the launcher process started (fire-and-forget; no game-process tracking in v1).
- `DiscoveryIncomplete` — discovery is missing required fields; the field names
  mirror `DiscoveryResult` properties so the UI can map them to a prompt.
- `Error` — unknown profile, missing runtime dir, or process-start failure; see
  `Message`.

`MissingDiscoveryFields` is derived from the `DiscoveryResult` fields directly
(both platforms need Steam + the game binary; Linux additionally needs compatdata
+ Proton), so it and `DiscoveryStatus` cannot diverge — it is equivalent to
`Status != Complete`.

### Injectable seams

- `IProcessLauncher` — `Start(filePath, arguments, environmentVariables) → bool`
  (fire-and-forget; `true` if started, `false` if it could not start — never
  throws). Abstracted so the launch path is deterministic and mockable in tests
  (the real `Process.Start` would spawn a real process). The default
  `ProcessLauncher` uses `ProcessStartInfo.ArgumentList` (argv-correct, no shell,
  no injection surface) and applies env overrides directly to the child's
  environment block.
- `LaunchPlatform` (internal enum `Windows` / `Linux`) — resolved once from the
  runtime OS via `RuntimeInformation`; tests force it via an internal constructor
  to exercise both branches on any CI OS.

`WinePath.ToWine(posixPath)` (internal) translates an absolute POSIX path to its
Wine `Z:\` form (`/` → `\`, `Z:` prefix) for the launcher-under-Wine flags.

## Cross-platform notes

The launch path branches on `LaunchPlatform` (decided once at construction; the
OS does not change at runtime):

### Windows — direct invocation

`Process.Start(magos_launcher.exe, args)`. No Proton, no path translation —
native Windows paths. Args: `--game-binary`, `--mod-path`, `--log-file`
(verbatim, untranslated). No environment-variable overrides.

### Linux — native Magos + Proton-at-launch

Magos runs natively (not Proton-wrapped); `magos_launcher.exe` is a Windows
binary, so Magos invokes it under **Proton**, using **Darktide's own compatdata**
as the prefix (required — the launcher `CreateProcess`es Darktide, so the two
must share the prefix). Proton reads `STEAM_COMPAT_DATA_PATH` from the
environment *before* the launcher runs; it cannot be passed as a launcher flag,
so Magos sets it in the environment when it invokes Proton.

Command: `<proton> run <launcher.exe> <args>`, where:

- The `proton` command + the launcher.exe path are **native Linux paths**
  (Proton resolves the `.exe` from a native path).
- The launcher's *own* path-valued flags (`--game-binary`, `--mod-path`,
  `--log-file`) are **`Z:\`-translated** (the launcher runs under Wine and needs
  Windows paths) — including `--log-file`, otherwise `magos_enginseer.log`
  couldn't be written where Magos expects.
- Environment: `STEAM_COMPAT_DATA_PATH = <compatdata>` (the Wine prefix) +
  `STEAM_COMPAT_CLIENT_INSTALL_PATH = <steam-install>` — both required for Proton
  to use the right prefix and find the Steam client. Discovery guaranteed both
  non-null above.

### `--log-level` is intentionally not emitted

`MagosConfig.Logging.Level` is a Serilog level name
(`Verbose`/`Information`/`Warning`/`Fatal`) for Magos's own log, but the Enginseer
shell's level vocabulary is `error`/`warn`/`info`/`debug`/`trace`. Forwarding the
Serilog name silently mis-resolved 4 of 6 levels (e.g. `Warning` → shell `info`,
more noise than intended). The two logs serve different purposes; the shell log
level is decoupled and the launcher's `info` default is used. A dedicated
shell-level config field can be added if a future need arises.

## DI registration

```csharp
public static IServiceCollection AddEnginseerClient(this IServiceCollection services)
{
    services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
    services.AddSingleton<IEnginseerLaunchService, EnginseerLaunchService>();
    return services;
}
```

`IProcessLauncher` is `TryAdd` so tests (and hosts wiring a custom launch hook)
can pre-register an override before calling `AddEnginseerClient` — the same
pattern the Steam library uses for its platform seams. `IEnginseerLaunchService`
is `AddSingleton` (holds no per-launch state). Resolves `IProfileService`,
`ISteamService`, `MagosConfig`, `IProcessLauncher`, and
`ILogger<EnginseerLaunchService>` from the container.

## Dependencies

- **Magos libraries:** `config` (`EnginseerRuntimeDir`, `Logging.LogFile`),
  `profiles` (`IProfileService.PrepareModRoot`), `steam` (`ISteamService.Discover`).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Magos.Modificus.EnginseerClient.Tests` is a **dual-purpose** project. `dotnet
test` runs the xUnit suite — `EnginseerLaunchServiceTests` (Windows + Linux arg
assembly, `DiscoveryIncomplete` missing-field derivation, `Error` mapping, the
forced-platform internal constructor), `WinePathTests`, the `AddEnginseerClient`
DI wiring, all against a fake `IProcessLauncher` (`TestDoubles.cs`).
`dotnet run -- <discover|list|launch>` runs the **composition smoke harness**
under `SmokeHarness/Program.cs` — it composes the **real** services (general +
profiles + steam + enginseer-client, no fakes) via the same `Add<Library>()`
chain the UI uses. `launch <profileId>` invokes an actual launch against the
user's Steam/Darktide setup, for user-machine validation; `discover` reports the
resolved Steam/Darktide/Proton discovery + `IsGameRunning()`; `list` lists
profiles.

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release          # xUnit suite
dotnet run --project magos-modificus/tests/Magos.Modificus.EnginseerClient.Tests -- launch <profileId>
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) — the
  [Launch](../../architecture/MAGOS-MODIFICUS.md#launch) section (the Windows /
  Linux split, the `STEAM_COMPAT_*` constraint, the escape hatch).
- [steam](steam.md) — produces the `DiscoveryResult` this consumes.
- [profiles](profiles.md) — `PrepareModRoot` produces the `--mod-path` + `mods.lst`.
