namespace Magos.Modificus.SharedMods.Tests;

/// <summary>
/// <see cref="AllocationResolver.Resolve"/> — the pure shared-vs-diverged logic.
/// Covers the four documented cases plus the policy-intent-not-version case.
/// </summary>
public sealed class AllocationResolverTests
{
    private static readonly Version V101 = new(1, 0, 1);
    private static readonly Version V201 = new(2, 0, 1);

    [Fact]
    public void Both_pinned_same_version_shares()
    {
        Assert.Equal(AllocationResolution.Share,
            AllocationResolver.Resolve(new PinnedPolicy(V101), V101, new PinnedPolicy(V101)));
    }

    [Fact]
    public void Both_pinned_different_version_diverges()
    {
        Assert.Equal(AllocationResolution.Diverge,
            AllocationResolver.Resolve(new PinnedPolicy(V101), V101, new PinnedPolicy(V201)));
    }

    [Fact]
    public void Both_latest_shares()
    {
        Assert.Equal(AllocationResolution.Share,
            AllocationResolver.Resolve(new LatestPolicy(), V201, new LatestPolicy()));
    }

    [Fact]
    public void Shared_latest_and_profile_pinned_diverges()
    {
        // The 4th documented case: shared will move, profile won't -> diverge.
        Assert.Equal(AllocationResolution.Diverge,
            AllocationResolver.Resolve(new LatestPolicy(), V201, new PinnedPolicy(V201)));
    }

    [Fact]
    public void Resolution_is_by_policy_intent_not_current_version()
    {
        // Shared Latest + profile Pinned to today's SAME version still diverge —
        // the shared one will move on the next release while the profile won't.
        // This is the key invariant the spec calls out.
        Assert.Equal(AllocationResolution.Diverge,
            AllocationResolver.Resolve(new LatestPolicy(), V101, new PinnedPolicy(V101)));
    }

    [Fact]
    public void Shared_pinned_and_profile_latest_diverges()
    {
        // Mirror of the 4th case (direction reversed): shared is frozen, profile
        // tracks latest -> they diverge.
        Assert.Equal(AllocationResolution.Diverge,
            AllocationResolver.Resolve(new PinnedPolicy(V101), V101, new LatestPolicy()));
    }

    [Fact]
    public void Resolve_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AllocationResolver.Resolve(null!, V101, new LatestPolicy()));
        Assert.Throws<ArgumentNullException>(() =>
            AllocationResolver.Resolve(new LatestPolicy(), null!, new LatestPolicy()));
        Assert.Throws<ArgumentNullException>(() =>
            AllocationResolver.Resolve(new LatestPolicy(), V101, null!));
    }

    [Fact]
    public void Shared_actual_version_does_not_affect_the_resolution()
    {
        // The signature carries sharedActualVersion for clarity/future-use, but
        // the decision is by intent — a shared Pinned(V101) with a *stale* actual
        // version field still shares with a profile Pinned(V101).
        Assert.Equal(AllocationResolution.Share,
            AllocationResolver.Resolve(new PinnedPolicy(V101), new Version(9, 9, 9), new PinnedPolicy(V101)));
    }
}
