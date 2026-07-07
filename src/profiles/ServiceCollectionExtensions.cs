using Modificus.Curator.Mods;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Modificus.Curator.Profiles;

/// <summary>DI registration for the Profiles library.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProfileService"/> → <see cref="ProfileService"/>,
    /// plus the staging dependencies it needs: the <see cref="SymlinkCreator"/>
    /// default (wraps <see cref="System.IO.Directory.CreateSymbolicLink"/>) and,
    /// defensively, <see cref="IModRepository"/> via <c>AddMods()</c>
    /// so a lone <c>AddProfiles()</c> yields a resolvable
    /// <see cref="IProfileService"/> (idempotent; the composition root also
    /// calls <c>AddMods()</c> for discoverability). Also registers
    /// <see cref="IModOrderResolver"/> → <see cref="IdentityModOrderResolver"/>
    /// (the auto-sort seam; identity stub now, real dependency-driven resolver
    /// later, DI-swappable without a UI change).
    /// </summary>
    /// <remarks>
    /// Resolves <c>IConfigLoader</c> (the live config reader) + <c>ILogger&lt;&gt;</c>
    /// from the container (both provided by <c>AddGeneral()</c> / <c>AddLogging()</c>).
    /// </remarks>
    public static IServiceCollection AddProfiles(this IServiceCollection services)
    {
        // Bring in the mod repository the staging seam depends on. Idempotent,
        // safe to call again from the composition root / other libraries.
        services.AddMods();

        // Default symlink impl: the BCL primitive. TryAdd so a caller may
        // pre-register an override (e.g. tests inject a throwing delegate to
        // exercise the SymlinkStagingException path without platform hacks).
        services.TryAddSingleton<SymlinkCreator>(_ => CreateSymbolicLink);

        // Auto-sort seam: identity stub for now (no-op). TryAdd so a caller may
        // pre-register the real dependency-driven resolver when it lands.
        services.TryAddSingleton<IModOrderResolver, IdentityModOrderResolver>();

        services.AddSingleton<IProfileService, ProfileService>();
        return services;
    }

    private static void CreateSymbolicLink(string linkPath, string targetPath) =>
        Directory.CreateSymbolicLink(linkPath, targetPath);
}
