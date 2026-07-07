using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Nxm;
using Modificus.Curator.Profiles;
using Modificus.Curator.Mods;
using Modificus.Curator.Steam;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Preferences;
using Modificus.Curator.UI.Session;
using Modificus.Curator.UI.ViewModels;
using Modificus.Curator.UI.Views;
using Modificus.Curator.RelayClient;
using Modificus.Curator.Launcher;
using Modificus.Curator.UI.Nxm;

namespace Modificus.Curator.UI;

/// <summary>
/// The DI composition root. Loads config, builds the structured logger, wires
/// every library's <c>Add&lt;Library&gt;()</c> extension, and registers the UI
/// surface (main window + view model + dialog service). All data operations flow
/// through the registered library services; the UI never touches files or APIs
/// directly.
/// </summary>
public static class CuratorComposition
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
        services.AddRelayClient();
        services.AddLauncher();
        // The nxm scheme-handler plumbing: IPC server (single-instance via
        // process enumeration, pipe bind degrades gracefully on failure), router
        // + no-op handler defaults, and the platform OS registrar. The IPC
        // server is bound + started after the provider is built (see
        // StartNxmServer).
        services.AddNxm();

        // Replace the no-op INxmModDownloadHandler (registered inside AddNxm)
        // with the real acquisition handler. MS DI resolves the LAST registration
        // for an interface, so this AddSingleton supersedes the no-op. Registered
        // with a factory that resolves its dependencies lazily at first use (the
        // factory delegate is deferred until the handler is first resolved by the
        // IPC router, by which point all dependencies including IProfileSession,
        // IDialogService, and MainWindow are registered). It coordinates the
        // acquisition service (Integrations) with the active-profile session,
        // profile service, and the UI-thread alert dialog. Registered with a
        // factory so the UI-thread marshaling seam
        // (Dispatcher.UIThread.InvokeAsync) is wired explicitly.
        services.AddSingleton<INxmModDownloadHandler>(sp => new NxmModDownloadHandler(
            invokeOnUi: action => Dispatcher.UIThread.InvokeAsync(action),
            sp.GetRequiredService<IModAcquisitionService>(),
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IProfileService>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ILogger<NxmModDownloadHandler>>(),
            refreshModList: () => sp.GetRequiredService<ModListViewModel>().Reload()));

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
        // The UI-thread marshal seam for ModListViewModel's CheckCompleted handler
        // (the event fires on a threadpool thread; the handler iterates the
        // UI-bound Mods collection). Production wires Dispatcher.UIThread.Post.
        services.AddSingleton<Action<Action>>(_ => action => Dispatcher.UIThread.Post(action));
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
                sp.GetRequiredService<INexusAuthService>(),
                sp.GetRequiredService<ILoggerFactory>()));

        // The UI-layer glue that fires an update check
        // (IUpdateCheckService, registered above via AddIntegrations) whenever a
        // profile becomes active (startup + active-profile switch), and a
        // periodic check while a profile stays active (the toggle + interval
        // are read live from config). Subscribes to IProfileSession.PropertyChanged
        // for switches + fires the opening check for the restored active id.
        // Started after the provider is built (see StartUpdateCheck); best-effort,
        // never blocks startup. Singleton: owns the session subscription for the
        // app lifetime. The periodic timer is wired to a DispatcherTimer (the
        // established ProfileSession pattern); the runner takes the timer-start
        // delegate as a seam so it stays unit-testable.
        services.AddSingleton(sp => new UpdateCheckRunner(
            sp.GetRequiredService<IProfileSession>(),
            sp.GetRequiredService<IUpdateCheckService>(),
            sp.GetRequiredService<IConfigLoader>(),
            sp.GetRequiredService<ILogger<UpdateCheckRunner>>(),
            StartUpdateCheckPolling));

        // The DMF (Darktide Mod Framework) install-prompt coordinator.
        // Subscribes to IProfileService.ProfileCreated +
        // INexusAuthService.AuthStateChanged (both fire from inside the
        // ManageProfiles / Integrations dialogs), records them as pending, and
        // the shell calls ProcessPendingAsync after those dialogs close so the
        // DMF prompt is the topmost modal at that point (no dialog-on-dialog).
        // Singleton: owns the event subscriptions for the app lifetime.
        services.AddSingleton<DmfPromptService>();

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
        // Curator is running (propagates out of Build() so the caller, App, shuts
        // down before the window shows); and (2) the pipe bind, which degrades
        // gracefully on IOException (the app continues without the IPC server).
        // Intentionally NOT wrapped in a try/catch (unlike the best-effort prune
        // + discovery above): a single-instance violation is fatal-by-design for
        // this process. The pipe-bind degradation is handled inside Bind itself.
        StartNxmServer(provider, loggerFactory.CreateLogger(nameof(CuratorComposition)));

        // Register Curator as the OS nxm:// scheme handler if not already
        // registered. Best-effort: a failure is logged + swallowed so a
        // registration problem never blocks startup (the user can still use
        // the app; they just can't click "Mod manager download" on Nexus until
        // it's resolved). This is what MO2, NMA, and Vortex all do on startup.
        RegisterNxmHandler(provider, loggerFactory);

        // Start the update-check runner so a check fires on profile load
        // (startup with the restored id + active-profile switches).
        // Best-effort: a failure is logged + swallowed so a wiring problem never
        // blocks startup (the mod-list update badges just stay blank until restart).
        StartUpdateCheck(provider, loggerFactory);

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
            var logger = loggerFactory.CreateLogger(nameof(CuratorComposition));
            var profiles = provider.GetRequiredService<IProfileService>();
            var repo = provider.GetRequiredService<IModRepository>();
            ModCleanup.PruneUnreferenced(profiles, repo);
            logger.LogInformation("Startup mod prune complete.");
        }
        catch (Exception ex)
        {
            // Swallow: cleanup is best-effort. Log + continue.
            loggerFactory.CreateLogger(nameof(CuratorComposition))
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
            var logger = loggerFactory.CreateLogger(nameof(CuratorComposition));
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
            loggerFactory.CreateLogger(nameof(CuratorComposition))
                .LogWarning(ex, "Startup discovery failed (best-effort; launch will re-try).");
        }
    }

    /// <summary>
    /// Registers Curator as the OS nxm:// scheme handler if not already
    /// registered, so clicking "Mod manager download" on Nexus invokes the
    /// handler exe. Best-effort: a failure is logged + swallowed so a
    /// registration problem never blocks startup.
    /// </summary>
    private static void RegisterNxmHandler(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            var logger = loggerFactory.CreateLogger(nameof(CuratorComposition));
            var registrar = provider.GetService<INxmHandlerRegistrar>();
            if (registrar is null)
            {
                // No registrar for this platform (not Windows or Linux).
                logger.LogWarning("No nxm handler registrar available for this platform; skipping registration.");
                return;
            }
            if (!registrar.IsRegistered())
            {
                registrar.Register();
                logger.LogInformation("Registered Curator as the nxm:// scheme handler.");
            }
        }
        catch (Exception ex)
        {
            // Swallow: registration is best-effort. Log + continue; the user
            // can still use the app (they just can't click "Mod manager
            // download" on Nexus until it's resolved).
            loggerFactory.CreateLogger(nameof(CuratorComposition))
                .LogWarning(ex, "Failed to register the nxm:// scheme handler (best-effort).");
        }
    }

    /// <summary>
    /// Resolves the <see cref="UpdateCheckRunner"/> + calls
    /// <see cref="UpdateCheckRunner.Start"/> so an update check fires on profile
    /// load (startup with the restored active id, then every active-profile
    /// switch). Best-effort: any failure is logged + swallowed so a wiring
    /// problem never blocks app startup (the user can still use the app; the
    /// mod-list update badges just stay blank until restart).
    /// </summary>
    private static void StartUpdateCheck(IServiceProvider provider, ILoggerFactory loggerFactory)
    {
        try
        {
            provider.GetRequiredService<UpdateCheckRunner>().Start();
        }
        catch (Exception ex)
        {
            // Swallow: update-check wiring is best-effort. Log + continue; the
            // app works without it (the mod-list update badges just stay blank).
            loggerFactory.CreateLogger(nameof(CuratorComposition))
                .LogWarning(ex, "Failed to start the update-check runner (best-effort).");
        }
    }

    /// <summary>
    /// The live running-state poll: a <see cref="DispatcherTimer"/> that pings
    /// <see cref="ISteamService.IsGameRunning"/> every few seconds so the status
    /// strip + launch-availability + dropdown-enable react to the game starting or
    /// stopping while Curator is open. Runs on the UI thread (composition happens
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
    /// The periodic update-check poll: a <see cref="DispatcherTimer"/> that ticks
    /// at <see cref="UpdateCheckRunner.TickInterval"/> (1 minute) so the runner
    /// can fire a check when the user-configured interval (read live from config)
    /// has elapsed. The runner owns the interval math + the toggle gate; this
    /// just drives the tick on the UI thread (mirrors
    /// <see cref="StartRunningStatePolling"/>). Composition happens on the UI
    /// thread during app startup.
    /// </summary>
    private static void StartUpdateCheckPolling(Action onTick)
    {
        var timer = new DispatcherTimer
        {
            Interval = UpdateCheckRunner.TickInterval,
        };
        timer.Tick += (_, _) => onTick();
        timer.Start();
    }

    /// <summary>
    /// Binds + starts the nxm IPC server. <see cref="NxmIpcServer.Bind"/> runs
    /// two separate checks: (1) single-instance via process enumeration, which
    /// throws <see cref="NxmSingleInstanceException"/> if another Curator process
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
