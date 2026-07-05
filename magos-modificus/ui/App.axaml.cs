using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Magos.Modificus.General;
using Magos.Modificus.Nxm;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.Preferences;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.UI;

/// <summary>
/// The Avalonia application. Also the startup log site: the composition root
/// runs here, config loading is logged so startup is observable, and the user's
/// preferences (theme, font scale, language) are applied to the running app
/// before the main window shows. The shell view model resolves the backend
/// services itself (no Phase-0 probe needed).
/// </summary>
public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        IServiceProvider services;
        ILogger<App> logger;

        try
        {
            services = MagosComposition.Build();
            logger = services.GetRequiredService<ILogger<App>>();
        }
        catch (NxmSingleInstanceException ex)
        {
            // Single-instance enforcement: another Magos process owns the nxm
            // IPC pipe (the bind is the claim). Exit cleanly before any window
            // shows. Surface the reason on stderr (the provider isn't available
            // here since Build threw).
            Console.Error.WriteLine(
                $"Magos is already running (nxm pipe '{ex.PipeName}' is owned by another instance); exiting.");
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
                desktopLifetime.Shutdown(1);
            return;
        }

        // A one-off startup snapshot for the startup log + applying initial
        // preferences before any window shows. This is NOT a cache: every
        // backend consumer reads live via IConfigLoader on each operation.
        var config = services.GetRequiredService<IConfigLoader>().Load();

        logger.LogInformation("Magos Modificus starting");
        logger.LogInformation(
            "Config loaded: ProfilesBaseFolder={Profiles}; ModsFolder={Mods}; " +
            "EnginseerRuntimeDir={Runtime}; LogLevel={Level}; LogFile={LogFile}",
            config.ProfilesBaseFolder,
            config.ModsFolder,
            config.EnginseerRuntimeDir,
            config.Logging.Level,
            config.Logging.LogFile);

        // Apply the user's preferences (theme, font scale, language) before any
        // window shows, so the first paint already reflects them. Swaps the XAML
        // resource placeholder for the real DI singleton, so every view's
        // {Binding [Key], Source={StaticResource Loc}} resolves through the live
        // service (culture switches refresh the whole UI through INPC).
        var localization = services.GetRequiredService<LocalizationService>();
        Resources["Loc"] = localization;
        var prefs = config.Preferences;
        services.GetRequiredService<IPreferencesService>()
            .ApplyAndPersist(prefs.Theme, prefs.FontScale, prefs.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = services.GetRequiredService<ShellViewModel>();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
