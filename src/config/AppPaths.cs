namespace Modificus.Curator.Config;

/// <summary>
/// Platform-appropriate default locations for Modificus Curator data, resolved
/// once from the OS local-application-data folder
/// (<c>%LOCALAPPDATA%</c> on Windows, <c>~/.local/share</c> on Linux).
/// Shared by <see cref="CuratorConfig"/> and <see cref="LoggingConfig"/> so every
/// field has a default and the JSON binder only overwrites what the file sets.
/// </summary>
public static class AppPaths
{
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Modificus Curator");

    public static readonly string DefaultLogFile = Path.Combine(AppDataDir, "logs", "curator.log");
    public static readonly string DefaultProfilesBaseFolder = Path.Combine(AppDataDir, "profiles");
    public static readonly string DefaultModsFolder = Path.Combine(AppDataDir, "mods");
    public static readonly string DefaultRelayDir = Path.Combine(AppDataDir, "relay");
}
