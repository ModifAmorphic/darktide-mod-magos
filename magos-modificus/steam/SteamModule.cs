using Microsoft.Extensions.DependencyInjection;

namespace Magos.Modificus.Steam;

/// <summary>
/// Steam operations outside Enginseer: locate Steam, find the Darktide install
/// + compatdata + Proton version, add/remove non-steam shortcuts, detect
/// whether the game is running. Stub — implemented in a later phase. See
/// <c>docs/architecture/MAGOS-MODIFICUS.md</c>.
/// </summary>
public interface ISteamService
{
}

internal sealed class SteamService : ISteamService
{
}

/// <summary>DI registration for the Steam library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Steam library services.</summary>
    public static IServiceCollection AddSteam(this IServiceCollection services)
    {
        services.AddSingleton<ISteamService, SteamService>();
        return services;
    }
}
