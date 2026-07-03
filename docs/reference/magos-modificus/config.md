# Config (`Magos.Modificus.Config`): reference

> The global configuration schema: a POCO model with platform-appropriate
> defaults, bound from JSON by the [General](general.md) library. Status:
> implemented (Phase 0; Preferences section added in Phase 3 Track D).

`MagosConfig` holds system-level settings shared across all profiles. Per-profile
settings live with the profile, not here. Every field carries a default, so an
absent or partially-populated config file always yields a usable object.

A sample file ships at `magos-modificus/config.example.json`; copy it to
`<LocalApplicationData>/Magos Modificus/config.json` and edit only what you want
to override.

## Public surface

### `MagosConfig`

The root config object: the aggregate the loader binds and (Phase 3 Track D) the
`ConfigLoader.Save` writes back wholesale on a Preferences change.

```csharp
public sealed class MagosConfig
{
    public LoggingConfig Logging { get; set; } = new();
    public string ProfilesBaseFolder { get; set; }      // default profiles root
    public string SharedModsFolder { get; set; }         // default shared-mods root
    public string EnginseerRuntimeDir { get; set; }      // default enginseer root
    public IntegrationsConfig Integrations { get; set; } = new();
    public PreferencesConfig Preferences { get; set; } = new();

    public static MagosConfig CreateDefault();
}
```

| Field | Default | Meaning |
| --- | --- | --- |
| `Logging` | see `LoggingConfig` | Log level + file (consumed by `LoggingBootstrap`). |
| `ProfilesBaseFolder` | `<app-data>/profiles` | Where profiles, per-profile mods, and profile settings are stored. |
| `SharedModsFolder` | `<app-data>/shared-mods` | The global shared mod store (see [shared-mods](shared-mods.md)). |
| `EnginseerRuntimeDir` | `<app-data>/enginseer` | Where `magos_launcher.exe`, `magos_shell.dll`, and `mod_loader/` live (consumed by [enginseer-client](enginseer-client.md)). |
| `Integrations` | see `IntegrationsConfig` | External-service (mod-source) integration settings. |
| `Preferences` | see `PreferencesConfig` | User-facing global preferences (theme, font scale, language). Phase 3 Track D. |

`<app-data>` is `Environment.SpecialFolder.LocalApplicationData`: `%LOCALAPPDATA%`
on Windows, `~/.local/share` on Linux, under a `Magos Modificus/` subfolder.

### `LoggingConfig`

```csharp
public sealed class LoggingConfig
{
    public string Level { get; set; } = "Information";   // Serilog level name
    public string LogFile { get; set; }                  // default <app-data>/logs/magos.log
}
```

- `Level`: a Serilog level name (`Verbose`/`Debug`/`Information`/`Warning`/
  `Error`/`Fatal`); an unknown value falls back to `Information` at bootstrap.
- `LogFile`: the structured log file; truncated on each manager startup.

### `IntegrationsConfig` / `GitHubConfig`

```csharp
public sealed class IntegrationsConfig
{
    public GitHubConfig GitHub { get; set; } = new();
}

public sealed class GitHubConfig
{
    public string BaseUrl { get; set; } = "https://api.github.com";
    public string? Token { get; set; }   // optional PAT
}
```

- `BaseUrl`: the GitHub REST API root, without a trailing slash. Override for
  GitHub Enterprise (`https://<host>/api/v3`). The Integrations library
  normalizes the value to end with a trailing slash when configuring
  `HttpClient.BaseAddress`.
- `Token`: an optional personal access token sent as `Bearer <token>`; when
  unset, requests are anonymous (public releases need no auth). Phase 1 has no
  token-management UI; supply via config only.

### `PreferencesConfig` / `ThemeMode` (Phase 3 Track D)

User-facing global preferences, exposed through the Preferences dialog. The
dialog applies each change immediately (theme + font scale + language take
effect live) and persists through `ConfigLoader.Save`; there is no commit step.

```csharp
public sealed class PreferencesConfig
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public double FontScale { get; set; } = 1.0;
    public string Language { get; set; } = "en";
}

public enum ThemeMode
{
    System = 0,   // follow the OS theme (Avalonia ThemeVariant.Default)
    Dark = 1,     // Avalonia ThemeVariant.Dark
    Light = 2,    // Avalonia ThemeVariant.Light
}
```

- `Theme`: the UI theme variant. `System` follows the OS (Avalonia's
  `ThemeVariant.Default`); applied via `Application.Current.RequestedThemeVariant`.
- `FontScale`: a continuous UI font-scale multiplier (1.0 = no scaling). The
  Preferences dialog exposes this as a percent slider (80 to 150 in 5% steps);
  the persisted value is the raw double (e.g. 1.25 for 125%). Applied as an
  application-level `AppFontSize` resource that a Window style binds to
  (`DynamicResource`), cascading to all controls via inheritance.
- `Language`: the display language as a culture name (e.g. `en`, `fr`). Empty or
  `en` resolves to the neutral English resources. English ships; the selector +
  culture switching are in place; real translations are content added later via
  translated `Strings.<culture>.resx` files (no code change). Switching language
  updates the live UI through the UI-layer `LocalizationService` (dynamic, no
  restart).

### `AppPaths` (internal)

Resolves the default locations once, from `LocalApplicationData`. Shared by
`MagosConfig` and `LoggingConfig` so every field has a default and the JSON
binder only overwrites what the file sets. Exposed as `internal`: not part of
the library's public surface.

## DI registration

None. This is a POCO schema library with no service registrations. Other
libraries register a loaded `MagosConfig` singleton via `AddGeneral()` (see
[General](general.md)) and resolve `MagosConfig` from the container as needed.

## Dependencies

- **Magos libraries:** none.
- **NuGet:** none (pure POCO; targets `net10.0`).

## Testing

Covered transitively: there is no standalone test project. The schema's
defaults and JSON binding are exercised by `Magos.Modificus.General.Tests`
(`ConfigLoader` first-run-safe + override tests, plus Phase 3 Track D
`Preferences` round-trip + `Save` coverage) and by every other library's test
fixtures, which build a `MagosConfig.CreateDefault()` pointing at a temp dir.

```sh
dotnet test magos-modificus/magos-modificus.sln -c Release
```

## See also

- [Magos Modificus architecture](../../architecture/MAGOS-MODIFICUS.md): the
  [Configuration](../../architecture/MAGOS-MODIFICUS.md#configuration) section.
- [general](general.md): the loader that binds this schema (and writes it back).
- `magos-modificus/config.example.json`: the sample file.
