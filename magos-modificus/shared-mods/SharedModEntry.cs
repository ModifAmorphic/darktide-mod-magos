namespace Magos.Modificus.SharedMods;

/// <summary>
/// A single mod in the global shared store: the manifest entry persisted to
/// <c>&lt;SharedModsFolder&gt;/shared-manifest.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Path"/> is <c>&lt;SharedModsFolder&gt;/&lt;Name&gt;</c>: where the
/// shared mod files live (one copy, shared across profiles that resolve to
/// Share). Stored as given by the caller (the import service places files there
/// for local imports; Phase 4 acquisition will place them for remote), per the
/// spec: <c>ISharedModStore.Add</c> <b>assumes the mod files are already at
/// <see cref="Path"/></b>. Phase 2 manages the manifest, not the downloads.</para>
/// <para>
/// <b>Source</b> (<see cref="ModSource"/>) records where this mod came from
/// (Local / Nexus / GitHub) so a pinned version is legible. <b>ActualVersion</b>
/// is the raw release tag of the shared copy (a string, kept verbatim:
/// arbitrary GitHub/Nexus tags are not SemVer). Both default to a legible empty
/// state (<see cref="NoneSource"/> + <see cref="string.Empty"/>).</para>
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
    /// Where this mod came from: Local / Nexus / GitHub. Makes a pinned version
    /// legible. Default <see cref="NoneSource"/> (local / untracked), which is
    /// also the read-back value for a legacy entry lacking the field.
    /// </summary>
    public ModSource Source { get; init; } = new NoneSource();

    /// <summary>
    /// The actual on-disk version of the shared copy (the raw release tag the
    /// importer / Phase 4 acquisition placed at <see cref="Path"/>). A string,
    /// stored verbatim (arbitrary GitHub/Nexus tags, not SemVer). Used for
    /// display; the "both Pinned same version" share check is by the policies'
    /// version strings (compared with string equality), not by this field.
    /// Default <see cref="string.Empty"/>.
    /// </summary>
    public string ActualVersion { get; init; } = string.Empty;

    /// <summary>
    /// Where the shared mod files live: <c>&lt;SharedModsFolder&gt;/&lt;Name&gt;</c>.
    /// The symlink target when staging resolves this entry to Share.
    /// </summary>
    public string Path { get; init; } = string.Empty;
}
