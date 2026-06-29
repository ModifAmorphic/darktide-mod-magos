using Avalonia;
using Avalonia.Logging;

namespace Magos.Modificus.UI;

internal static class Program
{
    // Avalonia + DI entry point. The DI composition root runs in
    // App.OnFrameworkInitializationCompleted (so it has access to the
    // application lifetime to install the main window).
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Used by the Avalonia visual designer; mirrors the runtime setup.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace(LogEventLevel.Warning);
}
