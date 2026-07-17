namespace Modificus.Curator.Profiles;

/// <summary>
/// Resolves the load order for a profile's mods (the auto-sort seam). The result
/// is applied via <see cref="IProfileService.SetModOrder"/>.
/// </summary>
/// <remarks>
/// <para>
/// A DI-swappable seam: the active resolver can be replaced without changing
/// the call site.</para>
/// <para>
/// Pure: no I/O, no logging. Implementations must be deterministic for a given
/// input (the caller applies the result and persists).</para>
/// </remarks>
public interface IModOrderResolver
{
    /// <summary>
    /// Resolves the load order for the given mods.
    /// </summary>
    /// <param name="mods">The profile's current mod list (any order). Non-null.</param>
    /// <returns>The container ids in resolved load order (lower index loads
    /// first). The set of ids must match the input; only the order changes.</returns>
    IReadOnlyList<Guid> ResolveOrder(IReadOnlyList<ModListEntry> mods);
}
