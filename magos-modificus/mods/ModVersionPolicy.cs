using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magos.Modificus.Mods;

/// <summary>
/// A mod's version policy: the type-safe one-of that drives version resolution
/// at stage time. A profile entry carries one of these alongside its
/// <see cref="ModContainer.Id"/>; <see cref="ModContainer.ResolveVersion"/>
/// resolves it to a <see cref="ModVersion"/>.
/// </summary>
/// <remarks>
/// <para>Two cases:</para>
/// <list type="bullet">
/// <item><see cref="PinnedPolicy"/>: frozen at a specific release, referenced
/// by id (<see cref="PinnedPolicy.VersionId"/>).</item>
/// <item><see cref="LatestPolicy"/>: tracks the newest release (auto-update).</item>
/// </list>
/// <para>
/// Persisted polymorphically to each container's <c>container.json</c> and to
/// <c>profile.json</c> via a <c>$kind</c> discriminator (the values are
/// stable identifiers, <c>pinned</c>/<c>latest</c>, independent of assembly
/// names). A <c>null</c> / absent policy defaults to <see cref="Latest"/>
/// (the baseline): handled by the callers' coercion (e.g. ProfileService on
/// read), not here.</para>
/// <para>
/// <b>The version reference is by id, not by tag.</b> A <see cref="PinnedPolicy"/>
/// carries a <see cref="PinnedPolicy.VersionId"/> (a foreign key to a
/// <see cref="ModVersion.Folder"/>), so the repository stays the single source
/// of truth for version details (<see cref="ModVersion.VersionString"/>,
/// <see cref="ModVersion.IsLatest"/>). A pin can never reference a version that
/// does not exist in the container: <c>IProfileService.SetModPolicy</c> rejects
/// an orphan id, and the UI's pin dropdown can only produce ids the container's
/// version list already holds.</para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind", IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(PinnedPolicy), "pinned")]
[JsonDerivedType(typeof(LatestPolicy), "latest")]
public abstract record ModVersionPolicy
{
    /// <summary>
    /// The default policy (<see cref="LatestPolicy"/>). Convenience so callers
    /// write <c>ModVersionPolicy.Latest</c> and so default parameters can name it.
    /// </summary>
    public static ModVersionPolicy Latest { get; } = new LatestPolicy();
}

/// <summary>
/// Pinned to a specific release. Resolves (via
/// <see cref="ModContainer.ResolveVersion"/>) to the version whose
/// <see cref="ModVersion.Folder"/> matches <see cref="VersionId"/> exactly
/// (raw string equality on the opaque version id).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VersionId"/> is a foreign key to a <see cref="ModVersion.Folder"/>:
/// the version's opaque on-disk folder name (a <c>Guid.NewGuid().ToString("N")</c>
/// minted by <see cref="IModRepository.AddVersion"/>). The repository is the
/// sole source of truth for the version's readable tag
/// (<see cref="ModVersion.VersionString"/>) + recency
/// (<see cref="ModVersion.IsLatest"/>); the profile holds only the id. This makes
/// a phantom pin (an id with no matching version in the container) a structural
/// impossibility through the supported paths: <c>SetModPolicy</c> rejects an
/// orphan id, and the UI dropdown only offers ids the container already holds.</para>
/// </remarks>
public sealed record PinnedPolicy : ModVersionPolicy
{
    public PinnedPolicy() { }

    public PinnedPolicy(string versionId)
    {
        ArgumentNullException.ThrowIfNull(versionId);
        VersionId = versionId;
    }

    /// <summary>
    /// The id of the exact release this policy freezes to: a
    /// <see cref="ModVersion.Folder"/> value (a <c>Guid.NewGuid().ToString("N")</c>-
    /// format opaque id). Compared by ordinal string equality against each
    /// version's <see cref="ModVersion.Folder"/> at stage-time resolution.
    /// </summary>
    public string VersionId { get; init; } = string.Empty;
}

/// <summary>
/// Tracks the newest release (auto-update). Resolves (via
/// <see cref="ModContainer.ResolveVersion"/>) to the container's
/// <see cref="ModVersion.IsLatest"/> version.
/// </summary>
public sealed record LatestPolicy : ModVersionPolicy;
