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
            .LogToTrace(LogEventLevel.Warning);
}
