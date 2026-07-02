namespace Magos.Modificus.SharedMods;

/// <summary>
/// The shared-vs-diverged allocation outcome for a single profile mod resolved
/// against its shared-store entry. See <see cref="AllocationResolver"/>.
/// </summary>
public enum AllocationResolution
{
    /// <summary>
    /// The profile uses the global shared copy (its policy is compatible with
    /// the shared entry's policy). Staged as a symlink into the shared store.
    /// </summary>
    Share,

    /// <summary>
    /// The profile needs a profile-local (diverged) copy (its policy differs in
    /// intent from the shared entry's). Staged as a symlink into the profile's
    /// <c>mods/</c> dir.
    /// </summary>
    Diverge,
}
