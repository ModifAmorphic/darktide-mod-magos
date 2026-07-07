namespace Modificus.Curator.Steam.Tests;

/// <summary>
/// Windows discovery: registry resolution (mockable on Linux), default-path
/// fallback, Failed when neither resolves, Complete/Partial by Darktide, and
/// Compatdata/Proton null by design (Windows native).
/// </summary>
/// <remarks>
/// These force <see cref="DiscoveryPlatform.Windows"/> with a fake
/// <see cref="ISteamRegistryReader"/> so the Windows path logic runs on Linux CI.
/// </remarks>
public sealed class WindowsDiscoveryTests
{
    [Fact]
    public void Registry_path_resolves_and_is_marked_as_from_registry()
    {
        using var fx = new SteamFixture(platform: DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot(); // the fixture SteamRoot is what the fake registry returns
        fx.Registry.SteamPath = fx.SteamRoot;
        fx.WithDarktide(fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Equal(fx.ExpectedDarktidePath(fx.SteamRoot), result.DarktideGameBinaryPath);
        Assert.Contains(result.Warnings, w => w.Contains("registry", StringComparison.Ordinal));
        // Windows never discovers Proton/compatdata.
        Assert.Null(result.CompatdataPath);
        Assert.Null(result.ProtonBinaryPath);
        Assert.Null(result.ProtonVersion);
    }

    [Fact]
    public void Registry_null_falls_back_to_default_path()
    {
        using var fx = new SteamFixture(platform: DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.Registry.SteamPath = null; // registry yields nothing
        fx.WithDarktide(fx.SteamRoot);

        var result = fx.Service.Discover();

        // Default path (WindowsDefaultSteamRoot) is the fixture SteamRoot here.
        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Contains(result.Warnings, w => w.Contains("default path", StringComparison.Ordinal));
    }

    [Fact]
    public void Registry_path_invalid_falls_back_to_default()
    {
        using var fx = new SteamFixture(platform: DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.Registry.SteamPath = Path.Combine(fx.TempRoot, "does-not-exist"); // invalid
        fx.WithDarktide(fx.SteamRoot);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.SteamRoot, result.SteamInstallPath);
        Assert.Contains(result.Warnings, w => w.Contains("default path", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_registry_and_default_invalid_returns_Failed()
    {
        // Nothing scaffolded: the fixture SteamRoot dir isn't created unless a
        // layout helper runs, so it (the Windows default) + the (null) registry
        // are both invalid.
        using var fx = new SteamFixture(platform: DiscoveryPlatform.Windows);
        fx.Registry.SteamPath = null;

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Failed, result.Status);
        Assert.Null(result.SteamInstallPath);
    }

    [Fact]
    public void Missing_darktide_returns_Partial()
    {
        using var fx = new SteamFixture(platform: DiscoveryPlatform.Windows);
        fx.WithLibraryFoldersAtSteamRoot();
        fx.Registry.SteamPath = fx.SteamRoot;
        // No Darktide.

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Partial, result.Status);
        Assert.Null(result.DarktideGameBinaryPath);
    }

    [Fact]
    public void Darktide_in_secondary_library_is_found()
    {
        using var fx = new SteamFixture(platform: DiscoveryPlatform.Windows);
        var secondary = Path.Combine(fx.TempRoot, "win-secondary-lib");
        Directory.CreateDirectory(secondary);
        fx.WithLibraryFoldersAtSteamRoot(fx.SteamRoot, secondary);
        fx.Registry.SteamPath = fx.SteamRoot;
        fx.WithDarktide(secondary);

        var result = fx.Service.Discover();

        Assert.Equal(DiscoveryStatus.Complete, result.Status);
        Assert.Equal(fx.ExpectedDarktidePath(secondary), result.DarktideGameBinaryPath);
    }
}
