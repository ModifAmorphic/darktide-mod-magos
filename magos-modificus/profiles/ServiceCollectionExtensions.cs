using Microsoft.Extensions.DependencyInjection;

namespace Magos.Modificus.Profiles;

/// <summary>DI registration for the Profiles library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProfileService"/> → <see cref="ProfileService"/>.
    /// Resolves <c>MagosConfig</c> + <c>ILogger&lt;ProfileService&gt;</c> from the
    /// container (both provided by <c>AddGeneral()</c> / <c>AddLogging()</c>).
    /// </summary>
    public static IServiceCollection AddProfiles(this IServiceCollection services)
    {
        services.AddSingleton<IProfileService, ProfileService>();
        return services;
    }
}
