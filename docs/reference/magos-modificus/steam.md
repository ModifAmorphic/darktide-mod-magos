# Steam (`Magos.Modificus.Steam`) — reference

> Steam / Darktide / Proton discovery and game-running detection. Steam
> **discovers** everything needed to launch Darktide modded on the current OS
> and reports missing pieces via the result's nullable fields; it does NOT set
> env vars or invoke Proton — that is [enginseer-client](enginseer-client.md)'s
> job. Status: implemented (Phase 1).

## Public surface

### `ISteamService`

```csharp
public interface ISteamService
{
    DiscoveryResult Discover();
    bool IsGameRunning();
}
```

- `Discover()` — probes the OS-appropriate Steam install locations and resolves
  the Steam install, Darktide install, compatdata, and Proton version. **Never
  throws on missing pieces** — those are reported via `DiscoveryResult.Status`
  and the nullable fields (the escape hatch the UI prompts against).
- `IsGameRunning()` — cross-platform best-effort check against Darktide's process
  name. Delegates to the platform `IProcessLookup`; never throws — enumeration
  failures degrade to "not running."

### `DiscoveryResult`

The outcome of a discovery pass. Fields are nullable: a null means "couldn't
resolve this — the UI should prompt for it."

```csharp
public sealed record DiscoveryResult(
    string? SteamInstallPath,          // Steam client dir → STEAM_COMPAT_CLIENT_INSTALL_PATH
    string? DarktideGameBinaryPath,    // native path to Darktide.exe
    string? CompatdataPath,            // Wine prefix → STEAM_COMPAT_DATA_PATH (Linux only)
    string? ProtonBinaryPath,          // the proton script for `proton run` (Linux only)
    string? ProtonVersion,             // informational label, e.g. "Proton - Experimental"
    DiscoveryStatus Status,            // Complete / Partial / Failed
    IReadOnlyList<string> Warnings);   // non-fatal notes (Flatpak, Proton-selection reason, …)
```

- `DarktideGameBinaryPath` is the native OS path; enginseer-client Z:\-translates
  it on Linux for `--game-binary`.
- `CompatdataPath` / `ProtonBinaryPath` / `ProtonVersion` are null **by design**
  on Windows (native — not used).

```csharp
public enum DiscoveryStatus { Complete, Partial, Failed }
public enum DiscoveryPlatform { Linux, Windows }
```

- **Complete** — every critical field for the current OS is non-null.
- **Partial** — Steam located but some critical field is missing (the nullables
  indicate what to prompt for).
- **Failed** — could not even locate Steam (prompt for the Steam dir).

`DiscoveryPlatform` is the platform discovery runs against; production detects it
from the runtime OS, tests can force it to exercise cross-platform logic on one
OS. Darktide ships on Windows (native) and Linux (Proton) only.

### Injectable seams

The discovery pipeline is fully exercisable against synthetic layouts because
every OS-specific input + platform seam is injected:

- `SteamDiscoveryOptions` — the candidate Steam roots + auxiliary paths (so the
  discoverer never hardcodes `~/.local/share/Steam`) and `Platform`. Production
  wires the real OS defaults via `CreateDefault()`; tests inject fixture paths.
  Notable fields: `LinuxDefaultSteamRoot`, `LinuxFlatpakSteamRoot`,
  `LinuxCompatibilityToolsDir`, `WindowsDefaultSteamRoot`, `DarktideAppId`
  (`1361210`), `DarktideCommonDir`, `GameBinaryName`, `GameProcessName`.
- `ISteamRegistryReader` — reads the Windows registry for the Steam install path
  (`GetSteamPath()` → `HKCU\Software\Valve\Steam\SteamPath`, or null on
  non-Windows / unreadable). Abstracted so the Windows path resolves on Linux CI.
