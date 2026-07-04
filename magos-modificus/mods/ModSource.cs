using System.Text.Json.Serialization;

namespace Magos.Modificus.Mods;

/// <summary>
/// A mod's source provenance: the type-safe one-of that records where a mod
/// container came from (Untracked / Nexus / GitHub). Stored on
/// <see cref="ModContainer"/> so the source badge is legible ("WeaponTweaks
/// *(GitHub owner/repo)* pinned to <c>1.2</c>").
/// </summary>
/// <remarks>
/// <para>The UI collects URLs; the model stores canonical identity (Nexus mod id;
/// GitHub owner/repo). URL → source parsing lives in
/// <see cref="ModSourceParser"/> (a pure helper).</para>
/// <para>
/// Persisted polymorphically to each container's <c>container.json</c> via a
/// <c>$kind</c> discriminator (the values are stable identifiers,
/// <c>untracked</c>/<c>nexus</c>/<c>github</c>, independent of assembly names),
/// mirroring the established <see cref="ModVersionPolicy"/> serialization.</para>
/// <para>
/// <b>Untracked identity is the container <see cref="ModContainer.Name"/></b>:
/// the <see cref="UntrackedSource"/> record itself carries no identity payload
/// (unlike Nexus/GitHub, whose identity is fully on the source). Container
/// lookup for an untracked source goes through
/// <see cref="IModRepository.FindUntrackedByName"/>; Nexus/GitHub lookup goes
/// through <see cref="IModRepository.FindBySource"/>. This keeps the source
/// hierarchy faithful to the spec while still letting the import service dedup
/// untracked-by-name on re-import.</para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind", IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(UntrackedSource), "untracked")]
[JsonDerivedType(typeof(NexusSource), "nexus")]
[JsonDerivedType(typeof(GitHubSource), "github")]
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
/// GitHub source. The canonical identity is the owner/repo pair (the trailing
/// two path segments of the repo URL).
/// </summary>
public sealed record GitHubSource : ModSource
{
    /// <summary>The repo owner (the first path segment, e.g. the user/org).</summary>
    public string Owner { get; init; } = string.Empty;

    /// <summary>The repo name (the second path segment).</summary>
    public string Repo { get; init; } = string.Empty;
}
