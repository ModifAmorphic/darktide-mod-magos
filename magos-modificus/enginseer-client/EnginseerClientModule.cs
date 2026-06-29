using Microsoft.Extensions.DependencyInjection;

namespace Magos.EnginseerClient;

/// <summary>
/// v1 launch façade over the Enginseer runtime: assemble launcher args, invoke
/// <c>magos_launcher.exe</c> (under Proton on Linux), and track process exit.
/// Stub — implemented in a later phase. See
/// <c>docs/architecture/MAGOS-MODIFICUS.md</c> and the Enginseer contract.
/// </summary>
public interface IEnginseerLaunchService
{
}

internal sealed class EnginseerLaunchService : IEnginseerLaunchService
{
}

/// <summary>DI registration for the Enginseer-client library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Enginseer-client library services.</summary>
    public static IServiceCollection AddEnginseerClient(this IServiceCollection services)
    {
        services.AddSingleton<IEnginseerLaunchService, EnginseerLaunchService>();
        return services;
    }
}
