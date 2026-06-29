using Microsoft.Extensions.DependencyInjection;

namespace Magos.Modificus.Profiles;

/// <summary>
/// Profile + settings management (create / edit / remove / switch, per-profile
/// mod lists, shared-vs-diverged allocation). Stub — implemented in a later
/// phase. See <c>docs/architecture/MAGOS-MODIFICUS.md</c>.
/// </summary>
public interface IProfileService
{
}

internal sealed class ProfileService : IProfileService
{
}

/// <summary>DI registration for the Profiles library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Profiles library services.</summary>
    public static IServiceCollection AddProfiles(this IServiceCollection services)
    {
        services.AddSingleton<IProfileService, ProfileService>();
        return services;
    }
}
