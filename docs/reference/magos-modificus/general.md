# General (`Magos.Modificus.General`) — reference

> Cross-cutting infrastructure: structured logging, JSON config loading, runtime
> app-state persistence, and the DI registration that wires all three into the
> container. Status: implemented (Phase 0; app-state store added in Phase 3).

The composition root (`magos-modificus/ui/MagosComposition.cs`) calls into this
library first — before any domain library — to load `MagosConfig` and build the
logger the rest of the app shares.

## Public surface

### `LoggingBootstrap` (static)

Builds the structured-logging pipeline from `MagosConfig.Logging`. Serilog
(console + file sinks) bridged into `Microsoft.Extensions.Logging`, filtered to
the configured level. The log file is truncated on each startup — no rolling,
retention, or backup.

```csharp
public static class LoggingBootstrap
{
    public static ILoggerFactory CreateLoggerFactory(MagosConfig config);
}
```

`CreateLoggerFactory(config)`:
- Reads `config.Logging.Level` (a Serilog level name — `Verbose` / `Debug` /
  `Information` / `Warning` / `Error` / `Fatal`); an unknown value falls back to
  `Information`.
- Creates the log-file parent directory (the file sink does not reliably create
  missing parents), then `File.Delete`s the configured log path so this run
  starts a fresh file (no-op when the file is absent).
- Builds a Serilog logger (`.MinimumLevel.Is(level)` + `.Enrich.FromLogContext()`
  + console + file sinks), assigns it to the global `Log.Logger`, wraps it in a
  `LoggerFactory` via `AddSerilog(logger, dispose: true)`, and returns it.
  Disposing the factory disposes the Serilog logger (flushing the file sink).

### `IConfigLoader` / `ConfigLoader`

Loads `MagosConfig` from JSON with full defaults. A missing or partial file
yields a fully-usable config — every field has a platform-appropriate default
(see [config](config.md)).

```csharp
public interface IConfigLoader
{
    MagosConfig Load();
}

public sealed class ConfigLoader : IConfigLoader
{
    public ConfigLoader(string? path = null);
    public string Path { get; }
    public MagosConfig Load();
    public static string DefaultConfigPath();
}
```

- `ConfigLoader(path)` — `null` resolves to `DefaultConfigPath()`.
- `Load()` — starts from `MagosConfig.CreateDefault()`. If the config file's
  parent directory exists, binds the JSON file onto the defaults via
  `Microsoft.Extensions.Configuration` (`AddJsonFile(optional: true)`); if the
  directory is absent (a fresh first run), skips straight to the defaults rather
  than letting `SetBasePath` throw. Unset keys keep their defaults.
- `DefaultConfigPath()` — `<LocalApplicationData>/Magos Modificus/config.json`
  (`%LOCALAPPDATA%` on Windows, `~/.local/share` on Linux).

### `IAppStateStore` / `AppStateStore`

Persists **runtime application state**: values that capture "where the app left
off" rather than user system settings. Kept deliberately narrow: the only state
today is the last-chosen active profile. A separate file (not `MagosConfig`)
holds it so the settings schema stays pure (system settings vs. runtime state).

```csharp
public interface IAppStateStore
{
    Guid? ActiveProfileId { get; set; }   // set persists immediately
}

public sealed class AppStateStore : IAppStateStore
{
    public AppStateStore(string? path = null);
    public string Path { get; }
    public static string DefaultStatePath();
}
```

- File: `<LocalApplicationData>/Magos Modificus/app-state.json`
  (`{ "ActiveProfileId": "<guid>" | null }`), derived the same way
  `ConfigLoader` derives its config path.
- JSON is handled with `System.Text.Json` directly (read + write);
  `Microsoft.Extensions.Configuration` is binding-oriented and read-only, the
  wrong fit for a tiny writable state file.
- **First-run safe:** a missing or corrupt file never throws; `get` just
  returns `null`. Writes are best-effort (runtime state is non-critical; a
  persistence failure is swallowed rather than crashing the app).
- Used by `IProfileSession` (the active-profile authority) to restore the active
  profile on construction and persist it on changes. The shell and the Manage
  dialog read the active id through the session; they do not touch this store.

## DI registration

```csharp
public static IServiceCollection AddGeneral(
    this IServiceCollection services,
    MagosConfig config,
    ILoggerFactory loggerFactory);
```

`AddGeneral(config, loggerFactory)` is called by the composition root **after**
config is loaded and the logger built (both are constructed outside DI because
DI itself needs them). It registers:

- `AddSingleton(config)` — the loaded `MagosConfig` (other libraries resolve this).
- `AddSingleton(loggerFactory)` — the Serilog-backed `ILoggerFactory`.
- `AddLogging()` — wires `ILogger<T>` resolution through the factory.
- `AddSingleton<IConfigLoader, ConfigLoader>()` — so a re-load is available if
  ever needed (the path is re-resolved to the default location).
- `TryAddSingleton<IAppStateStore, AppStateStore>()`: the runtime app-state
  store. `TryAdd` (not `Add`) so a test or host may pre-register an override
  (e.g. an in-memory or temp-path store) before `AddGeneral` runs.

`config` and `loggerFactory` are constructed objects passed in, not overridable
from the container; `IAppStateStore` is the one seam here (overridable via
pre-registration).

## Dependencies

- **Magos libraries:** `config` (project reference — `MagosConfig`).
- **NuGet:** `Microsoft.Extensions.Configuration` (+ `.Binder`, `.Json`),
  `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging` (+ `.Abstractions`), Serilog (`Serilog`,
  `Serilog.Extensions.Logging`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`).

## Testing

`Magos.Modificus.General.Tests` covers `ConfigLoader` (first-run-safe + JSON
override binding), `AppStateStore` (round-trip + first-run + corrupt-file
safety + the app-data default path), `LoggingBootstrap` (level parsing,
truncation, file/dir creation), and the `AddGeneral` DI wiring (including the
`TryAdd` `IAppStateStore` override).

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) —
  design; the [Composition & startup](../../architecture/MAGOS-MODIFICUS.md#composition--startup)
  section.
- [config](config.md) — the `MagosConfig` schema this library loads.
