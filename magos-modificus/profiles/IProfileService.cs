using Magos.Modificus.Mods;

namespace Magos.Modificus.Profiles;

/// <summary>
/// Profile + per-profile mod-list management. Owns the profile data model,
/// its on-disk persistence, and the projection of the mod list into a staged
/// mod root (symlinks to the repository's resolved version folders) +
/// <c>mods.lst</c> for the Enginseer runtime.
/// </summary>
/// <remarks>
/// <para>
/// A profile references mods by <see cref="ModListEntry.ContainerId"/>; it never
/// stores mod files of its own. Staging resolves each enabled mod's
/// <see cref="ModVersionPolicy"/> against its <see cref="ModContainer"/> (via
/// <see cref="IModRepository"/>) and symlinks <c>staged/&lt;name&gt;</c> to the
/// resolved version folder. <b>Symlinks, never copies.</b></para>
/// <para>
/// No storage details (paths, version-folder ids) leak through the interface.
/// Registered as a singleton: the service holds no per-request state, and
/// <c>MagosConfig</c> (its only config source) is itself a singleton.</para>
/// </remarks>
public interface IProfileService
{
    /// <summary>All known profiles, as lightweight summaries.</summary>
    IReadOnlyList<ProfileSummary> ListProfiles();

    /// <summary>Loads the full profile (metadata + mod list).</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    Profile GetProfile(Guid id);

    /// <summary>
    /// Creates a new profile: generates the id, scaffolds its directory tree
    /// (<c>staged/</c>) + persists an empty <c>profile.json</c>.
    /// </summary>
    /// <returns>The newly-created profile.</returns>
    Profile CreateProfile(string name);

    /// <summary>Renames the profile (display label only; id and dir are unchanged).</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void RenameProfile(Guid id, string newName);

    /// <summary>Removes the profile entry and its entire on-disk directory tree.</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void DeleteProfile(Guid id);

    /// <summary>The profile's mod list (in stored order, not load order).</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    IReadOnlyList<ModListEntry> GetModList(Guid id);

    /// <summary>
    /// Reassigns <see cref="ModListEntry.Order"/> so the profile's mods follow
    /// <paramref name="containerIdsInOrder"/>. Mods not mentioned keep their
    /// relative order, appended after the listed ones; ids in the list that
    /// aren't in the profile are ignored. No mods are added or removed.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void SetModOrder(Guid id, IReadOnlyList<Guid> containerIdsInOrder);

    /// <summary>Toggles <see cref="ModListEntry.Enabled"/> for a single mod.</summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="containerId"/> is not in the profile's list.
    /// </exception>
    void SetModEnabled(Guid id, Guid containerId, bool enabled);

    /// <summary>
    /// Adds a mod entry to the end of the list (<see cref="ModListEntry.Enabled"/>
    /// = true) with the given policy. <b>List entry only: does NOT fetch or
    /// install mod files</b> (the repository holds the files; staging symlinks
    /// to them). Idempotent: re-adding a <paramref name="containerId"/> already
    /// in the list is a no-op (order/enabled/policy untouched).
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void AddMod(Guid id, Guid containerId, ModVersionPolicy policy);

    /// <summary>
    /// Changes a profile mod's <see cref="ModListEntry.Policy"/>. The new policy
    /// takes effect at the next <see cref="PrepareModRoot"/> (the resolved
    /// version folder may change; no on-disk transition is needed because the
    /// profile never stores mod files).
    /// </summary>
    /// <remarks>
    /// A <see cref="PinnedPolicy"/> is validated against the container's current
    /// versions: its <see cref="PinnedPolicy.VersionId"/> must reference a
    /// version that exists on the container (defense-in-depth against a
    /// programmatic call with a stale id; the UI's pin dropdown can only
    /// produce ids the container already holds). <see cref="LatestPolicy"/> needs
    /// no check (it resolves dynamically to the current <see cref="ModVersion.IsLatest"/>).
    /// </remarks>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="containerId"/> is not in the profile's list.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="policy"/> is a
    /// <see cref="PinnedPolicy"/> whose <see cref="PinnedPolicy.VersionId"/> does
    /// not reference a version present in the container (or the container itself
    /// is missing).</exception>
    void SetModPolicy(Guid id, Guid containerId, ModVersionPolicy policy);

    /// <summary>
    /// Removes the mod entry. The repository copy is <b>not</b> touched (other
    /// profiles may still reference it; the startup prune reclaims it when no
    /// profile does).
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="containerId"/> is not in the profile's list.
    /// </exception>
    void RemoveMod(Guid id, Guid containerId);

    /// <summary>
    /// Regenerates the profile's staged mod root (the <c>--mod-path</c>) from the
    /// current per-mod version resolution, and writes <c>mods.lst</c> from the
    /// successfully-staged enabled mods in <see cref="ModListEntry.Order"/>.
    /// Idempotent (each call clears + rebuilds <c>staged/</c>). Returns the
    /// <c>--mod-path</c> to pass to the Enginseer launcher.
    /// </summary>
    /// <remarks>
    /// Symlinks, not copies (the repository holds the files). A symlink-creation
    /// failure (e.g. Windows without symlink permissions / Developer Mode) throws
    /// <see cref="SymlinkStagingException"/>; it never silently copies. A mod
    /// whose container or resolved version is missing is skipped with a warning
    /// (not a crash); it has no entry in <c>staged/</c> or <c>mods.lst</c>.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    /// <exception cref="SymlinkStagingException">A symlink could not be created.</exception>
    string PrepareModRoot(Guid id);
}
