namespace Magos.Modificus.Config;

/// <summary>
/// Platform-appropriate default locations for Magos Modificus data, resolved
/// once from the OS local-application-data folder
/// (<c>%LOCALAPPDATA%</c> on Windows, <c>~/.local/share</c> on Linux).
/// Shared by <see cref="MagosConfig"/> and <see cref="LoggingConfig"/> so every
/// field has a default and the JSON binder only overwrites what the file sets.
/// </summary>
internal static class AppPaths
{
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Magos Modificus");

    public static readonly string DefaultLogFile = Path.Combine(AppDataDir, "logs", "magos.log");
    public static readonly string DefaultProfilesBaseFolder = Path.Combine(AppDataDir, "profiles");
    public static readonly string DefaultModsFolder = Path.Combine(AppDataDir, "mods");
    public static readonly string DefaultEnginseerRuntimeDir = Path.Combine(AppDataDir, "enginseer");
}
