using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.Nxm.Tests;

/// <summary>
/// <see cref="LinuxNxmHandlerRegistrar"/> standalone-mode tests (gated to
/// Linux): Register writes the <c>.desktop</c> file with the expected content +
/// path; IsRegistered reflects the (faked) xdg-mime result; Unregister removes
/// the file. The xdg invocation is faked so the test is deterministic and does
/// not depend on a desktop environment being installed. Each registrar is built
/// with an explicit null <c>$APPIMAGE</c> accessor so the standalone path is
/// exercised regardless of whether the test host itself runs from an AppImage.
/// </summary>
public sealed class LinuxNxmHandlerRegistrarTests
{
    // Forces standalone mode (no $APPIMAGE) so these tests exercise the direct
    // handler path regardless of the test host environment.
    private static Func<string?> NoAppImage => () => null;

    [Fact]
    public void Register_writes_desktop_file_with_expected_content()
    {
        if (!OperatingSystem.IsLinux())
            return; // gated: the Linux registrar only runs on Linux.

        var dir = CreateTempApplicationsDir();
        try
        {
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => (0, "modificus-curator-nxm-handler.desktop\n"),
                appImagePathAccessor: NoAppImage);

            registrar.Register();

            var file = Path.Combine(dir, NxmHandlerPaths.LinuxDesktopFileId);
            Assert.True(File.Exists(file));
            var content = File.ReadAllText(file);
            Assert.Contains("Type=Application", content);
            Assert.Contains("Name=Modificus Curator NXM Handler", content);
            Assert.Contains("Exec=\"/opt/curator/Modificus.Curator.NxmHandler\" %u", content);
            Assert.Contains("NoDisplay=true", content);
            Assert.Contains("MimeType=x-scheme-handler/nxm;", content);
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void IsRegistered_true_when_desktop_file_present_and_xdg_reports_us()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => (0, "modificus-curator-nxm-handler.desktop\n"),
                appImagePathAccessor: NoAppImage);

            registrar.Register();
            Assert.True(registrar.IsRegistered());
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void IsRegistered_false_when_xdg_reports_another_handler()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => (0, "some-other-app.desktop\n"),
                appImagePathAccessor: NoAppImage);

            registrar.Register();
            Assert.False(registrar.IsRegistered());
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void IsRegistered_false_when_desktop_file_absent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => (0, "modificus-curator-nxm-handler.desktop\n"),
                appImagePathAccessor: NoAppImage);

            Assert.False(registrar.IsRegistered());
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void Unregister_removes_desktop_file()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => (0, ""),
                appImagePathAccessor: NoAppImage);

            registrar.Register();
            Assert.True(File.Exists(Path.Combine(dir, NxmHandlerPaths.LinuxDesktopFileId)));

            registrar.Unregister();
            Assert.False(File.Exists(Path.Combine(dir, NxmHandlerPaths.LinuxDesktopFileId)));
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    [Fact]
    public void Register_tolerates_missing_xdg_mime()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = CreateTempApplicationsDir();
        try
        {
            // xdg-mime "missing": the runXdg fake throws, simulating the binary
            // being absent. Register must NOT throw (the .desktop file is still
            // the source of truth).
            var registrar = new LinuxNxmHandlerRegistrar(
                "/opt/curator/Modificus.Curator.NxmHandler",
                NullLogger<LinuxNxmHandlerRegistrar>.Instance,
                applicationsDir: dir,
                runXdg: _ => throw new FileNotFoundException("xdg-mime not installed"),
                appImagePathAccessor: NoAppImage);

            registrar.Register();
            Assert.True(File.Exists(Path.Combine(dir, NxmHandlerPaths.LinuxDesktopFileId)));
        }
        finally
        {
            TryCleanup(dir);
        }
    }

    private static string CreateTempApplicationsDir() =>
        Path.Combine(Path.GetTempPath(), "curator-nxm-test-" + Guid.NewGuid().ToString("N"));

    private static void TryCleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }
}
