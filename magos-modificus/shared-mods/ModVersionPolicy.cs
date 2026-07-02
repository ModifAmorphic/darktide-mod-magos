using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magos.Modificus.SharedMods;

/// <summary>
/// A mod's version policy — the type-safe one-of that drives shared-vs-diverged
/// allocation (see <see cref="AllocationResolver"/>).
/// </summary>
/// <remarks>
/// <para>Two cases:</para>
/// <list type="bullet">
/// <item><see cref="PinnedPolicy"/> — frozen at a specific release
/// (<see cref="PinnedPolicy.Version"/>).</item>
/// <item><see cref="LatestPolicy"/> — tracks the newest release (auto-update).</item>
/// </list>
/// <para>
/// Persisted polymorphically to <c>shared-manifest.json</c> and
/// <c>profile.json</c> via a <c>$kind</c> discriminator (the values are
/// stable identifiers, <c>pinned</c>/<c>latest</c>, independent of assembly
/// names). A <c>null</c> / absent policy defaults to <see cref="Latest"/>
/// (the Phase 1 baseline) — handled by the callers' coercion, not here.</para>
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
/// Pinned to a specific release. Two pins share only when their versions match.
/// </summary>
public sealed record PinnedPolicy : ModVersionPolicy
{
    public PinnedPolicy() { }

    public PinnedPolicy(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        Version = version;
    }

    /// <summary>The exact release this policy freezes to.</summary>
    public Version Version { get; init; } = new(0, 0);
}

/// <summary>
/// Tracks the newest release (auto-update). Two Latests share (both move together).
/// </summary>
public sealed record LatestPolicy : ModVersionPolicy;
