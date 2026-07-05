using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Magos.Modificus.General;
using Magos.Modificus.Integrations;
using Magos.Modificus.Nxm;
using Magos.Modificus.Profiles;
using Magos.Modificus.Mods;
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
        // 1. One config loader: used for the transient startup snapshot (to
        //    build the logger) AND registered as the live-read IConfigLoader
        //    singleton so every consumer re-reads the current disk state on
        //    each operation (the config file is tiny; a startup cache would
        //    only create staleness for the upcoming Settings window + mod-
        //    repository relocation, which write config at runtime).
        var loader = new ConfigLoader();

        // The startup snapshot feeds the logger (logging config is a one-off;
        // it does not change at runtime in v1).
        var config = loader.Load();

        // 2. Build the structured logger (console + file, config-honored).
        var loggerFactory = LoggingBootstrap.CreateLoggerFactory(config);

        // 3. Compose services: General infra + every domain library + UI.
        //    The same loader instance is registered as the IConfigLoader
        //    singleton before AddGeneral (which TryAdd-skips its own default).
        //    AddMods() is called explicitly (and idempotently again inside
        //    AddProfiles()) so the repository is discoverable at the root +
        //    IProfileService always resolves its staging dependency.
        var services = new ServiceCollection();
        services.AddSingleton<IConfigLoader>(loader);
        services.AddGeneral(loggerFactory);
        services.AddMods();
        services.AddProfiles();
        services.AddIntegrations();
        services.AddSteam();
        services.AddEnginseerClient();
        services.AddLauncher();
        // The nxm scheme-handler plumbing: IPC server (single-instance via
        // process enumeration, pipe bind degrades gracefully on failure), router
        // + no-op handler defaults, and the platform OS registrar. The IPC
        // server is bound + started after the provider is built (see
        // StartNxmServer).
        services.AddNxm();

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
        // The active profile's mod-list VM: a singleton (one list, the dominant
        // content area). Resolves IModImportService (via AddMods) +
        // IModOrderResolver (via AddProfiles), both already registered above.
        services.AddSingleton<ModListViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IDialogService>(sp =>
            new DialogService(
                sp.GetRequiredService<MainWindow>(),
                sp.GetRequiredService<IProfileService>(),
                sp.GetRequiredService<IProfileSession>(),
                sp.GetRequiredService<IPreferencesService>(),
                sp.GetRequiredService<LocalizationService>(),
                sp.GetRequiredService<IConfigLoader>(),
                sp.GetRequiredService<IModRepository>(),
                sp.GetRequiredService<ILoggerFactory>()));

        var provider = services.BuildServiceProvider();

        // Startup prune: drop repository versions no profile references + empty
        // containers (spec §5). Best-effort: a failure is logged + swallowed so
        // cleanup never blocks startup (the repository is still usable, and the
        // next startup retries).
        RunStartupPrune(provider, loggerFactory);

        // Startup discovery: validate + heal + persist. The persisted overrides
        // are populated from the platform discoverer when missing/non-existent
        // (so the Settings window shows the resolved paths rather than blanks).
        // Non-blocking: a missing-fields result is logged as a warning + the
        // user can still use the app (browse mods, manage profiles); they just
        // cannot launch until resolved (the launch-time Discover re-checks and
        // surfaces the escape-hatch when incomplete).
        RunStartupDiscovery(provider, loggerFactory);

        // Start the nxm IPC server. Bind runs two checks: (1) single-instance via
        // process enumeration, which throws NxmSingleInstanceException if another
        // Magos is running (propagates out of Build() so the caller, App, shuts
        // down before the window shows); and (2) the pipe bind, which degrades
        // gracefully on IOException (the app continues without the IPC server).
        // Intentionally NOT wrapped in a try/catch (unlike the best-effort prune
        // + discovery above): a single-instance violation is fatal-by-design for
        // this process. The pipe-bind degradation is handled inside Bind itself.
        StartNxmServer(provider, loggerFactory.CreateLogger(nameof(MagosComposition)));

        return provider;
    }

    /// <summary>
    /// Runs <see cref="ModCleanup.PruneUnreferenced"/> once after composition.
    /// Best-effort: any failure is logged + swallowed so a cleanup failure never
    /// blocks app startup (the repository is still usable; the next startup
    /// retries).
    /// </summary>
    private static void RunStartupPrune(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            var logger = loggerFactory.CreateLogger(nameof(MagosComposition));
            var profiles = provider.GetRequiredService<IProfileService>();
            var repo = provider.GetRequiredService<IModRepository>();
            ModCleanup.PruneUnreferenced(profiles, repo);
            logger.LogInformation("Startup mod prune complete.");
        }
        catch (Exception ex)
        {
            // Swallow: cleanup is best-effort. Log + continue.
            loggerFactory.CreateLogger(nameof(MagosComposition))
                .LogWarning(ex, "Startup mod prune failed (best-effort; will retry next startup).");
        }
    }

    /// <summary>
    /// Runs <see cref="ISteamService.Discover"/> once after composition so the
    /// persisted discovery overrides are validated + healed up front (the
    /// Settings window reads them directly, so this populates the fields rather
    /// than leaving them blank). Best-effort + non-blocking: any failure is
    /// logged + swallowed so a discovery problem never blocks app startup. A
    /// missing-fields result is logged as a warning so the operator knows they
    /// cannot launch yet; the launch-time Discover re-checks and surfaces the
    /// escape-hatch when incomplete.
    /// </summary>
    private static void RunStartupDiscovery(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            var logger = loggerFactory.CreateLogger(nameof(MagosComposition));
            var steam = provider.GetRequiredService<ISteamService>();
            var result = steam.Discover();
            if (result.Status == DiscoveryStatus.Complete)
            {
                logger.LogInformation("Startup discovery complete.");
            }
            else
            {
                // Non-blocking: the user can still use the app; they just cannot
                // launch until the missing fields are resolved (the launch-time
                // Discover re-validates + heals, then surfaces the escape-hatch
                // when still incomplete).
                logger.LogWarning(
                    "Startup discovery is {Status}: missing fields will block launch until resolved " +
                    "(steam={Steam}, darktide={Darktide}, compatdata={Compatdata}, proton={Proton}).",
                    result.Status,
                    result.SteamInstallPath ?? "(missing)",
                    result.DarktideGameBinaryPath ?? "(missing)",
                    result.CompatdataPath ?? "(missing)",
                    result.ProtonBinaryPath ?? "(missing)");
            }
        }
        catch (Exception ex)
        {
            // Swallow: discovery is best-effort at startup. Log + continue; the
            // launch-time Discover re-runs and surfaces real failures.
            loggerFactory.CreateLogger(nameof(MagosComposition))
                .LogWarning(ex, "Startup discovery failed (best-effort; launch will re-try).");
        }
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

    /// <summary>
    /// Binds + starts the nxm IPC server. <see cref="NxmIpcServer.Bind"/> runs
    /// two separate checks: (1) single-instance via process enumeration, which
    /// throws <see cref="NxmSingleInstanceException"/> if another Magos process
    /// is running (this method rethrows it so the caller,
    /// <c>App.OnFrameworkInitializationCompleted</c>, can shut down before the
    /// main window shows); and (2) the IPC pipe bind, which is its own check
    /// that degrades gracefully on <see cref="IOException"/> (a real pipe
    /// problem, not another instance). On a successful bind the accept loop is
    /// kicked off on a background task (fire-and-forget; process exit reclaims
    /// the pipe). On a degraded bind, the loop is skipped and the app continues
    /// without the IPC server (nxm click-to-download won't work this session;
    /// everything else is unaffected).
    /// </summary>
    /// <remarks>
    /// Called from <see cref="Build"/> after the provider is built. Throwing
    /// (rather than returning a flag) on the single-instance violation keeps
    /// <see cref="Build"/>'s signature unchanged and makes the violation an
    /// explicit, unmissable signal at the call site. The composition root never
    /// catches <see cref="NxmSingleInstanceException"/>, so it propagates to the
    /// App. The pipe-bind degradation, by contrast, is non-fatal: the warning is
    /// logged inside <see cref="NxmIpcServer.Bind"/> and this method simply skips
    /// the accept loop when <see cref="NxmIpcServer.IsBound"/> is false.
    /// </remarks>
    private static void StartNxmServer(IServiceProvider provider, ILogger logger)
    {
        var server = provider.GetRequiredService<NxmIpcServer>();

        // Bind runs Check 1 (process enumeration -> NxmSingleInstanceException on
        // collision, fatal) and Check 2 (pipe ctor -> IOException degrades to a
        // not-bound server with a warning logged, non-fatal).
        server.Bind();

        if (!server.IsBound)
        {
            // Degraded: Bind already logged the detailed warning (with the
            // IOException). Skip the accept loop; the app continues without nxm
            // IPC. Everything else (window, profiles, mods, launch) is unaffected.
            logger.LogWarning(
                "nxm IPC server is not running; nxm click-to-download from Nexus is unavailable this session.");
            return;
        }

        // Kick off the accept loop. The cancellation token is captured for a
        // future graceful-shutdown hook; for v1, process exit reclaims the pipe.
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The nxm IPC server accept loop exited unexpectedly.");
            }
        });

        logger.LogInformation("nxm IPC server accept loop started.");
    }
}
