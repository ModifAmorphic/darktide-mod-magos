using Magos.Modificus.SharedMods;

namespace Magos.Modificus.Profiles;

/// <summary>
/// A single mod entry within a profile's mod list — the source of truth that
/// <see cref="IProfileService.PrepareModRoot"/> projects into the staged mod
/// root (symlinks) + <c>mods.lst</c>.
/// </summary>
/// <remarks>
/// <para>Immutable: all properties are init-only. Mutations go through the
/// <see cref="IProfileService"/> methods, which rebuild the changed entry
/// (via <c>with</c> expressions) and persist — a consumer can't silently edit
/// an entry returned from <see cref="IProfileService.GetModList"/> and have it
/// look persisted when it isn't.</para>
/// <para>
/// <b>Phase 2:</b> the <see cref="Policy"/> field (default <see cref="ModVersionPolicy.Latest"/>)
/// drives shared-vs-diverged allocation. It is additive — Phase 1 entries in a
/// persisted <c>profile.json</c> that lack it deserialize to <c>Latest</c>, so
/// existing profiles upgrade transparently.</para>
/// </remarks>
public sealed record ModListEntry
{
    /// <summary>The mod folder name — the value written to <c>mods.lst</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Whether the mod is active. Disabled mods are omitted from
    /// <c>mods.lst</c> (enable-by-omission, per the loader contract).
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Position within the load order; lower loads first. <see cref="int"/>
    /// rather than the list index so partial reordering is stable.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// This profile mod's version policy — drives shared-vs-diverged allocation
    /// against the shared-store entry (see <c>AllocationResolver</c>). Defaults
    /// to <see cref="ModVersionPolicy.Latest"/> (the Phase 1 baseline behavior).
    /// </summary>
    public ModVersionPolicy Policy { get; init; } = ModVersionPolicy.Latest;
}
