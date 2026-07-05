# Steam (`Magos.Modificus.Steam`) — reference

> Steam / Darktide / Proton discovery and game-running detection. Steam
> **discovers** everything needed to launch Darktide modded on the current OS
> and reports missing pieces via the result's nullable fields; it does NOT set
> env vars or invoke Proton — that is [enginseer-client](enginseer-client.md)'s
> job. Status: implemented (Phase 1; user-override overlay added in Phase 3
> Track C).

## Public surface

### `ISteamService`

```csharp
public interface ISteamService
{
    DiscoveryResult Discover();
    bool IsGameRunning();
}
```

- `Discover()`: runs the platform `ISteamDiscoverer` (selected once at DI
  registration from `SteamDiscoveryOptions.Platform`, which probes the
  OS-appropriate Steam install locations and resolves the Steam install, Darktide
  install, compatdata, and Proton version), then **overlays** the live
  `MagosConfig.Discovery` user overrides (a non-null/whitespace field replaces
  the auto-discovered value as-is; null keeps auto) and recomputes `Status` via
  the shared `SteamDiscoveryCore.ComputeStatus`. **Never throws on missing
  pieces**; those are reported via `DiscoveryResult.Status` and the nullable
  fields (the escape hatch the UI prompts against). `SteamService` itself
  contains no platform dispatch.
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
- `ISteamDiscoverer` (internal) — `Discover() → DiscoveryResult`. The
  platform-specific discovery strategy. Two implementations
  (`LinuxSteamDiscoverer`, `WindowsSteamDiscoverer`), selected once at DI time
  from `SteamDiscoveryOptions.Platform` (see [Cross-platform notes](#cross-platform-notes)).
- `SteamDiscoveryCore` (internal) — the shared, platform-agnostic mechanics
  (root resolution, `libraryfolders.vdf` reading, Darktide probing, the all-null
  failure result) that both discoverers compose, **plus the single completeness
  rule** `ComputeStatus(platform, steam, darktide, compatdata, proton)` used by
  both discoverers (when building their result) and by `SteamService` (when
  recomputing `Status` after overlaying user overrides). Consolidating the rule
  here guarantees the overlay's recomputed status is, by construction, the same
  rule the discoverer used; the discoverers' per-platform `StatusForLinux` /
  `StatusForWindows` helpers were extracted into it. This is composition, not
  inheritance — each discoverer injects the core and layers its own platform
  steps on top.
- `ISteamRegistryReader` — reads the Windows registry for the Steam install path
  (`GetSteamPath()` → `HKCU\Software\Valve\Steam\SteamPath`, or null if
  unreadable). Abstracted so the Windows discoverer's registry resolution is
  mockable on Linux CI. The production `SteamRegistryReader` is Windows-only
  (annotated `[SupportedOSPlatform("windows")]`) and is registered **only on
  Windows** — on Linux it is intentionally absent so resolving it fails fast.
- `IProcessLookup` — `IsRunning(processName)`; two production implementations,
  selected once at DI time from the host OS (see [Cross-platform notes](#cross-platform-notes)).

## Discovery behavior

`SteamService.Discover()` runs the active `ISteamDiscoverer` (all platform logic
lives in the discoverer + the shared `SteamDiscoveryCore` it composes), then
overlays `MagosConfig.Discovery` user overrides and recomputes `Status`:

1. **Auto-discover** (platform discoverer): resolves Steam / Darktide /
   compatdata / Proton as below, producing a `DiscoveryResult` with a status
   computed via `SteamDiscoveryCore.ComputeStatus`.
2. **Overlay user overrides:** for each of the four path fields, if the matching
   `Discovery.User*Path` is non-null/non-whitespace, it replaces the auto value
   as-is (no re-verify; the user said "use this"); otherwise the auto value is
   kept. (Read live from `IConfigLoader` once per call, so a Settings or
   escape-hatch write is visible on the next `Discover()`.) Overriding the Proton
   binary path nulls the derived `ProtonVersion` label (it no longer describes
   the path in use).
3. **Recompute status:** `SteamDiscoveryCore.ComputeStatus` runs again against
   the final field values, so the reported `Status` reflects the post-overlay
   fields with the same completeness rule the discoverer used (e.g. a user
   override that fills the last Linux gap flips `Partial` to `Complete`).

### Linux (`LinuxSteamDiscoverer`)

1. **Steam root** — ordered candidates: native default (`~/.local/share/Steam`)
   first, then Flatpak (`~/.var/app/com.valvesoftware.Steam/data/Steam`). The
   first whose `steamapps/libraryfolders.vdf` exists wins; resolving Flatpak
   raises a warning. A missing root (no candidate carries a valid VDF) → `Failed`.
2. **Libraries** — parses `libraryfolders.vdf` (multi-library) via the internal
   `LibraryFoldersVdf` parser; always includes the Steam root itself as a
   fallback (the VDF usually lists itself as library "0"). (Both steps are
   `SteamDiscoveryCore` mechanics, shared with the Windows path.)
3. **Darktide** — `<lib>/steamapps/common/<DarktideCommonDir>/binaries/<GameBinaryName>`
   probed across every library; first hit wins. (Shared `SteamDiscoveryCore` step.)
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

### Windows (`WindowsSteamDiscoverer`)

Registry first (`ISteamRegistryReader` — authoritative when present), then the
default path (`C:\Program Files (x86)\Steam`); the resolved source is recorded.
Same multi-library `libraryfolders.vdf` parse + Darktide probe (shared core).
Compatdata/Proton are null (native — unused). Status is `Complete` only if Steam
+ Darktide resolve.

### VDF parsing (`LibraryFoldersVdf`, internal)

A minimal regex parser for Steam's `libraryfolders.vdf` — extracts the library
root `"path"` values in document order (enough to drive multi-library discovery
without a heavyweight VDF dependency). Unescapes `\\` → `\` and `\"` → `"`
(Windows VDF uses C-style escapes; Linux Steam writes forward slashes). Visible
to tests via `InternalsVisibleTo`.

## Cross-platform notes

There are two independent platform selections, made once each at DI registration
by `AddSteam()` — neither leaves a per-call OS branch inside the service:

| Collaborator | Selected from | Implementations |
| --- | --- | --- |
| `ISteamDiscoverer` | `SteamDiscoveryOptions.Platform` (overridable) | `LinuxSteamDiscoverer`, `WindowsSteamDiscoverer` |
| `IProcessLookup` | host runtime OS | `LinuxProcessLookup`, `WinProcessLookup` |

The discoverer follows the **`Platform` option, not the runtime OS**, on purpose:
the `Platform` knob exists precisely so cross-OS testing works — a fixture forces
`Platform = Windows` and the Windows discoverer runs on Linux CI (and vice
 versa). `IsGameRunning` has no such option, so `IProcessLookup` is picked from
the host OS.

### `IProcessLookup`

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
    services.TryAddSingleton<SteamDiscoveryCore>();

    // Discoverer follows the (overridable) Platform knob, NOT the runtime OS.
    services.TryAddSingleton<ISteamDiscoverer>(sp =>
        sp.GetRequiredService<SteamDiscoveryOptions>().Platform == DiscoveryPlatform.Linux
            ? new LinuxSteamDiscoverer(...)   // core + options + logger
            : new WindowsSteamDiscoverer(...)); // core + options + ISteamRegistryReader + logger

    // Windows-only capability: NOT registered on Linux (fail-fast if resolved).
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        services.TryAddSingleton<ISteamRegistryReader, SteamRegistryReader>();

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        services.TryAddSingleton<IProcessLookup, LinuxProcessLookup>();
    else
        services.TryAddSingleton<IProcessLookup, WinProcessLookup>();

    services.AddSingleton<ISteamService, SteamService>();
    return services;
}
```

`SteamDiscoveryOptions`, `SteamDiscoveryCore`, `ISteamDiscoverer`,
`ISteamRegistryReader`, and `IProcessLookup` are all `TryAdd` so tests (and hosts
with custom paths) can pre-register overrides — the discovery pipeline is then
fully exercisable against fixture layouts (e.g. the Steam fixture pre-registers
its `FakeRegistryReader` + forces `Platform = Windows`, which drives the discoverer
selection so the Windows path runs on Linux CI). `ISteamService` is `AddSingleton`
(holds no per-call state).

Note: `AddSteam()` does **not** register `IConfigLoader` itself. `SteamService`
depends on `IConfigLoader` (it reads `MagosConfig.Discovery` live on each
`Discover()` call so a Settings / escape-hatch write is visible immediately), so
an `IConfigLoader` must be registered externally before resolving
`ISteamService`. In production that is [General](general.md)'s `AddGeneral()`
(which `TryAdd`s `ConfigLoader`); tests register a fake (e.g. the overlay + DI
tests register a `FakeConfigLoader`).

`SteamRegistryReader` is Windows-only: no `Microsoft.Win32.Registry` package is
required (on `net10.0` the `Registry` type is in the reference assembly, gated
behind `[SupportedOSPlatform("windows")]`), and the reader is annotated
`[SupportedOSPlatform("windows")]` at the type level (for CA1416, with no
per-call runtime guard). It is registered **only on Windows** by `AddSteam()`;
on Linux it is intentionally absent so resolving `ISteamRegistryReader` fails
fast (the honest outcome for a Windows-only capability, rather than a silent
no-op).

## Dependencies

- **Magos libraries:** [config](config.md) (`DiscoveryConfig`, the user-override
  section of `MagosConfig` overlaid onto the discoverer's result) +
  [general](general.md) (`IConfigLoader`, the live reader `SteamService` reads
  `Discovery` from on each `Discover()` call).
- **NuGet:** `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging.Abstractions`.

## Testing

`Magos.Modificus.Steam.Tests` covers Linux discovery (`LinuxDiscoveryTests`,
`FlatpakDiscoveryTests`), Windows discovery (`WindowsDiscoveryTests`), Proton
selection (`ProtonSelectionTests`), the `libraryfolders.vdf` parser
(`LibraryFoldersVdfTests`), game-running detection (`GameRunningTests`,
`ArgvMatchTests` — the latter pinning the `MatchesArgv0` backslash normalization),
the `AddSteam` DI wiring (the `TryAdd` overrides, the `ISteamDiscoverer`
selection by `SteamDiscoveryOptions.Platform`, and the platform `IProcessLookup`
selection), and the `SteamService.Discover()` user-override overlay
(`SteamServiceOverlayTests`: a supplied path replaces the auto value as-is, a
null/whitespace value keeps auto, mixed overlays apply only the set fields, and
`Status` recomputes via the shared `ComputeStatus` rule, including the live-read
contract that makes a config write between calls visible on the next
`Discover()`). `WindowsDiscoveryTests` force `Platform = Windows` + a fake registry
reader so the Windows discoverer path runs on Linux CI — the load-bearing proof
that discoverer selection follows `Platform`, not the runtime OS.

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) — the
  [Launch](../../architecture/MAGOS-MODIFICUS.md#launch) section (the Linux
  discovery + escape-hatch + fail-fast design).
- [enginseer-client](enginseer-client.md) — consumes `DiscoveryResult` to invoke
  the launcher.
