# General (`Magos.Modificus.General`) — reference

> Cross-cutting infrastructure: structured logging, JSON config loading, and the
> DI registration that wires both into the container. Status: implemented (Phase 0).

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

There are no `TryAdd` seams here: `config` and `loggerFactory` are constructed
objects passed in, not overridable from the container. Tests that want fakes
construct their own `ServiceCollection`.

## Dependencies

- **Magos libraries:** `config` (project reference — `MagosConfig`).
- **NuGet:** `Microsoft.Extensions.Configuration` (+ `.Binder`, `.Json`),
  `Microsoft.Extensions.DependencyInjection.Abstractions`,
  `Microsoft.Extensions.Logging` (+ `.Abstractions`), Serilog (`Serilog`,
  `Serilog.Extensions.Logging`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`).

## Testing

`Magos.Modificus.General.Tests` covers `ConfigLoader` (first-run-safe + JSON
override binding), `LoggingBootstrap` (level parsing, truncation, file/dir
creation), and the `AddGeneral` DI wiring.

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md) —
  design; the [Composition & startup](../../architecture/MAGOS-MODIFICUS.md#composition--startup)
  section.
- [config](config.md) — the `MagosConfig` schema this library loads.
