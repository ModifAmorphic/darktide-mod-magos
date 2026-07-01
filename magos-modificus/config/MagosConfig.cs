namespace Magos.Modificus.Config;

/// <summary>
/// The global Magos Modificus configuration — system-level settings shared
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
    /// The global shared mod store. Mods live shared-first across profiles;
    /// see <c>docs/architecture/MAGOS-MODIFICUS.md</c> (Shared mod storage).
    /// </summary>
    public string SharedModsFolder { get; set; } = AppPaths.DefaultSharedModsFolder;

    /// <summary>
    /// The Enginseer runtime directory — where <c>magos_launcher.exe</c>,
    /// <c>magos_shell.dll</c>, and <c>mod_loader/</c> live.
    /// </summary>
    public string EnginseerRuntimeDir { get; set; } = AppPaths.DefaultEnginseerRuntimeDir;

    /// <summary>External-service (mod-source) integration settings.</summary>
    public IntegrationsConfig Integrations { get; set; } = new();

    /// <summary>A fully-defaulted config instance.</summary>
    public static MagosConfig CreateDefault() => new();
}
