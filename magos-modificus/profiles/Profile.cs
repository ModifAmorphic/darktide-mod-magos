namespace Magos.Modificus.Profiles;

/// <summary>
/// A Magos Modificus profile — a named, owned set of mods + load order. The
/// aggregate root persisted to <c>&lt;ProfilesBaseFolder&gt;/&lt;Id&gt;/profile.json</c>.
/// </summary>
/// <remarks>
/// Identity is <see cref="Id"/> (a <see cref="Guid"/>, stable across renames);
/// the on-disk directory is keyed by it. <see cref="Name"/> is a display label,
/// not unique and not used as a path.
/// </remarks>
public sealed class Profile
{
    /// <summary>Stable identity; also the on-disk directory name.</summary>
    public Guid Id { get; init; }

    /// <summary>Display name. Renamable via <see cref="IProfileService.RenameProfile"/>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the profile was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The profile's mods, in no particular storage order — load order comes
    /// from each entry's <see cref="ModListEntry.Order"/>. Exposed as a
    /// <see cref="IReadOnlyList{T}"/> of immutable entries: neither the list
    /// nor its entries can be edited in place — changes go through the
    /// <see cref="IProfileService"/> methods, which rebuild + persist.
    /// </summary>
    public IReadOnlyList<ModListEntry> Mods { get; set; } = Array.Empty<ModListEntry>();
}
