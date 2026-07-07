using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles.Tests;

/// <summary>
/// Per-test filesystem + DI fixture: fresh temp <c>ProfilesBaseFolder</c> +
/// <c>ModsFolder</c>, and an <see cref="IProfileService"/> +
/// <see cref="IModRepository"/> resolved through the real
/// <c>AddProfiles()</c> / <c>AddMods()</c> registrations pointing at them.
/// Disposes the temp trees (and the service provider) on teardown so tests are
/// isolated regardless of outcome.
/// </summary>
/// <remarks>
/// Resolving via DI, rather than constructing the implementation directly,
/// keeps the tests black-box against <see cref="IProfileService"/> +
/// <see cref="IModRepository"/> and proves the real registration paths on every
/// test. <see cref="SymlinkCreator"/> defaults to the real BCL symlink; pass a
/// custom one to the constructor to exercise the symlink-failure path.
/// </remarks>
internal sealed class ProfileServiceFixture : IDisposable
{
    private readonly ServiceProvider _provider;

    public string BaseFolder { get; } = Path.Combine(Path.GetTempPath(), "curator-profiles-" + Guid.NewGuid());
    public string ModsFolder { get; } = Path.Combine(Path.GetTempPath(), "curator-mods-" + Guid.NewGuid());

    public IProfileService Service { get; }
    public IModRepository Repo { get; }

    /// <param name="symlink">Optional override for the staging symlink seam
    /// (default: the real BCL <see cref="Directory.CreateSymbolicLink"/>). Pass a
    /// throwing delegate to exercise <see cref="SymlinkStagingException"/>.</param>
    public ProfileServiceFixture(SymlinkCreator? symlink = null)
    {
        var config = CuratorConfig.CreateDefault();
        config.ProfilesBaseFolder = BaseFolder;
        config.ModsFolder = ModsFolder;

        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(new FakeConfigLoader { Config = config });
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning)); // quiet by default
        services.AddMods();
        if (symlink is not null)
        {
            services.AddSingleton(symlink);
        }
        services.AddProfiles();
        _provider = services.BuildServiceProvider();

        Service = _provider.GetRequiredService<IProfileService>();
        Repo = _provider.GetRequiredService<IModRepository>();
    }

    // ---- profile-tree path helpers -----------------------------------------

    public string ProfileDir(Guid id) => Path.Combine(BaseFolder, id.ToString());
    public string ProfileJson(Guid id) => Path.Combine(ProfileDir(id), "profile.json");

    // staged/ is the --mod-path (regenerated each PrepareModRoot).
    public string StagedDir(Guid id) => Path.Combine(ProfileDir(id), "staged");
    public string ModsLst(Guid id) => Path.Combine(StagedDir(id), "mods.lst");
    public string StagedModLink(Guid id, string linkName) => Path.Combine(StagedDir(id), linkName);

    // ---- repository path helpers -------------------------------------------

    public string ContainerDir(Guid containerId) => Path.Combine(ModsFolder, containerId.ToString());
    public string VersionDir(Guid containerId, string versionFolder) =>
        Path.Combine(ContainerDir(containerId), versionFolder);

    // ---- container seeding -------------------------------------------------

    /// <summary>
    /// Creates a container + a single version whose version folder contains the
    /// mod's base folder (named after the container, sanitized), with a
    /// <c>&lt;base&gt;.mod</c> descriptor + a marker file inside it. Mirrors the
    /// shape <c>ModImportService</c> now produces, so staging's base-folder
    /// discovery resolves the container name as the base name (existing mods.lst
    /// assertions stay valid). The version becomes the container's
    /// <c>IsLatest</c>. Used by tests to make a container stage-able.
    /// </summary>
    public ModContainer AddContainerWithVersion(
        string name,
        string versionString = "1.0.0",
        ModSource? source = null)
    {
        var container = Repo.CreateContainer(source ?? new UntrackedSource(), name);
        return Repo.AddVersion(container.Id, versionString, dir =>
        {
            var baseName = SanitizeBaseName(name);
            var baseDir = Path.Combine(dir, baseName);
            Directory.CreateDirectory(baseDir);
            File.WriteAllText(Path.Combine(baseDir, baseName + ".mod"), name);
            File.WriteAllText(Path.Combine(baseDir, "marker.txt"), name);
        });
    }

    /// <summary>
    /// Adds a second version to an existing container whose version folder
    /// contains the mod's base folder (named after the container, sanitized) +
    /// marker, and returns the updated container. The new version becomes the
    /// container's <c>IsLatest</c>.
    /// </summary>
    public ModContainer AddVersion(Guid containerId, string versionString)
    {
        var name = Repo.Get(containerId)?.Name ?? "mod";
        return Repo.AddVersion(containerId, versionString, dir =>
        {
            var baseName = SanitizeBaseName(name);
            var baseDir = Path.Combine(dir, baseName);
            Directory.CreateDirectory(baseDir);
            File.WriteAllText(Path.Combine(baseDir, baseName + ".mod"), versionString);
            File.WriteAllText(Path.Combine(baseDir, "marker.txt"), versionString);
        });
    }

    /// <summary>
    /// Sanitizes a container name into a valid base folder name (illegal
    /// filename chars replaced with <c>_</c>), mirroring what a real import can
    /// place on disk. Normal names ("DMF", "ModB") are unaffected.
    /// </summary>
    private static string SanitizeBaseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "mod" : sanitized;
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(BaseFolder))
        {
            Directory.Delete(BaseFolder, recursive: true);
        }
        if (Directory.Exists(ModsFolder))
        {
            Directory.Delete(ModsFolder, recursive: true);
        }
    }
}
