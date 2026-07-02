using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Magos.Modificus.Config;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI;

/// <summary>
/// The Avalonia application. Also the startup log site: the composition root
/// runs here, and config loading is logged so startup is observable. The shell
/// view model resolves the backend services itself (no Phase-0 probe needed).
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

        logger.LogInformation("Magos Modificus starting");
        logger.LogInformation(
            "Config loaded — ProfilesBaseFolder={Profiles}; SharedModsFolder={Shared}; " +
            "EnginseerRuntimeDir={Runtime}; LogLevel={Level}; LogFile={LogFile}",
            config.ProfilesBaseFolder,
            config.SharedModsFolder,
            config.EnginseerRuntimeDir,
            config.Logging.Level,
            config.Logging.LogFile);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = services.GetRequiredService<ShellViewModel>();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
