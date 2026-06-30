namespace Magos.Modificus.Profiles;

/// <summary>
/// Profile + per-profile mod-list management. Owns the profile data model,
/// its on-disk persistence, and the projection of the mod list into
/// <c>mods.lst</c> for the Enginseer runtime.
/// </summary>
/// <remarks>
/// <para><b>Phase 1 → Phase 2 stability:</b> this interface is designed so the
/// storage implementation can swap (per-profile dirs → shared-first + staging)
/// without changing the surface:</para>
/// <list type="bullet">
/// <item><see cref="PrepareModRoot"/> abstracts "give me the <c>--mod-path</c>" —
/// Phase 1 returns the per-profile <c>mods/</c> dir; Phase 2 returns a staged
/// dir built from shared-first resolution.</item>
/// <item><see cref="ModListEntry"/> will grow fields (version policy, source) but
/// the Phase 1 fields stay.</item>
/// <item>No storage details (paths, shared-vs-local) leak through the interface.</item>
/// </list>
/// </remarks>
public interface IProfileService
{
    /// <summary>All known profiles, as lightweight summaries.</summary>
    IReadOnlyList<ProfileSummary> ListProfiles();

    /// <summary>Loads the full profile (metadata + mod list).</summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    Profile GetProfile(Guid id);

    /// <summary>
    /// Creates a new profile: generates the id, scaffolds its directory + mod
    /// root, and persists an empty <c>profile.json</c>.
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
    /// <paramref name="modNamesInOrder"/>. Mods not mentioned keep their relative
    /// order, appended after the listed ones; names in the list that aren't in
    /// the profile are ignored. No mods are added or removed.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void SetModOrder(Guid id, IReadOnlyList<string> modNamesInOrder);

    /// <summary>Toggles <see cref="ModListEntry.Enabled"/> for a single mod.</summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="modName"/> is not in the profile's list.
    /// </exception>
    void SetModEnabled(Guid id, string modName, bool enabled);

    /// <summary>
    /// Adds a mod entry to the end of the list (<see cref="ModListEntry.Enabled"/>
    /// = true). <b>List entry only — does NOT fetch or install mod files.</b>
    /// Idempotent: adding a name already in the list is a no-op.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void AddMod(Guid id, string modName);

    /// <summary>
    /// Removes the mod entry and, if present, deletes its directory under the
    /// profile's mod root. A missing mod directory for a listed mod is graceful
    /// (not a crash).
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="modName"/> is not in the profile's list.
    /// </exception>
    void RemoveMod(Guid id, string modName);

    /// <summary>
    /// Ensures the profile's mod root exists and writes <c>mods.lst</c> from the
    /// current mod list (enabled mods, in <see cref="ModListEntry.Order"/>).
    /// Idempotent. Returns the <c>--mod-path</c> to pass to the Enginseer launcher.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    string PrepareModRoot(Guid id);
}
