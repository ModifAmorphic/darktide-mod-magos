using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Magos.Modificus.Config;

namespace Magos.Modificus.General;

/// <summary>
/// DI registration for the General library (cross-cutting infra). Registers
/// the loaded config, the logging pipeline, and the config loader. The
/// composition root calls this after loading config and building the logger.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers General services: the <paramref name="config"/> singleton,
    /// the <paramref name="loggerFactory"/>, <c>AddLogging()</c>,
    /// <see cref="IConfigLoader"/>, and <see cref="IAppStateStore"/> (runtime
    /// app-state: the active-profile id, persisted separately from
    /// <see cref="MagosConfig"/>).
    /// </summary>
    public static IServiceCollection AddGeneral(
        this IServiceCollection services,
        MagosConfig config,
        ILoggerFactory loggerFactory)
    {
        services.AddSingleton(config);
        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddSingleton<IConfigLoader, ConfigLoader>();
        // TryAdd so a test/host may pre-register an override (e.g. an in-memory
        // or temp-path state store) before AddGeneral runs.
        services.TryAddSingleton<IAppStateStore, AppStateStore>();
        return services;
    }
}
