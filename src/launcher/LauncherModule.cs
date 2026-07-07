using Microsoft.Extensions.DependencyInjection;

namespace Modificus.Curator.Launcher;

/// <summary>
/// The slim profile launcher -- a thin native binary that accepts a profile
/// argument and launches it (the target for Steam non-steam shortcuts). It
/// reuses the Steam and Enginseer-client libraries. Stub -- implemented in a
/// later phase. See <c>docs/architecture/MODIFICUS-CURATOR.md</c>.
/// </summary>
public interface IProfileLauncher
{
}

internal sealed class ProfileLauncher : IProfileLauncher
{
}

/// <summary>DI registration for the Launcher library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Launcher library services.</summary>
    public static IServiceCollection AddLauncher(this IServiceCollection services)
    {
        services.AddSingleton<IProfileLauncher, ProfileLauncher>();
        return services;
    }
}
