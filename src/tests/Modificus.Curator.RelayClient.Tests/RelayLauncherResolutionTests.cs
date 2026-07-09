namespace Modificus.Curator.RelayClient.Tests;

/// <summary>
/// Precedence tests for <see cref="RelayLaunchService.ResolveLauncherPath"/>:
/// the configured RelayDir wins when its launcher is present; otherwise the
/// Windows app-local fallback (the Velopack payload's <c>relay/</c> folder) is
/// used on Windows; otherwise <c>null</c>. The helper is a pure function of
/// (configRelayDir, baseDirectory, isWindows), so the Windows branch is
/// exercisable on any CI OS by passing <c>isWindows</c> explicitly.
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
    public void App_local_relay_is_ignored_on_linux()
    {
        // Linux never consults the app-local fallback, even when the payload is
        // present: Relay lives at the data-root relay folder (config.RelayDir).
        using var fx = new RelayFixture();
        fx.DeleteLauncher();
        var (appBase, _) = DeployAppLocalRelay(fx);

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, appBase, isWindows: false);

        Assert.Null(resolved);
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
    public void Returns_null_when_neither_configured_nor_app_local_has_the_launcher()
    {
        using var fx = new RelayFixture();
        fx.DeleteLauncher();

        var resolved = RelayLaunchService.ResolveLauncherPath(
            fx.Config.RelayDir, AppContext.BaseDirectory, isWindows: true);

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
}
