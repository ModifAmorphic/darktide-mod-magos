namespace Magos.Modificus.Profiles;

/// <summary>
/// A single mod entry within a profile's mod list — the source of truth that
/// <see cref="IProfileService.PrepareModRoot"/> projects into <c>mods.lst</c>.
/// </summary>
/// <remarks>
/// <para>Immutable: all properties are init-only. Mutations go through the
/// <see cref="IProfileService"/> methods, which rebuild the changed entry
/// (via <c>with</c> expressions) and persist — a consumer can't silently edit
/// an entry returned from <see cref="IProfileService.GetModList"/> and have it
/// look persisted when it isn't.</para>
/// <para>Phase 1 shape only. Version / source / policy fields land with
/// Integrations and Phase 2 (shared-first storage); the Phase 1 fields stay
/// stable when they arrive, so this type will <em>grow</em> but not break.</para>
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
}
