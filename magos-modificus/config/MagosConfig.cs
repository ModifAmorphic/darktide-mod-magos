namespace Magos.Modificus.Config;

/// <summary>
/// The global Magos Modificus configuration: system-level settings shared
/// across all profiles. Per-profile settings live with the profile, not here.
/// Bound from JSON by the config loader in <c>Magos.Modificus.General</c>.
/// </summary>
/// <remarks>
/// Every field carries a sensible platform-appropriate default, so an absent
/// (or partially-populated) config file always yields a usable object.
/// </remarks>
public sealed class MagosConfig
{
    /// <summary>Logging settings (level + file).</summary>
    public LoggingConfig Logging { get; set; } = new();

    /// <summary>Where profiles, per-profile mods, and profile settings are stored.</summary>
    public string ProfilesBaseFolder { get; set; } = AppPaths.DefaultProfilesBaseFolder;

    /// <summary>
    /// The unified mod repository root. One UUID container per (source, identity),
    /// holding opaque-ID version subfolders; see <c>docs/architecture/MAGOS-MODIFICUS.md</c>
    /// (Mod repository).
    /// </summary>
    public string ModsFolder { get; set; } = AppPaths.DefaultModsFolder;

    /// <summary>
    /// The Enginseer runtime directory: where <c>magos_launcher.exe</c>,
    /// <c>magos_shell.dll</c>, and <c>mod_loader/</c> live.
    /// </summary>
    public string EnginseerRuntimeDir { get; set; } = AppPaths.DefaultEnginseerRuntimeDir;

    /// <summary>
    /// User-supplied discovery overrides. When a field here is non-null/non-
    /// whitespace, <c>SteamService.Discover()</c> uses it as-is in place of the
    /// auto-discovered value (no re-verify); null/whitespace means auto-discover
    /// that field. Read live per <c>Discover()</c> call via
    /// <see cref="General.IConfigLoader"/>.
    /// </summary>
    public DiscoveryConfig Discovery { get; set; } = new();

    /// <summary>External-service (mod-source) integration settings.</summary>
    public IntegrationsConfig Integrations { get; set; } = new();

    /// <summary>
    /// User-facing global preferences (theme, font scale, language). The
    /// Preferences dialog reads + writes this section via the
    /// <c>IPreferencesService</c>; persistence is through
    /// <see cref="General.ConfigLoader"/>.<c>Save</c>.
    /// </summary>
    public PreferencesConfig Preferences { get; set; } = new();

    /// <summary>A fully-defaulted config instance.</summary>
    public static MagosConfig CreateDefault() => new();
}
