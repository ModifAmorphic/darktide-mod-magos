namespace Magos.Modificus.SharedMods;

/// <summary>
/// A single mod in the global shared store — the manifest entry persisted to
/// <c>&lt;SharedModsFolder&gt;/shared-manifest.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Path"/> is <c>&lt;SharedModsFolder&gt;/&lt;Name&gt;</c> — where the
/// shared mod files live (one copy, shared across profiles that resolve to
/// Share). Stored as given by the caller (Phase 4 acquisition), per the spec:
/// <c>ISharedModStore.Add</c> <b>assumes the mod files are already at
/// <see cref="Path"/></b> — Phase 2 manages the manifest, not the downloads.</para>
/// <para>
/// Immutable record: mutations go through <see cref="ISharedModStore.Add"/> /
/// <see cref="ISharedModStore.Remove"/>, which rebuild the manifest on disk.</para>
/// </remarks>
public sealed record SharedModEntry
{
    /// <summary>The mod folder name — the value written to <c>mods.lst</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The shared entry's version policy (drives allocation resolution).</summary>
    public ModVersionPolicy Policy { get; init; } = ModVersionPolicy.Latest;

    /// <summary>
    /// The actual on-disk version of the shared copy (what Phase 4 placed at
    /// <see cref="Path"/>). Used for display + the "both Pinned same version"
    /// share check is by the policies' versions, not this.
    /// </summary>
    public Version ActualVersion { get; init; } = new(0, 0);

    /// <summary>
    /// Where the shared mod files live: <c>&lt;SharedModsFolder&gt;/&lt;Name&gt;</c>.
    /// The symlink target when staging resolves this entry to Share.
    /// </summary>
    public string Path { get; init; } = string.Empty;
}
