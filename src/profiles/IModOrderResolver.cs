namespace Modificus.Curator.Profiles;

/// <summary>
/// Resolves the load order for a profile's mods (the auto-sort seam). The
/// mod-list UI's auto-sort toggle calls this; the result is applied via
/// <see cref="IProfileService.SetModOrder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The current implementation is the identity stub (<see cref="IdentityModOrderResolver"/>):
/// it returns container ids in their current <see cref="ModListEntry.Order"/> (a
/// no-op). The real dependency-driven algorithm lands in a later phase; this
/// interface is the DI-swappable seam so the UI wires against the abstraction
/// now and the real resolver drops in later without a UI change.</para>
/// <para>
/// Pure: no I/O, no logging, no DI. Implementations must be deterministic for a
/// given input (the UI applies the result and persists).</para>
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
