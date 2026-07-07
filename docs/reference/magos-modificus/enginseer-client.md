# Enginseer-client (`Magos.Modificus.EnginseerClient`) — reference

> The v1 launch façade over the Enginseer runtime. Resolves the profile + Steam
> discovery, assembles the launcher args, and invokes `magos_launcher.exe` —
> directly on Windows, under `proton run` on Linux. Fire-and-forget in v1: it
> starts the launcher and returns; it does not track the game process.

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
  launch that won't happen) — `MissingDiscoveryFields` lists them. The
  per-platform required set comes from the active `IPlatformLaunchStrategy`.
- Prepares the mod root (`IProfileService.PrepareModRoot(profileId)` — writes
  `mods.lst` and returns the `--mod-path`). An unknown profile (`KeyNotFoundException`
  from PrepareModRoot) is caught and mapped to `Error`.
- Checks that the launcher exists at `<EnginseerRuntimeDir>/magos_launcher.exe`.
- Spawns the launcher via the active `IPlatformLaunchStrategy` (directly on
  Windows; under `proton run` on Linux) — the service itself contains no
  per-launch OS branch.

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
`Status != Complete`. The per-platform required set is owned by the active
`IPlatformLaunchStrategy` (`RequiredDiscoveryFields`).

### Injectable seams

- `IPlatformLaunchStrategy` (internal) — the per-platform launch surface:
  - `RequiredDiscoveryFields(discovery)` — the discovery fields this platform
    requires but could not resolve (Windows: Steam + game binary; Linux: +
    compatdata + Proton).
  - `Start(launcherPath, discovery, gameBinary, modPath, logFile) → bool` — the
    spawn. Windows: a direct invocation of the launcher with native
    (untranslated) args; Linux: `<proton> run <launcher.exe> <args>` with both
    `STEAM_COMPAT_*` env vars and the path-valued flags `Z:\`-translated.
  - `Name` — a short label ("Windows" / "Linux") for log messages.
  - Two implementations (`WindowsLaunchStrategy`, `LinuxLaunchStrategy`),
    selected once at DI time from the host OS (see
    [Cross-platform notes](#cross-platform-notes)).
- `IProcessLauncher` — `Start(filePath, arguments, environmentVariables) → bool`
  (fire-and-forget; `true` if started, `false` if it could not start — never
  throws). Abstracted so the launch path is deterministic and mockable in tests
  (the real `Process.Start` would spawn a real process). Injected into the
  strategy (not the service) so tests can fake the spawn. The default
  `ProcessLauncher` uses `ProcessStartInfo.ArgumentList` (argv-correct, no shell,
  no injection surface) and applies env overrides directly to the child's
  environment block.

`WinePath.ToWine(posixPath)` (internal) translates an absolute POSIX path to its
Wine `Z:\` form (`/` → `\`, `Z:` prefix) for the launcher-under-Wine flags; it is
used only by `LinuxLaunchStrategy`.

## Cross-platform notes

The launch path branches on platform via the active `IPlatformLaunchStrategy`,
selected once at DI registration from the host OS — the launch service contains
no per-launch OS branch. Each strategy owns the spawn (via `IProcessLauncher`),
its required discovery fields, and its own log label.

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

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        services.TryAddSingleton<IPlatformLaunchStrategy, WindowsLaunchStrategy>();
    else
        services.TryAddSingleton<IPlatformLaunchStrategy, LinuxLaunchStrategy>();

    services.AddSingleton<IEnginseerLaunchService, EnginseerLaunchService>();
    return services;
}
```

`IProcessLauncher` and `IPlatformLaunchStrategy` are `TryAdd` so tests (and hosts
wiring a custom launch hook) can pre-register an override before calling
`AddEnginseerClient` — the same pattern the Steam library uses for its platform
seams. The strategy is selected once, here, from the host OS, so the launch
service contains no per-call OS branch. `IEnginseerLaunchService` is `AddSingleton`
(holds no per-launch state). Resolves `IProfileService`, `ISteamService`,
`MagosConfig`, `IPlatformLaunchStrategy`, and `ILogger<EnginseerLaunchService>`
from the container.

## Dependencies

- **Magos libraries:** `config` (`EnginseerRuntimeDir`, `Logging.LogFile`),
  `profiles` (`IProfileService.PrepareModRoot`), `steam` (`ISteamService.Discover`).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Magos.Modificus.EnginseerClient.Tests` is a **dual-purpose** project. `dotnet
test` runs the xUnit suite — `EnginseerLaunchServiceTests` (Windows + Linux arg
assembly via the concrete `WindowsLaunchStrategy` / `LinuxLaunchStrategy` + a
fake `IProcessLauncher`, `DiscoveryIncomplete` missing-field derivation, `Error`
mapping), `WinePathTests`, the `AddEnginseerClient` DI wiring, all against the
fakes in `TestDoubles.cs`. Tests inject the concrete strategy to exercise either
path on any CI OS. `dotnet run -- <discover|list|launch>` runs the **composition
smoke harness** under `SmokeHarness/Program.cs` — it composes the **real**
services (general + profiles + steam + enginseer-client, no fakes) via the same
`Add<Library>()` chain the UI uses. `launch <profileId>` invokes an actual launch
against the user's Steam/Darktide setup, for user-machine validation; `discover`
reports the resolved Steam/Darktide/Proton discovery + `IsGameRunning()`; `list`
lists profiles.

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
