using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;
using Magos.Modificus.General;
using Magos.Modificus.Integrations;
using Magos.Modificus.Profiles;
using Magos.Modificus.SharedMods;
using Magos.Modificus.Steam;
using Magos.Modificus.UI.Dialogs;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Views;
using Magos.Modificus.EnginseerClient;
using Magos.Modificus.Launcher;

namespace Magos.Modificus.UI;

/// <summary>
/// The DI composition root. Loads config, builds the structured logger, wires
/// every library's <c>Add&lt;Library&gt;()</c> extension, and registers the UI
/// surface (main window + view model + dialog service). All data operations flow
/// through the registered library services — the UI never touches files or APIs
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
        // the same one as the owner for modal dialogs.
        services.AddSingleton<MainWindow>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IDialogService>(sp =>
            new DialogService(
                sp.GetRequiredService<MainWindow>(),
                sp.GetRequiredService<IProfileService>(),
                sp.GetRequiredService<ISteamService>()));

        return services.BuildServiceProvider();
    }
}
