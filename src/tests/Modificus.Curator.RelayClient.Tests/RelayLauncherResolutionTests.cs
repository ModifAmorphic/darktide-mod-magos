namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Precedence tests for <see cref="RelayLaunchService.ResolveLauncherPath"/>:
/// the configured RelayDir wins when its launcher is present (on both Linux and
/// Windows); otherwise the app-local fallback (the Velopack payload's
/// <c>relay/</c> folder, used on both OSes) is used; otherwise the Windows-only
/// sibling-folder fallback (<c>&lt;base&gt;/../relay/</c>, the portable archive
/// layout) is used on Windows; otherwise <c>null</c>. The helper is a pure
/// function of (configRelayDir, baseDirectory, isWindows), so the Windows
/// branches are exercisable on any CI OS by passing <c>isWindows</c> explicitly.
/// </summary>
public sealed class RelayLauncherResolutionTests
{
    [Fact]
    public void Configured_RelayDir_wins_when_its_launcher_is_present()
    {
        // RelayFixture deploys a stub launcher at fx.Config.RelayDir.
        using var fx = new RelayFixture();

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, AppContext.BaseDirectory, isWindows: true);

        Assert.Equal(fx.LauncherPath, resolved);
    }

    [Fact]
    public void Configured_RelayDir_wins_on_linux_when_its_launcher_is_present()
    {
        // The configured RelayDir is honored on Linux too: the standalone
        // layout deploys Relay at the data root, and that path takes priority
        // over any packaged fallback.
        using var fx = new RelayFixture();

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, AppContext.BaseDirectory, isWindows: false);

        Assert.Equal(fx.LauncherPath, resolved);
    }

    [Fact]
    public void App_local_relay_is_used_when_configured_RelayDir_is_missing_on_windows()
    {
        // Velopack install on Windows: the data-root RelayDir has no launcher,
        // but the app-local payload ships one under <appDir>/relay/.
        using var fx = new RelayFixture();
        fx.DeleteLauncher();
        var (appBase, appLocalLauncher) = DeployAppLocalRelay(fx);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: true);

        Assert.Equal(appLocalLauncher, resolved);
    }

    [Fact]
    public void App_local_relay_is_used_on_linux_when_configured_RelayDir_is_missing()
    {
        // AppImage on Linux: the data-root RelayDir has no launcher, but the
        // app-local payload (mounted inside the AppImage) ships one under
        // <appDir>/relay/. This is the AppImage case; the standalone layout
        // keeps Relay at the data root and never reaches this fallback.
        using var fx = new RelayFixture();
        fx.DeleteLauncher();
        var (appBase, appLocalLauncher) = DeployAppLocalRelay(fx);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: false);

        Assert.Equal(appLocalLauncher, resolved);
    }

    [Fact]
    public void Configured_RelayDir_wins_over_app_local_when_both_have_the_launcher()
    {
        // Both the configured dir and the app-local payload have the launcher.
        // The configured dir wins: it honors an explicit user override and the
        // data-root default once Relay is deployed there.
        using var fx = new RelayFixture();
        var (appBase, _) = DeployAppLocalRelay(fx);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: true);

        Assert.Equal(fx.LauncherPath, resolved);
    }

    [Fact]
    public void Sibling_relay_is_used_when_configured_and_app_local_miss_on_windows()
    {
        // Portable Windows archive: Curator under <root>/app/, Relay under
        // <root>/relay/ (a sibling of the app folder). With neither the
        // configured RelayDir nor the app-local payload holding a launcher, the
        // sibling fallback resolves it without a config override.
        using var fx = new RelayFixture();
        fx.DeleteLauncher();
        var (appBase, _, siblingLauncher) = DeployPortableLayout(fx, withAppLocal: false);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: true);

        Assert.Equal(siblingLauncher, resolved);
    }

    [Fact]
    public void App_local_relay_wins_over_sibling_when_both_are_present_on_windows()
    {
        // Precedence within the Windows packaged fallbacks: app-local
        // (<base>/relay/, Velopack) takes priority over the sibling
        // (<base>/../relay/, portable archive) when both ship a launcher.
        using var fx = new RelayFixture();
        fx.DeleteLauncher();
        var (appBase, appLocalLauncher, _) = DeployPortableLayout(fx, withAppLocal: true);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: true);

        Assert.Equal(appLocalLauncher, resolved);
    }

    [Fact]
    public void Configured_RelayDir_wins_over_app_local_and_sibling_when_all_present_on_windows()
    {
        // Full precedence chain: configured > app-local > sibling. With a
        // launcher in all three locations, the configured RelayDir wins.
        using var fx = new RelayFixture();
        var (appBase, _, _) = DeployPortableLayout(fx, withAppLocal: true);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: true);

        Assert.Equal(fx.LauncherPath, resolved);
    }

    [Fact]
    public void Sibling_relay_is_ignored_on_linux()
    {
        // Linux never consults the Windows-only sibling fallback
        // (<base>/../relay/), even when the portable sibling layout is present
        // and no app-local launcher is deployed: Relay lives at the data-root
        // relay folder (config.RelayDir) or the app-local payload, never the
        // sibling archive layout.
        using var fx = new RelayFixture();
        fx.DeleteLauncher();
        var (appBase, _, _) = DeployPortableLayout(fx, withAppLocal: false);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: false);

        Assert.Null(resolved);
    }

    [Fact]
    public void Returns_null_when_configured_app_local_and_sibling_all_miss_on_windows()
    {
        // No launcher anywhere: configured RelayDir empty, app base has no
        // app-local relay and no sibling relay. A clean, empty app base keeps
        // this deterministic regardless of the test runner's own layout.
        using var fx = new RelayFixture();
        fx.DeleteLauncher();
        var appBase = Path.Combine(fx.TempRoot, "empty-app");
        Directory.CreateDirectory(appBase);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: true);

        Assert.Null(resolved);
    }

    [Fact]
    public void Returns_null_on_linux_when_configured_and_app_local_both_miss()
    {
        // Linux has no sibling fallback, so "all missing" on Linux means the
        // configured RelayDir and the app-local payload are both absent.
        using var fx = new RelayFixture();
        fx.DeleteLauncher();
        var appBase = Path.Combine(fx.TempRoot, "empty-app");
        Directory.CreateDirectory(appBase);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: false);

        Assert.Null(resolved);
    }

    // Deploy a fake app-local Relay payload (a stub launcher under <appBase>/relay/)
    // rooted inside the fixture's temp tree so it is cleaned up on disposal.
    private static (string appBase, string appLocalLauncher) DeployAppLocalRelay(RelayFixture fx)
    {
        var appBase = Path.Combine(fx.TempRoot, "app");
        var appLocalRelay = Path.Combine(appBase, RelayLaunchService.AppLocalRelayFolderName);
        Directory.CreateDirectory(appLocalRelay);
        var appLocalLauncher = Path.Combine(appLocalRelay, RelayLaunchService.LauncherExecutableName);
        File.WriteAllText(appLocalLauncher, string.Empty);
        return (appBase, appLocalLauncher);
    }

    // Deploy the portable archive layout under <root>/app + <root>/relay
    // (a sibling of the app folder), rooted inside the fixture's temp tree so it
    // is cleaned up on disposal. When withAppLocal is true, also deploys an
    // app-local launcher under <root>/app/relay/ so a test can assert precedence
    // between the two Windows fallbacks against a single app base.
    private static (string appBase, string appLocalLauncher, string siblingLauncher) DeployPortableLayout(
        RelayFixture fx, bool withAppLocal)
    {
        var root = Path.Combine(fx.TempRoot, "portable");
        var appBase = Path.Combine(root, "app");
        Directory.CreateDirectory(appBase);

        var appLocalRelay = Path.Combine(appBase, RelayLaunchService.AppLocalRelayFolderName);
        Directory.CreateDirectory(appLocalRelay);
        var appLocalLauncher = Path.Combine(appLocalRelay, RelayLaunchService.LauncherExecutableName);
        if (withAppLocal)
        {
            File.WriteAllText(appLocalLauncher, string.Empty);
        }

        var siblingRelay = Path.Combine(root, RelayLaunchService.AppLocalRelayFolderName);
        Directory.CreateDirectory(siblingRelay);
        var siblingLauncher = Path.Combine(siblingRelay, RelayLaunchService.LauncherExecutableName);
        File.WriteAllText(siblingLauncher, string.Empty);

        return (appBase, appLocalLauncher, siblingLauncher);
    }
}
