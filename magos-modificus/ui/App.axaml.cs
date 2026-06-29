using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Magos.Modificus.Config;
using Magos.Modificus.Integrations;
using Magos.Modificus.Profiles;
using Magos.Modificus.Steam;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Views;
using Magos.EnginseerClient;
using Magos.Modificus.Launcher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI;

/// <summary>
/// The Avalonia application. Also the startup log site: the composition root
/// runs here, and config + DI wiring are logged so the scaffold is observable.
/// </summary>
public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = MagosComposition.Build();
        var logger = services.GetRequiredService<ILogger<App>>();
        var config = services.GetRequiredService<MagosConfig>();

        logger.LogInformation("Magos Modificus starting (Phase 0 scaffold)");
        logger.LogInformation(
            "Config loaded — ProfilesBaseFolder={Profiles}; SharedModsFolder={Shared}; " +
            "EnginseerRuntimeDir={Runtime}; LogLevel={Level}; LogFile={LogFile}",
            config.ProfilesBaseFolder,
            config.SharedModsFolder,
            config.EnginseerRuntimeDir,
            config.Logging.Level,
            config.Logging.LogFile);

        // Resolve each domain service to prove the library Add<>() registrations
        // are wired and resolvable.
        var wired = ResolveDomainServices(services);
        logger.LogInformation("DI wired: {Count} domain services resolved", wired);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static int ResolveDomainServices(IServiceProvider services)
    {
        var count = 0;
        if (services.GetService<IProfileService>() is not null) count++;
        if (services.GetService<IModSourceService>() is not null) count++;
        if (services.GetService<ISteamService>() is not null) count++;
        if (services.GetService<IEnginseerLaunchService>() is not null) count++;
        if (services.GetService<IProfileLauncher>() is not null) count++;
        return count;
    }
}
