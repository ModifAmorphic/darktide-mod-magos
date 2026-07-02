namespace Magos.Modificus.SharedMods;

/// <summary>
/// Pure allocation logic — resolves a profile mod's version policy against the
/// shared store's entry to decide <see cref="AllocationResolution"/>.
/// </summary>
/// <remarks>
/// <para>
/// The four documented cases:</para>
/// <code>
/// shared Pinned(v1.0.1) + profile Pinned(v1.0.1) -&gt; Share  (same pin)
/// shared Pinned(v1.0.1) + profile Pinned(v2.0.1) -&gt; Diverge (different pins)
/// shared Latest         + profile Latest         -&gt; Share  (both track latest)
/// shared Latest         + profile Pinned(v2.0.1) -&gt; Diverge (shared will move)
/// </code>
/// <para>
/// The resolution is by policy <b>intent</b>, not current version: a shared
/// <c>Latest</c> and a profile <c>Pinned</c> to today's same version still
/// <see cref="AllocationResolution.Diverge"/>, because the shared one will move
/// on the next release while the profile won't. This is why a matching version
/// alone is not enough — both sides must agree on intent.</para>
/// <para>
/// Pure: no I/O, no logging, no DI. Inputs are non-null; the caller decides how
/// to handle a mod with no shared-store entry (treated as needing a diverged/
/// local copy by the staging layer).</para>
/// </remarks>
public static class AllocationResolver
{
    /// <summary>
    /// Resolves the allocation for a profile mod against its shared-store entry.
    /// </summary>
    /// <param name="sharedPolicy">The shared entry's policy (non-null).</param>
    /// <param name="sharedActualVersion">The shared entry's actual on-disk version
    /// (unused for the decision — kept in the signature for clarity + future use;
    /// the resolution is by intent, not version).</param>
    /// <param name="profilePolicy">The profile mod's policy (non-null).</param>
    /// <returns><see cref="AllocationResolution.Share"/> iff both sides carry the
    /// same intent — both <c>Pinned</c> to the same version, or both
    /// <c>Latest</c>; otherwise <see cref="AllocationResolution.Diverge"/>.</returns>
    public static AllocationResolution Resolve(
        ModVersionPolicy sharedPolicy,
        Version sharedActualVersion,
        ModVersionPolicy profilePolicy)
    {
        ArgumentNullException.ThrowIfNull(sharedPolicy);
        ArgumentNullException.ThrowIfNull(sharedActualVersion);
        ArgumentNullException.ThrowIfNull(profilePolicy);

        // Both Latest -> share (both track latest; shared is updated to latest).
        if (sharedPolicy is LatestPolicy && profilePolicy is LatestPolicy)
        {
            return AllocationResolution.Share;
        }

        // Both Pinned to the same version -> share (frozen to the same release).
        // TODO(phase4): System.Version equality is COMPONENT-COUNT-SENSITIVE —
        // new Version(1,0) != new Version(1,0,0) and Version.Parse("1.0") !=
        // Version.Parse("1.0.0") (Build/Revision default to -1, not 0). Phase 2
        // is internally consistent (its tests use matching component counts), so
        // this is dormant. But Phase 4 (acquisition) will parse user-typed pins
        // + GitHub-release-tag versions against shared-store entries — cross-
        // component-count comparisons would silently Diverge here. Phase 4 must
        // normalize version representation at the acquisition boundary (canonical
        // Major.Minor.Build, or a 2/3/4-component-normalized compare) so "1.0"
        // and "1.0.0" compare equal. No Phase 2 logic change.
        if (sharedPolicy is PinnedPolicy sp && profilePolicy is PinnedPolicy pp
            && sp.Version == pp.Version)
        {
            return AllocationResolution.Share;
        }

        // Any other pairing diverges (different pins, or mixed Latest/Pinned —
        // the shared intent and the profile intent disagree, even if today's
        // versions happen to match).
        return AllocationResolution.Diverge;
    }
}
