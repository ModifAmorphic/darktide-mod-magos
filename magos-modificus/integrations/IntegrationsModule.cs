using Microsoft.Extensions.DependencyInjection;

namespace Magos.Modificus.Integrations;

/// <summary>
/// External mod-source clients: Nexus Mods (primary), GitHub Releases, and
/// local install. Version checks, downloads / updates. Stub — implemented in a
/// later phase. See <c>docs/architecture/MAGOS-MODIFICUS.md</c>.
/// </summary>
public interface IModSourceService
{
}

internal sealed class ModSourceService : IModSourceService
{
}

/// <summary>DI registration for the Integrations library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Integrations library services.</summary>
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
    {
        services.AddSingleton<IModSourceService, ModSourceService>();
        return services;
    }
}