- `IProcessLookup` — `IsRunning(processName)`; two production implementations,
  selected once at DI time (see [Cross-platform notes](#cross-platform-notes)).

## Discovery behavior

### Linux (`DiscoverLinux`)

1. **Steam root** — ordered candidates: native default (`~/.local/share/Steam`)
   first, then Flatpak (`~/.var/app/com.valvesoftware.Steam/data/Steam`). The
   first whose `steamapps/libraryfolders.vdf` exists wins; resolving Flatpak
   raises a warning. A missing root (no candidate carries a valid VDF) → `Failed`.
2. **Libraries** — parses `libraryfolders.vdf` (multi-library) via the internal
   `LibraryFoldersVdf` parser; always includes the Steam root itself as a
   fallback (the VDF usually lists itself as library "0").
3. **Darktide** — `<lib>/steamapps/common/<DarktideCommonDir>/binaries/<GameBinaryName>`
   probed across every library; first hit wins.
4. **Compatdata** — `steamapps/compatdata/<DarktideAppId>/` probed on the main
   install first, then each library in VDF order (the prefix frequently lives on
   a library drive, not the main install); first existing dir wins.
5. **Proton** (Phase 1 heuristic — deep Steam per-game config parsing is
   deferred):
   1. `Proton - Experimental` in `steamapps/common` (common default).
   2. The highest-versioned `Proton X.Y` in `steamapps/common`.
   3. The highest-versioned build in `compatibilitytools.d` (ProtonUp-GE).
   4. Nothing → `null` (escape hatch; UI prompts).

   The chosen source is recorded in `Warnings`. Status is `Complete` only if
   Steam + Darktide + compatdata + Proton all resolve.

### Windows (`DiscoverWindows`)

Registry first (`ISteamRegistryReader` — authoritative when present), then the
default path (`C:\Program Files (x86)\Steam`); the resolved source is recorded.
Same multi-library `libraryfolders.vdf` parse + Darktide probe. Compatdata/Proton
are null (native — unused). Status is `Complete` only if Steam + Darktide resolve.

### VDF parsing (`LibraryFoldersVdf`, internal)

A minimal regex parser for Steam's `libraryfolders.vdf` — extracts the library
root `"path"` values in document order (enough to drive multi-library discovery
without a heavyweight VDF dependency). Unescapes `\\` → `\` and `\"` → `"`
(Windows VDF uses C-style escapes; Linux Steam writes forward slashes). Visible
to tests via `InternalsVisibleTo`.

## Cross-platform notes

`IProcessLookup` is selected **once, at DI registration** by `AddSteam()` from
`RuntimeInformation.IsOSPlatform` — there is no per-call OS branch inside the
check:

| Host | Implementation | How it matches |
| --- | --- | --- |
| Linux | `LinuxProcessLookup` | scans `/proc/<pid>/cmdline`, compares the `argv[0]` basename-stem |
| Windows | `WinProcessLookup` | `Process.GetProcessesByName(processName)` (process comm) |

`LinuxProcessLookup` reads `argv[0]` because the kernel `comm` field (what
`GetProcessesByName` reads on Unix, capped at 15 chars) is **unreliable under
Proton** — Darktide's `comm` is literally `main`, which would yield a false
negative while the game is running.

The load-bearing detail is `MatchesArgv0`: under Proton/wine the launched exe's
`argv[0]` is a **Windows-style** path (`S:\...\Darktide.exe`).
`Path.GetFileNameWithoutExtension` only recognizes the *current* runtime's
directory separators, so on Linux it would not split on backslashes and would
yield the wrong stem. `MatchesArgv0` normalizes backslashes → slashes first so
stem extraction is correct on both OSes. It matches `argv[0]` only — a
whole-cmdline substring match is a known false-positive trap (it would match the
`steam.exe` wrapper and the detector itself).

Both implementations swallow enumeration failures (permission denied, exited
processes, procfs unavailable) as "not running" so a launch is never blocked on
a false negative. `WinProcessLookup` catches `Win32Exception` /
`InvalidOperationException`; `LinuxProcessLookup`'s outer try/catch around
`Directory.EnumerateDirectories("/proc")` is load-bearing (an eager existence
check raises there, not during enumeration).

## DI registration

```csharp
public static IServiceCollection AddSteam(this IServiceCollection services)
{
    services.TryAddSingleton(_ => SteamDiscoveryOptions.CreateDefault());
    services.TryAddSingleton<ISteamRegistryReader, SteamRegistryReader>();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        services.TryAddSingleton<IProcessLookup, LinuxProcessLookup>();
    else
        services.TryAddSingleton<IProcessLookup, WinProcessLookup>();

    services.AddSingleton<ISteamService, SteamService>();
    return services;
}
```

`SteamDiscoveryOptions`, `ISteamRegistryReader`, and `IProcessLookup` are all
`TryAdd` so tests (and hosts with custom paths) can pre-register overrides — the
discovery pipeline is then fully exercisable against fixture layouts. `ISteamService`
is `AddSingleton` (holds no per-call state). Resolves `ILogger<SteamService>` from
the container.

Note: this library does **not** reference `MagosConfig` — it reads OS-specific
inputs entirely from the injected `SteamDiscoveryOptions`. No
`Microsoft.Win32.Registry` package is required: on `net10.0` the `Registry` type
is in the reference assembly, gated behind `[SupportedOSPlatform("windows")]`;
`SteamRegistryReader` guards every call with `OperatingSystem.IsWindows()`, so it
compiles cleanly on Linux and is a no-op there.

## Dependencies

- **Magos libraries:** none (no project references).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Magos.Modificus.Steam.Tests` covers Linux discovery (`LinuxDiscoveryTests`,
`FlatpakDiscoveryTests`), Windows discovery (`WindowsDiscoveryTests`), Proton
selection (`ProtonSelectionTests`), the `libraryfolders.vdf` parser
(`LibraryFoldersVdfTests`), game-running detection (`GameRunningTests`,
`ArgvMatchTests` — the latter pinning the `MatchesArgv0` backslash normalization),
and the `AddSteam` DI wiring (including the `TryAdd` overrides + platform
`IProcessLookup` selection).

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) — the
  [Launch](../../architecture/MAGOS-MODIFICUS.md#launch) section (the Linux
  discovery + escape-hatch + fail-fast design).
- [enginseer-client](enginseer-client.md) — consumes `DiscoveryResult` to invoke
  the launcher.
