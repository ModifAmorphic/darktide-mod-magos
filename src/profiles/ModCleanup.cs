using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles;

/// <summary>
/// Startup cleanup for the mod repository. Collects every
/// <c>(containerId, versionFolder)</c> referenced by any profile (resolving each
/// entry's policy against its container), then asks the repository to drop
/// unreferenced versions + empty containers. Keeps the on-disk tree in sync
/// with what the profiles actually use; safe to run on every startup.
/// </summary>
/// <remarks>
/// <para>
/// This is the spec's startup prune (§5): <c>PruneUnreferenced</c> runs once
/// after composition. The resolution here mirrors
/// <c>ProfileService.PrepareModRoot</c> via <see cref="ModContainer.ResolveVersion"/>,
/// so a version folder survives the prune iff at least one profile would stage
/// it. Disabled mods still count as referenced (the profile entry is the
/// reference; enable/disable is a stage-time decision, not a delete signal).</para>
/// <para>
/// I/O failures (a missing directory, an unreadable profile) propagate as
/// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>: the
/// composition root decides whether to swallow them (startup should not abort
/// on a cleanup failure; the repository is still usable, and the next startup
/// retries).</para>
/// </remarks>
public static class ModCleanup
{
    /// <summary>
    /// Runs the startup prune: collects referenced version folders across every
    /// profile, then calls <see cref="IModRepository.PruneUnreferenced"/>. Safe
    /// to call once after composition; idempotent.
    /// </summary>
    /// <exception cref="IOException">On a filesystem failure while reading
    /// profiles.</exception>
    /// <exception cref="UnauthorizedAccessException">On an access-denied failure
    /// while reading profiles.</exception>
    public static void PruneUnreferenced(IProfileService profiles, IModRepository repo)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(repo);

        var referenced = new HashSet<(Guid, string)>();

        foreach (var summary in profiles.ListProfiles())
        {
            IReadOnlyList<ModListEntry> mods;
            try
            {
                mods = profiles.GetModList(summary.Id);
            }
            catch (KeyNotFoundException)
            {
                // The profile disappeared between list + read (unlikely, but
                // possible if a cleanup ran concurrently). Skip it.
                continue;
            }

            foreach (var entry in mods)
            {
                var container = repo.Get(entry.ContainerId);
                if (container is null)
                {
                    continue; // dangling reference; nothing to keep.
                }

                var version = container.ResolveVersion(entry.Policy);
                if (version is null)
                {
                    continue; // policy resolves to nothing; nothing to keep.
                }

                referenced.Add((entry.ContainerId, version.Folder));
            }
        }

        repo.PruneUnreferenced(referenced);
    }
}
