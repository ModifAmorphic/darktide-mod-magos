using System.Text.Json.Serialization;

namespace Magos.Modificus.SharedMods;

/// <summary>
/// A mod's source provenance: the type-safe one-of that records where a shared
/// copy came from (Local / Nexus / GitHub). Stored on <see cref="SharedModEntry"/>
/// so a pinned version is legible ("WeaponTweaks *(GitHub owner/repo)* pinned to
/// <c>1.2</c>").
/// </summary>
/// <remarks>
/// <para>The UI collects URLs; the model stores canonical identity (Nexus mod id;
/// GitHub owner/repo). URL → source parsing lives in
/// <see cref="ModSourceParser"/> (a pure helper).</para>
/// <para>
/// Persisted polymorphically to <c>shared-manifest.json</c> via a <c>$kind</c>
/// discriminator (the values are stable identifiers, <c>none</c>/
/// <c>nexus</c>/<c>github</c>, independent of assembly names), mirroring the
/// established <see cref="ModVersionPolicy"/> serialization. A <c>null</c> /
/// absent source defaults to <see cref="NoneSource"/> (local / untracked):
/// handled by caller coercion (e.g. the import service always supplies one),
/// and an existing/legacy shared entry reads back as <see cref="NoneSource"/>
/// when the field is missing.</para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind", IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(NoneSource), "none")]
[JsonDerivedType(typeof(NexusSource), "nexus")]
[JsonDerivedType(typeof(GitHubSource), "github")]
public abstract record ModSource;

/// <summary>
/// Local / untracked source: the default for an imported folder or a legacy
/// shared entry. No remote identity.
/// </summary>
public sealed record NoneSource : ModSource;

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
