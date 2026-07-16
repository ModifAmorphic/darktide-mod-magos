using Avalonia;
using Avalonia.Logging;
#if CURATOR_VELOPACK
using Velopack;
#endif

namespace Modificus.Curator.UI;

internal static class Program
{
    // Avalonia + DI entry point. The DI composition root runs in
    // App.OnFrameworkInitializationCompleted (so it has access to the
    // application lifetime to install the main window).
    [STAThread]
    public static void Main(string[] args)
    {
#if CURATOR_VELOPACK
        // Velopack lifecycle hook entry point. MUST be the first thing in Main:
        // it detects install/update/uninstall hook arguments, runs any fast
        // callbacks, then exits the process. No-op on a normal app start. Kept
        // before Avalonia startup so Velopack can manage the app lifecycle.
        VelopackApp.Build().Run();
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Used by the Avalonia visual designer; mirrors the runtime setup.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(DesktopIdentityOptions.Build())
            .LogToTrace(LogEventLevel.Warning);
}

/// <summary>
/// Curator's explicit X11 desktop identity. <see cref="WmClass"/> is the single
/// C# runtime constant for the class Curator advertises on its top-level X11
/// windows; it is deliberately coupled to (must stay equal to) the Velopack
/// pack id, the <c>StartupWMClass</c> the release pipeline bakes into the
/// generated AppImage desktop file, and the <c>StartupWMClass</c>
/// <c>scripts/install.sh</c> writes into the user desktop entry. The
/// AppImage packaging smoke (<c>curator-build.yml</c>) and the installer test
/// harness (<c>scripts/tests/test-install.sh</c>) assert that coupling from
/// the packaging side; this constant is the C# side. This is the normal app
/// identity, not a runtime heuristic.
/// </summary>
internal static class DesktopIdentityOptions
{
    /// <summary>
    /// The X11 WM_CLASS Curator advertises on its top-level windows. Matches
    /// the Velopack pack id (<c>ModifAmorphic.ModificusCurator</c>) and the
    /// <c>StartupWMClass</c> the release pipeline + installer write into the
    /// Linux desktop entries, so task managers group the Curator window under
    /// Curator (not Darktide when Curator launched it from its AppImage).
    /// </summary>
    internal const string WmClass = "ModifAmorphic.ModificusCurator";

    /// <summary>
    /// Builds the <see cref="X11PlatformOptions"/> carrying Curator's
    /// <see cref="WmClass"/>. Factored as a pure factory so a test can read the
    /// configured value without initializing X11 or requiring <c>DISPLAY</c>.
    /// Production wires it via <see cref="AppBuilder.With{T}(T)"/> in
    /// <see cref="Program.BuildAvaloniaApp"/>; the platform reads WmClass from
    /// the bound options only when an X11 window is created.
    /// </summary>
    internal static X11PlatformOptions Build() => new() { WmClass = WmClass };
}
