using Magos.Modificus.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Magos.Modificus.SharedMods;

/// <summary>DI registration for the SharedMods library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISharedModStore"/> → <see cref="SharedModStore"/>.
    /// Resolves <c>MagosConfig</c> + <c>ILogger&lt;SharedModStore&gt;</c> from the
    /// container (both provided by <c>AddGeneral()</c> / <c>AddLogging()</c>).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{IService,Service}"/>,
    /// mirroring the <c>SymlinkCreator</c> seam in Profiles: production behavior
    /// is unchanged (TryAdd registers on first call when nothing's pre-registered),
    /// but a caller may pre-register an <see cref="ISharedModStore"/> mock and have
    /// it survive <c>AddProfiles()</c> (which calls <c>AddSharedMods()</c>
    /// unconditionally — a plain AddSingleton would clobber a pre-registered mock,
    /// since MS DI resolves the last descriptor).
    /// </remarks>
    public static IServiceCollection AddSharedMods(this IServiceCollection services)
    {
        services.TryAddSingleton<ISharedModStore, SharedModStore>();
        return services;
    }
}
