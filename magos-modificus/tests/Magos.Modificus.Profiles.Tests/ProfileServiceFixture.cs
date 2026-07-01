using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;

namespace Magos.Modificus.Profiles.Tests;

/// <summary>
/// Per-test filesystem + DI fixture: a fresh temp <c>ProfilesBaseFolder</c>
/// and an <see cref="IProfileService"/> resolved through the real
/// <c>AddProfiles()</c> registration pointing at it. Disposes the temp tree
/// (and the service provider) on teardown so tests are isolated regardless of
/// outcome.
/// </summary>
/// <remarks>
/// Resolving via DI — rather than constructing the implementation directly —
/// keeps the tests black-box against <see cref="IProfileService"/> and proves
/// the real registration path on every test.
/// </remarks>
internal sealed class ProfileServiceFixture : IDisposable
{
    private readonly ServiceProvider _provider;

    public string BaseFolder { get; } = Path.Combine(Path.GetTempPath(), "magos-profiles-" + Guid.NewGuid());

    public IProfileService Service { get; }

    public ProfileServiceFixture()
    {
        var config = MagosConfig.CreateDefault();
        config.ProfilesBaseFolder = BaseFolder;

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning)); // quiet by default
        services.AddProfiles();
        _provider = services.BuildServiceProvider();

        Service = _provider.GetRequiredService<IProfileService>();
    }

    public string ProfileDir(Guid id) => Path.Combine(BaseFolder, id.ToString());
    public string ProfileJson(Guid id) => Path.Combine(ProfileDir(id), "profile.json");
    public string ModRoot(Guid id) => Path.Combine(ProfileDir(id), "mods");
    public string ModsLst(Guid id) => Path.Combine(ModRoot(id), "mods.lst");

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(BaseFolder))
        {
            Directory.Delete(BaseFolder, recursive: true);
        }
    }
}
