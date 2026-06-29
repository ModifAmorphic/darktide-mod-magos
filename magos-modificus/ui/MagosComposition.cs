using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;
using Magos.Modificus.General;
using Magos.Modificus.Integrations;
using Magos.Modificus.Profiles;
using Magos.Modificus.Steam;
using Magos.Modificus.UI.ViewModels;
using Magos.Modificus.UI.Views;
using Magos.EnginseerClient;
using Magos.Modificus.Launcher;

namespace Magos.Modificus.UI;

/// <summary>
/// The DI composition root. Loads config, builds the structured logger, wires
/// every library's <c>Add&lt;Library&gt;()</c> extension, and registers the UI
/// surface (main window + view model). All data operations flow through the
/// registered library services — the UI never touches files or APIs directly.
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
        var services = new ServiceCollection();
        services.AddGeneral(config, loggerFactory);
        services.AddProfiles();
        services.AddIntegrations();
        services.AddSteam();
        services.AddEnginseerClient();
        services.AddLauncher();
        services.AddTransient<MainWindow>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
