using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;
using Magos.Modificus.General;
using Magos.Modificus.Integrations;
using Magos.Modificus.Profiles;
using Magos.Modificus.SharedMods;
using Magos.Modificus.Steam;
using Magos.Modificus.UI.Dialogs;
using Magos.Modificus.UI.Localization;
using Magos.Modificus.UI.Preferences;
using Magos.Modificus.UI.Session;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Views;
using Magos.Modificus.EnginseerClient;
using Magos.Modificus.Launcher;

namespace Magos.Modificus.UI;

/// <summary>
/// The DI composition root. Loads config, builds the structured logger, wires
/// every library's <c>Add&lt;Library&gt;()</c> extension, and registers the UI
/// surface (main window + view model + dialog service). All data operations flow
/// through the registered library services; the UI never touches files or APIs
/// directly.
/// </summary>
public static class MagosComposition
{
    /// <summary>Builds and returns the application service provider.</summary>
    public static IServiceProvider Build()
    {
        // 1. Load config (defaults + JSON overrides). Logging needs this first.
        var config = new ConfigLoader().Load();

        // 2. Build the structured logger (console + file, config-honored).
        var loggerFactory = LoggingBootstrap.CreateLoggerFactory(config);

        // 3. Compose services: General infra + every domain library + UI.
        //    AddSharedMods() is called explicitly (and idempotently again inside
        //    AddProfiles()) so the shared store is discoverable at the root +
        //    IProfileService always resolves its staging dependency.
        var services = new ServiceCollection();
        services.AddGeneral(config, loggerFactory);
        services.AddSharedMods();
        services.AddProfiles();
        services.AddIntegrations();
        services.AddSteam();
        services.AddEnginseerClient();
        services.AddLauncher();

        // UI surface. MainWindow is a singleton: the desktop lifetime installs
        // the resolved instance as desktop.MainWindow, and DialogService resolves
        // the same one as the owner for modal dialogs. IProfileSession is the
        // single active-profile + running-state authority shared by the shell and
        // the manage-profiles dialog (its polling timer drives the live status).
        // LocalizationService + IPreferencesService are the i18n + preference
        // authorities (singletons so the whole app shares one culture + theme).
        services.AddSingleton<IProfileSession>(sp => new ProfileSession(
            sp.GetRequiredService<ISteamService>(),
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IAppStateStore>(),
            StartRunningStatePolling));
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IDialogService>(sp =>
            new DialogService(
                sp.GetRequiredService<MainWindow>(),
                sp.GetRequiredService<IProfileService>(),
                sp.GetRequiredService<IProfileSession>(),
                sp.GetRequiredService<IPreferencesService>(),
                sp.GetRequiredService<LocalizationService>(),
                sp.GetRequiredService<MagosConfig>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The live running-state poll: a <see cref="DispatcherTimer"/> that pings
    /// <see cref="ISteamService.IsGameRunning"/> every few seconds so the status
    /// strip + launch-availability + dropdown-enable react to the game starting or
    /// stopping while Magos is open. Runs on the UI thread (composition happens
    /// during app startup, also on the UI thread).
    /// </summary>
    private static void StartRunningStatePolling(Action onTick)
    {
        var timer = new DispatcherTimer
        {
            Interval = ProfileSession.PollInterval,
        };
        timer.Tick += (_, _) => onTick();
        timer.Start();
    }
}
