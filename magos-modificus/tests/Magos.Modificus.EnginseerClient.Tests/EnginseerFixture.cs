using Magos.Modificus.Config;
using Magos.Modificus.Profiles;
using Magos.Modificus.Steam;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.EnginseerClient.Tests;

/// <summary>
/// Per-test fixture: scaffolds a temp Enginseer runtime dir with a stub
/// <c>magos_launcher.exe</c> (so the runtime-dir check passes), and supplies
/// fakes for <see cref="IProfileService"/> + <see cref="ISteamService"/> +
/// <see cref="IProcessLauncher"/>. Builds the internal
/// <see cref="EnginseerLaunchService"/> with a concrete
/// <see cref="IPlatformLaunchStrategy"/> (backed by the fake launcher) so both
/// the Windows and Linux code paths are exercisable on any CI OS. Disposes the
/// temp tree on teardown so tests are isolated regardless of outcome.
/// </summary>
/// <remarks>
/// Mirrors the Steam library's SteamFixture: resolve the seams as fakes, drive
/// the service under test, assert on the recorded side-effects. The service is
/// constructed via its DI constructor with the chosen strategy; the DI path is
/// covered separately in the service-collection tests.
/// </remarks>
internal sealed class EnginseerFixture : IDisposable
{
    public string TempRoot { get; }
    public string RuntimeDir { get; }
    public FakeProfileService Profiles { get; } = new();
    public FakeSteamService Steam { get; } = new();
    public FakeProcessLauncher Launcher { get; } = new();
    public MagosConfig Config { get; }

    public EnginseerFixture()
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "magos-enginseer-" + Guid.NewGuid().ToString("N"));
        RuntimeDir = Path.Combine(TempRoot, "enginseer");
        Directory.CreateDirectory(RuntimeDir);

        // Deploy a stub launcher.exe so the runtime-dir existence check passes
        // for the success-path tests. Tests that need it absent call DeleteLauncher().
        LauncherPath = Path.Combine(RuntimeDir, EnginseerLaunchService.LauncherExecutableName);
        File.WriteAllText(LauncherPath, string.Empty);

        Config = MagosConfig.CreateDefault();
        Config.EnginseerRuntimeDir = RuntimeDir;
    }

    /// <summary>The full path to the stub launcher in the temp runtime dir.</summary>
    public string LauncherPath { get; }

    /// <summary>
    /// Builds the service under test wired for a Windows launch (direct
    /// invocation, untranslated args) — the real <see cref="WindowsLaunchStrategy"/>
    /// driven by the fixture's fake <see cref="IProcessLauncher"/>.
    /// </summary>
    public EnginseerLaunchService BuildWindowsService() =>
        BuildService(new WindowsLaunchStrategy(Launcher, NullLogger<WindowsLaunchStrategy>.Instance));

    /// <summary>
    /// Builds the service under test wired for a Linux launch (<c>proton run</c>
    /// + both <c>STEAM_COMPAT_*</c> env vars + <c>Z:\</c>-translated args) — the
    /// real <see cref="LinuxLaunchStrategy"/> driven by the fixture's fake
    /// <see cref="IProcessLauncher"/>.
    /// </summary>
    public EnginseerLaunchService BuildLinuxService() =>
        BuildService(new LinuxLaunchStrategy(Launcher, NullLogger<LinuxLaunchStrategy>.Instance));

    /// <summary>Builds the service under test with an explicit strategy.</summary>
    public EnginseerLaunchService BuildService(IPlatformLaunchStrategy strategy) =>
        new(Profiles, Steam, Config, strategy, NullLogger<EnginseerLaunchService>.Instance);

    /// <summary>Removes the stub launcher so the runtime-dir check fails.</summary>
    public void DeleteLauncher()
    {
        if (File.Exists(LauncherPath))
        {
            File.Delete(LauncherPath);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(TempRoot))
        {
            try { Directory.Delete(TempRoot, recursive: true); }
            catch (IOException) { /* best-effort: temp dirs are harmless if left */ }
        }
    }
}
