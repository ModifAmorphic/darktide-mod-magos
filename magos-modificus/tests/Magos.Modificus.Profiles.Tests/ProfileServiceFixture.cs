using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;
using Magos.Modificus.SharedMods;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// Per-test filesystem + DI fixture: fresh temp <c>ProfilesBaseFolder</c> +
/// <c>SharedModsFolder</c>, and an <see cref="IProfileService"/> +
/// <see cref="ISharedModStore"/> resolved through the real
/// <c>AddProfiles()</c> / <c>AddSharedMods()</c> registrations pointing at them.
/// Disposes the temp trees (and the service provider) on teardown so tests are
/// isolated regardless of outcome.
/// </summary>
/// <remarks>
/// Resolving via DI — rather than constructing the implementation directly —
/// keeps the tests black-box against <see cref="IProfileService"/> /
/// <see cref="ISharedModStore"/> and proves the real registration paths on every
/// test. <see cref="SymlinkCreator"/> defaults to the real BCL symlink; pass a
/// custom one to the constructor to exercise the symlink-failure path.
/// </remarks>
internal sealed class ProfileServiceFixture : IDisposable
{
    private readonly ServiceProvider _provider;

    public string BaseFolder { get; } = Path.Combine(Path.GetTempPath(), "magos-profiles-" + Guid.NewGuid());
    public string SharedFolder { get; } = Path.Combine(Path.GetTempPath(), "magos-shared-" + Guid.NewGuid());

    public IProfileService Service { get; }
    public ISharedModStore SharedStore { get; }

    /// <param name="symlink">Optional override for the staging symlink seam
    /// (default: the real BCL <see cref="Directory.CreateSymbolicLink"/>). Pass a
    /// throwing delegate to exercise <see cref="SymlinkStagingException"/>.</param>
    public ProfileServiceFixture(SymlinkCreator? symlink = null)
    {
        var config = MagosConfig.CreateDefault();
        config.ProfilesBaseFolder = BaseFolder;
        config.SharedModsFolder = SharedFolder;

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning)); // quiet by default
        services.AddSharedMods();
        if (symlink is not null)
        {
            services.AddSingleton(symlink);
        }
        services.AddProfiles();
        _provider = services.BuildServiceProvider();

        Service = _provider.GetRequiredService<IProfileService>();
        SharedStore = _provider.GetRequiredService<ISharedModStore>();
    }

    // ---- profile-tree path helpers -----------------------------------------

    public string ProfileDir(Guid id) => Path.Combine(BaseFolder, id.ToString());
    public string ProfileJson(Guid id) => Path.Combine(ProfileDir(id), "profile.json");

    // staged/ is the --mod-path (regenerated each PrepareModRoot).
    public string StagedDir(Guid id) => Path.Combine(ProfileDir(id), "staged");
    public string ModsLst(Guid id) => Path.Combine(StagedDir(id), "mods.lst");
    public string StagedModLink(Guid id, string modName) => Path.Combine(StagedDir(id), modName);

    // mods/ holds a profile's local copy of a mod.
    public string DivergedDir(Guid id) => Path.Combine(ProfileDir(id), "mods");
    public string DivergedModDir(Guid id, string modName) => Path.Combine(DivergedDir(id), modName);

    // ---- shared-store path helpers -----------------------------------------

    public string SharedModDir(string modName) => Path.Combine(SharedFolder, modName);

    /// <summary>
    /// Adds a shared-store entry + creates the mod's files at
    /// <c>&lt;SharedFolder&gt;/&lt;modName&gt;</c> (the acquisition step Phase 2
    /// assumes). Used by tests to make a mod Share-able.
    /// </summary>
    public void AddSharedMod(string modName, string policyLabel = "latest", string? version = null)
    {
        Directory.CreateDirectory(SharedModDir(modName));
        File.WriteAllText(Path.Combine(SharedModDir(modName), "marker.txt"), modName);

        var policy = policyLabel == "pinned"
            ? (ModVersionPolicy)new PinnedPolicy(Version.Parse(version ?? "1.0.0"))
            : ModVersionPolicy.Latest;
        var actualVersion = version is null ? new Version(1, 0, 0) : Version.Parse(version);

        SharedStore.Add(new SharedModEntry
        {
            Name = modName,
            Policy = policy,
            ActualVersion = actualVersion,
            Path = SharedModDir(modName),
        });
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(BaseFolder))
        {
            Directory.Delete(BaseFolder, recursive: true);
        }
        if (Directory.Exists(SharedFolder))
        {
            Directory.Delete(SharedFolder, recursive: true);
        }
    }
}
