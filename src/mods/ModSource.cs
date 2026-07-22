using System.Text.Json.Serialization;

namespace Modificus.Curator.Mods;

/// <summary>
/// A mod's source provenance: the type-safe one-of that records where a mod
/// container came from (Untracked / Nexus / Linked). Stored on
/// <see cref="ModContainer"/> so the source badge is legible ("WeaponTweaks
/// *(Nexus #12345)* pinned to <c>1.2</c>").
/// </summary>
/// <remarks>
/// <para>The UI collects URLs; the model stores canonical identity (Nexus mod
/// id). URL → source parsing lives in <see cref="ModSourceParser"/> (a pure
/// helper).</para>
/// <para>
/// Persisted polymorphically to each container's <c>container.json</c> via a
/// <c>$kind</c> discriminator (the values are stable identifiers,
/// <c>untracked</c>/<c>nexus</c>/<c>linked</c>, independent of assembly names),
/// mirroring the established <see cref="ModVersionPolicy"/> serialization.</para>
/// <para>
/// <b>Untracked identity is the container <see cref="ModContainer.Name"/></b>:
/// the <see cref="UntrackedSource"/> record itself carries no identity payload
/// (unlike Nexus + Linked, whose identity is fully on the source). Container
/// lookup for an untracked source goes through
/// <see cref="IModRepository.FindUntrackedByName"/>; Nexus + Linked lookup go
/// through <see cref="IModRepository.FindBySource"/>. This keeps the source
/// hierarchy faithful to the spec while still letting the import service dedup
/// untracked-by-name on re-import.</para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind", IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(UntrackedSource), "untracked")]
[JsonDerivedType(typeof(NexusSource), "nexus")]
[JsonDerivedType(typeof(LinkedSource), "linked")]
public abstract record ModSource;

/// <summary>
/// Untracked source: the default for a locally imported folder or archive. No
/// remote identity; the dedup key is the container's <see cref="ModContainer.Name"/>
/// (the user-typed name, stable across re-imports).
/// </summary>
public sealed record UntrackedSource : ModSource;

/// <summary>
/// Nexus Mods source. The game is fixed (Darktide), so the canonical identity
/// is just the numeric mod id.
/// </summary>
public sealed record NexusSource : ModSource
{
    /// <summary>The Nexus mod id (the trailing integer of the mod's URL).</summary>
    public int ModId { get; init; }
}

/// <summary>
/// Linked source: an external mod folder added to the repository without
/// copying. The canonical identity is the normalized
/// <see cref="ExternalPath"/>. The folder is externally owned: Curator never
/// copies, writes, versions, or deletes anything inside it. A linked container
/// has no versions; staging links <c>&lt;profile&gt;/staged/mods/&lt;baseName&gt;</c>
/// directly to <see cref="ExternalPath"/> at launch.
/// </summary>
public sealed record LinkedSource : ModSource
{
    /// <summary>
    /// The normalized absolute path to the external mod folder. Identity for a
    /// linked container (two linked containers with the same
    /// <see cref="ExternalPath"/> are the same container). Stored verbatim on
    /// <c>container.json</c>; never rewritten by Curator.
    /// </summary>
    public string ExternalPath { get; init; } = string.Empty;
}
