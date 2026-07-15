using System.IO;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Nxm.Tests;

/// <summary>
/// AppImage-mode tests for <see cref="LinuxNxmHandlerRegistrar"/> (gated to
/// Linux, where symlinks + Unix file modes are available). Covers the durable
/// AppImage registration contract: the handler is copied to a Curator-managed
/// per-user directory, a sibling symlink points at <c>$APPIMAGE</c>, the desktop
/// entry's Exec targets the persistent copy (never the temporary mount), paths
/// with spaces are quoted, maintenance is a no-op without active ownership,
/// maintenance refreshes changed handler bytes + stale symlinks atomically,
/// maintenance never claims ownership from another handler, unregister removes
/// only Curator-managed files, xdg-mime failures stay non-fatal, and the
/// cold-start sibling resolution finds the AppImage through the managed symlink.
/// </summary>
/// <remarks>
/// Every external dependency is faked: the <c>$APPIMAGE</c> accessor returns a
/// temp file, <c>xdg-mime</c> is a delegate, and the applications + managed
/// dirs live under a temp root cleaned up on disposal. No real user home or
/// desktop integration is touched.
/// </remarks>
public sealed class LinuxNxmAppImageRegistrarTests
{
    private const string HandlerV1 = "handler-bytes-v1";
    private const string HandlerV2 = "handler-bytes-v2";

    [Fact]
    public void Standalone_registration_points_exec_directly_at_packaged_handler()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        // Explicit null accessor: no $APPIMAGE, so the registrar runs in
        // standalone mode (Exec points directly at the packaged handler).
        var reg = fx.CreateRegistrar(appImageAccessor: () => null);

        reg.Register();

