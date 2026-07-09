using Modificus.Curator.Mods;

namespace Modificus.Curator.Profiles;

/// <summary>
/// Profile + per-profile mod-list management. Owns the profile data model,
/// its on-disk persistence, and the projection of the mod list into a staged
/// mod root (staging links to the repository's resolved version folders) +
/// <c>mods.lst</c> for Modificus Relay.
/// </summary>
/// <remarks>
/// <para>
/// A profile references mods by <see cref="ModListEntry.ContainerId"/>; it never
/// stores mod files of its own. Staging resolves each enabled mod's
/// <see cref="ModVersionPolicy"/> against its <see cref="ModContainer"/> (via
/// <see cref="IModRepository"/>) and links <c>staged/&lt;name&gt;</c> to the
/// resolved version folder (an NTFS junction on Windows, a symlink on Linux).
/// <b>Staging links, never copies.</b></para>
/// <para>
/// No storage details (paths, version-folder ids) leak through the interface.
/// Registered as a singleton: the service holds no per-request state, and
/// <c>CuratorConfig</c> (its only config source) is itself a singleton.</para>
/// </remarks>
public interface IProfileService
{
    /// <summary>
    /// Raised whenever <see cref="CreateProfile"/> successfully persists a new
    /// profile. Carries the new profile's summary (id + name). Subscribers use
    /// this to react to "a profile was just created" (the DMF new-profile
    /// prompt coordinator is the consumer).
    /// </summary>
    /// <remarks>
    /// Fires from inside the create call, so a subscriber still in the call
    /// chain (e.g. a modal dialog) sees it synchronously. The DMF prompt
    /// coordinator treats it as a pending signal + processes it once the
    /// owning dialog has closed, to avoid a dialog-on-dialog.
    /// </remarks>
    event EventHandler<ProfileSummary>? ProfileCreated;

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
    /// install mod files</b> (the repository holds the files; staging links
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
    /// Pre-checks a base-name collision for the add flow: returns the profile mod
    /// (if any) whose resolved base folder name matches <paramref name="baseName"/>,
    /// excluding <paramref name="excludeContainerId"/> (a re-add of a mod already
    /// in the profile). Used to REFUSE an import that would stage two mods under
    /// the same folder name (the mod loader can't tell them apart).
    /// </summary>
    /// <param name="id">The profile to check.</param>
    /// <param name="baseName">The candidate base folder name (peeked via
    /// <c>IModImportService.GetBaseName</c>).</param>
    /// <param name="excludeContainerId">A container id to skip (the container the
    /// import would dedup to, from
    /// <c>IModImportService.FindExistingContainer</c>); pass <c>null</c> for a
    /// brand-new container.</param>
    /// <returns>The colliding <see cref="ModListEntry"/>, or <c>null</c> if no
    /// profile mod (other than the excluded one) resolves to
    /// <paramref name="baseName"/>.</returns>
    /// <remarks>
    /// Considers <b>all</b> profile mods (enabled <em>and</em> disabled): a
    /// disabled colliding mod could be enabled later. A mod whose base name can't
    /// be resolved (missing container/version, or a corrupted version folder with
    /// zero/multiple subdirs) is skipped silently; it can't collide. Pure query:
    /// no logging, no side effects (the caller decides what to do with a hit).
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is
    /// unknown.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is null,
    /// empty, or whitespace.</exception>
    ModListEntry? GetBaseNameCollision(Guid id, string baseName, Guid? excludeContainerId);

    /// <summary>
    /// Regenerates the profile's staged mod root (the <c>--mod-path</c>) from the
    /// current per-mod version resolution, and writes <c>mods.lst</c> from the
    /// successfully-staged enabled mods in <see cref="ModListEntry.Order"/>.
    /// Idempotent (each call clears + rebuilds <c>staged/</c>). Returns the
    /// <c>--mod-path</c> to pass to the Relay launcher.
    /// </summary>
    /// <remarks>
    /// Staging links, not copies (the repository holds the files). A staging-link
    /// creation failure (e.g. Windows on a non-NTFS volume, or no write access to
    /// the profile's <c>staged/</c> directory) throws <see cref="IOException"/>;
    /// it never silently copies. A mod whose container or resolved version is
    /// missing is skipped with a warning (not a crash); it has no entry in
    /// <c>staged/</c> or <c>mods.lst</c>.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    /// <exception cref="IOException">A staging link could not be created.</exception>
    string PrepareModRoot(Guid id);
}
