using Magos.Modificus.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Magos.Modificus.SharedMods;

/// <summary>DI registration for the SharedMods library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISharedModStore"/> → <see cref="SharedModStore"/>.
    /// Resolves <c>MagosConfig</c> + <c>ILogger&lt;SharedModStore&gt;</c> from the
    /// container (both provided by <c>AddGeneral()</c> / <c>AddLogging()</c>).
    /// </summary>
    public static IServiceCollection AddSharedMods(this IServiceCollection services)
    {
        services.AddSingleton<ISharedModStore, SharedModStore>();
        return services;
    }
}