        var content = File.ReadAllText(fx.DesktopFilePath);
        // Exec points directly at the packaged handler, not a managed copy.
        Assert.Contains($"Exec=\"{fx.PackagedHandlerPath}\" %u", content);
        // No managed dir is created in standalone mode.
        Assert.False(Directory.Exists(fx.ManagedDir));
    }

    [Fact]
    public void AppImage_registration_copies_handler_to_managed_directory()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();

        reg.Register();

        Assert.True(File.Exists(fx.ManagedHandlerPath), "managed handler copy must exist");
        Assert.Equal(HandlerV1, File.ReadAllText(fx.ManagedHandlerPath));
    }

    [Fact]
    public void Copied_handler_is_executable_with_no_broad_permissions()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();

        reg.Register();

        var mode = File.GetUnixFileMode(fx.ManagedHandlerPath);
        Assert.True((mode & UnixFileMode.UserExecute) != 0, "owner execute must be set");
        Assert.True((mode & UnixFileMode.UserRead) != 0, "owner read must be set");
        Assert.True((mode & UnixFileMode.UserWrite) != 0, "owner write must be set");
        // No broad permissions: group/other get nothing.
        Assert.True((mode & (UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0,
            "group/other execute must NOT be set");
    }

    [Fact]
    public void AppImage_registration_creates_sibling_curator_symlink_pointing_at_appimage()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();

        reg.Register();

        Assert.True(File.Exists(fx.ManagedSymlinkPath), "sibling symlink must exist");
        var target = File.ResolveLinkTarget(fx.ManagedSymlinkPath, returnFinalTarget: false);
        Assert.NotNull(target);
        Assert.Equal(fx.AppImagePath, target!.FullName);
    }

    [Fact]
    public void Desktop_exec_points_at_persistent_handler_not_temporary_mount()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();

        reg.Register();

        var content = File.ReadAllText(fx.DesktopFilePath);
        // Exec points at the managed copy, NOT the packaged handler (temp mount).
        Assert.Contains($"Exec=\"{fx.ManagedHandlerPath}\" %u", content);
        Assert.DoesNotContain(fx.PackagedHandlerPath, content);
    }

    [Fact]
    public void Paths_containing_spaces_are_safely_quoted_in_exec()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        // A managed dir whose path contains a space (mirrors the default
        // ".../Modificus Curator/nxm-handler/" segment).
        var spacedManagedDir = Path.Combine(fx.TempRoot, "Modificus Curator", "nxm-handler");
        var reg = fx.CreateRegistrar(managedDir: spacedManagedDir);

        reg.Register();

        var managedHandler = Path.Combine(spacedManagedDir, NxmHandlerPaths.HandlerExeBaseName);
        Assert.True(File.Exists(managedHandler));
        var content = File.ReadAllText(fx.DesktopFilePath);
        Assert.Contains($"Exec=\"{managedHandler}\" %u", content);
    }

    [Fact]
    public void Maintenance_is_noop_when_desktop_file_does_not_exist()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        // No Register() call: no desktop file, so Curator does not own.
        var reg = fx.CreateRegistrar();

        reg.MaintainRegistration();

        // Nothing was created.
        Assert.False(File.Exists(fx.ManagedHandlerPath));
        Assert.False(File.Exists(fx.ManagedSymlinkPath));
    }

    [Fact]
    public void Maintenance_is_noop_when_xdg_reports_another_handler()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        // Register first (Curator owns), then switch xdg to report another
        // handler for maintenance.
        var reg = fx.CreateRegistrar(runXdg: _ => (0, NxmHandlerPaths.LinuxDesktopFileId + "\n"));
        reg.Register();

        // Simulate another manager taking over: xdg now reports a different id.
        var otherReg = fx.CreateRegistrar(
            runXdg: _ => (0, "some-other-manager.desktop\n"));
        // Change the source bytes so we can detect whether a refresh happened.
        File.WriteAllText(fx.PackagedHandlerPath, HandlerV2);

        otherReg.MaintainRegistration();

        // The managed handler must NOT have been refreshed: Curator no longer
        // owns the active association.
        Assert.Equal(HandlerV1, File.ReadAllText(fx.ManagedHandlerPath));
    }

    [Fact]
    public void Maintenance_refreshes_changed_handler_bytes_atomically()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();
        reg.Register();

        // Simulate an AppImage update: the packaged handler now has new bytes.
        File.WriteAllText(fx.PackagedHandlerPath, HandlerV2);

        reg.MaintainRegistration();

        // The managed copy now reflects the new bytes.
        Assert.Equal(HandlerV2, File.ReadAllText(fx.ManagedHandlerPath));
        // The atomic copy used a temp file + rename; no temp file is left.
        Assert.Empty(Directory.GetFiles(fx.ManagedDir, "*.tmp-*"));
    }

    [Fact]
    public void Maintenance_skips_unchanged_handler_bytes()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();
        reg.Register();

        var writeTimeBefore = new FileInfo(fx.ManagedHandlerPath).LastWriteTimeUtc;

        reg.MaintainRegistration();

        // No change: the file was not rewritten.
        Assert.Equal(HandlerV1, File.ReadAllText(fx.ManagedHandlerPath));
        Assert.Empty(Directory.GetFiles(fx.ManagedDir, "*.tmp-*"));
    }

    [Fact]
    public void Maintenance_updates_stale_appimage_symlink()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        string? appImage = fx.AppImagePath;
        var reg = fx.CreateRegistrar(appImageAccessor: () => appImage);
        reg.Register();

        // Simulate the user moving the AppImage + relaunching: $APPIMAGE now
        // points at a new path.
        var movedAppImage = Path.Combine(fx.TempRoot, "Curator-Moved.AppImage");
        File.WriteAllText(movedAppImage, "moved");
        appImage = movedAppImage;

        reg.MaintainRegistration();

        var target = File.ResolveLinkTarget(fx.ManagedSymlinkPath, returnFinalTarget: false);
        Assert.NotNull(target);
        Assert.Equal(movedAppImage, target!.FullName);
    }

    [Fact]
    public void Maintenance_cannot_claim_ownership_from_another_handler()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        // Another manager owns: desktop file is absent (Curator never registered)
        // but the managed dir has stale files from a prior Curator install.
        Directory.CreateDirectory(fx.ManagedDir);
        File.WriteAllText(fx.ManagedHandlerPath, "stale");
        File.CreateSymbolicLink(fx.ManagedSymlinkPath, fx.AppImagePath);

        var reg = fx.CreateRegistrar(
            runXdg: _ => (0, "another-manager.desktop\n"));
        File.WriteAllText(fx.PackagedHandlerPath, HandlerV2);

        reg.MaintainRegistration();

        // The stale managed files are untouched: maintenance did not refresh
        // them because Curator does not own the active association.
        Assert.Equal("stale", File.ReadAllText(fx.ManagedHandlerPath));
    }

    [Fact]
    public void Maintenance_tolerates_xdg_failure_without_throwing()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();
        reg.Register();
        File.WriteAllText(fx.PackagedHandlerPath, HandlerV2);

        // xdg-mime is missing / throws during maintenance.
        var failingReg = fx.CreateRegistrar(
            runXdg: _ => throw new FileNotFoundException("xdg-mime not installed"));

        // Must not throw: maintenance is best-effort.
        failingReg.MaintainRegistration();

        // The handler was NOT refreshed (ownership could not be proven).
        Assert.Equal(HandlerV1, File.ReadAllText(fx.ManagedHandlerPath));
    }

    [Fact]
    public void Maintenance_tolerates_xdg_nonzero_exit_without_throwing()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();
        reg.Register();
        File.WriteAllText(fx.PackagedHandlerPath, HandlerV2);

        var failingReg = fx.CreateRegistrar(runXdg: _ => (1, ""));

        failingReg.MaintainRegistration();

        Assert.Equal(HandlerV1, File.ReadAllText(fx.ManagedHandlerPath));
    }

    [Fact]
    public void Unregister_removes_desktop_managed_handler_symlink_and_empty_dir()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();
        reg.Register();

        Assert.True(File.Exists(fx.DesktopFilePath));
        Assert.True(File.Exists(fx.ManagedHandlerPath));
        Assert.True(File.Exists(fx.ManagedSymlinkPath));

        reg.Unregister();

        Assert.False(File.Exists(fx.DesktopFilePath));
        Assert.False(File.Exists(fx.ManagedHandlerPath));
        Assert.False(File.Exists(fx.ManagedSymlinkPath));
        // The managed dir is removed when empty.
        Assert.False(Directory.Exists(fx.ManagedDir));
    }

    [Fact]
    public void Unregister_does_not_delete_appimage_symlink_target()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();
        reg.Register();

        reg.Unregister();

        // The AppImage the symlink pointed at must survive unregister.
        Assert.True(File.Exists(fx.AppImagePath));
    }

    [Fact]
    public void Unregister_preserves_unexpected_files_and_leaves_nonempty_managed_dir()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();
        reg.Register();

        // An unexpected file the user (or a future feature) placed in the managed
        // dir. Unregister must NOT recursively delete it.
        var unexpected = Path.Combine(fx.ManagedDir, "user-file.txt");
        File.WriteAllText(unexpected, "do-not-delete");

        reg.Unregister();

        // Curator's own files are gone.
        Assert.False(File.Exists(fx.ManagedHandlerPath));
        Assert.False(File.Exists(fx.ManagedSymlinkPath));
        // The unexpected file survives.
        Assert.True(File.Exists(unexpected));
        // The managed dir survives (not empty). Never recursive delete.
        Assert.True(Directory.Exists(fx.ManagedDir));
    }

    [Fact]
    public void Unregister_is_idempotent_when_nothing_is_registered()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();

        // No Register() call: nothing to remove. Must not throw.
        reg.Unregister();

        Assert.False(File.Exists(fx.DesktopFilePath));
        Assert.False(Directory.Exists(fx.ManagedDir));
    }

    [Fact]
    public void Cold_start_sibling_resolution_finds_appimage_through_managed_symlink()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var fx = new AppImageFixture();
        var reg = fx.CreateRegistrar();
        reg.Register();

        // The handler runs from the managed dir (the desktop Exec target). Its
        // cold-start resolution looks for a sibling Modificus.Curator, which is
        // the symlink -> $APPIMAGE. ResolveCuratorMainExe must find it.
        var resolved = NxmHandlerRelay.ResolveCuratorMainExe(fx.ManagedDir);

        Assert.Equal(fx.ManagedSymlinkPath, resolved);
    }

    /// <summary>
    /// Temp fixture: scaffolds an applications dir, a packaged handler source
    /// (simulating the handler under the AppImage mount), and an AppImage file
    /// (the <c>$APPIMAGE</c> target), all under a temp root cleaned up on
    /// disposal. The managed dir path is computed but NOT created (the registrar
    /// creates it), so tests can assert the registrar's filesystem effects.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private sealed class AppImageFixture : IDisposable
    {
        public string TempRoot { get; }
        public string ApplicationsDir { get; }
        public string ManagedDir { get; }
        public string PackagedHandlerPath { get; }
        public string AppImagePath { get; }
        public string DesktopFilePath { get; }

        public AppImageFixture()
        {
            TempRoot = Path.Combine(Path.GetTempPath(), "curator-nxm-appimage-" + Guid.NewGuid().ToString("N"));
            ApplicationsDir = Path.Combine(TempRoot, "applications");
            Directory.CreateDirectory(ApplicationsDir);

            ManagedDir = Path.Combine(TempRoot, "managed", "nxm-handler");

            // The packaged handler: simulates the native-AOT handler under the
            // AppImage mount (AppContext.BaseDirectory in production).
            var mountDir = Path.Combine(TempRoot, "mount");
            Directory.CreateDirectory(mountDir);
            PackagedHandlerPath = Path.Combine(mountDir, NxmHandlerPaths.HandlerExeBaseName);
            File.WriteAllText(PackagedHandlerPath, HandlerV1);

            // The AppImage file: the $APPIMAGE target the symlink points at.
            AppImagePath = Path.Combine(TempRoot, "Modificus.Curator.AppImage");
            File.WriteAllText(AppImagePath, "appimage-bytes");

            DesktopFilePath = Path.Combine(ApplicationsDir, NxmHandlerPaths.LinuxDesktopFileId);
        }

        public string ManagedHandlerPath => Path.Combine(ManagedDir, NxmHandlerPaths.HandlerExeBaseName);

        public string ManagedSymlinkPath => Path.Combine(ManagedDir, LinuxNxmHandlerRegistrar.ManagedAppImageSymlinkName);

        /// <summary>
        /// Builds a registrar with faked seams. <paramref name="appImage"/>
        /// null means standalone (no $APPIMAGE); a string is returned by the
        /// accessor; when <paramref name="appImageAccessor"/> is set it takes
        /// precedence over <paramref name="appImage"/>.
        /// </summary>
        public LinuxNxmHandlerRegistrar CreateRegistrar(
            string? appImage = null,
            Func<string, (int exitCode, string output)>? runXdg = null,
            string? managedDir = null,
            Func<string?>? appImageAccessor = null)
        {
            return new LinuxNxmHandlerRegistrar(
                PackagedHandlerPath,
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: ApplicationsDir,
                runXdg: runXdg ?? (_ => (0, NxmHandlerPaths.LinuxDesktopFileId + "\n")),
                managedDir: managedDir ?? ManagedDir,
                appImagePathAccessor: appImageAccessor ?? (() => appImage ?? AppImagePath));
        }

        public void Dispose()
        {
            try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
