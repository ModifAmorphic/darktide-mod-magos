namespace Magos.Modificus.Profiles;

/// <summary>
/// The identity <see cref="IModOrderResolver"/>: returns the mod names in their
/// current <see cref="ModListEntry.Order"/> (a no-op). Phase-3 stub: the real
/// dependency-driven auto-sort lands in a later phase; this keeps the UI's
/// auto-sort toggle wired + shippable now.
/// </summary>
/// <remarks>
/// Pure + stateless. Stable on equal <see cref="ModListEntry.Order"/> values
/// (<see cref="Enumerable.OrderBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/>
/// is a stable sort, so equal orders keep the input's relative order, which is
/// usually storage order from <see cref="IProfileService.GetModList"/>).</remarks>
public sealed class IdentityModOrderResolver : IModOrderResolver
{
    /// <inheritdoc />
    public IReadOnlyList<string> ResolveOrder(IReadOnlyList<ModListEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        // Identity: current order, unchanged. Select names ordered by current
        // Order; OrderBy is stable on ties.
        return mods
            .OrderBy(m => m.Order)
            .Select(m => m.Name)
            .ToArray();
    }
}
