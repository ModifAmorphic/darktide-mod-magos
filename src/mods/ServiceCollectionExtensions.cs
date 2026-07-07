using Modificus.Curator.General;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Modificus.Curator.Mods;

/// <summary>DI registration for the Mods library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IModRepository"/> → <see cref="ModRepository"/> and
    /// <see cref="IModImportService"/> → <see cref="ModImportService"/>. Resolves
    /// <see cref="IConfigLoader"/> (the live config reader) +
    /// <c>ILogger&lt;&gt;</c> from the container (both provided by
    /// <c>AddGeneral()</c> / <c>AddLogging()</c>).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{IService,Service}"/>,
    /// mirroring the <c>SymlinkCreator</c> seam in Profiles: production behavior
    /// is unchanged (TryAdd registers on first call when nothing's pre-registered),
    /// but a caller may pre-register an <see cref="IModRepository"/> fake and have
    /// it survive <c>AddProfiles()</c> (which calls <c>AddMods()</c>
    /// unconditionally; a plain AddSingleton would clobber a pre-registered fake,
    /// since MS DI resolves the last descriptor). The same posture applies to
    /// <see cref="IModImportService"/>: tests may pre-register a fake import
    /// service.
    /// </remarks>
    public static IServiceCollection AddMods(this IServiceCollection services)
    {
        services.TryAddSingleton<IModRepository, ModRepository>();
        services.TryAddSingleton<IModImportService, ModImportService>();
        return services;
    }
}
