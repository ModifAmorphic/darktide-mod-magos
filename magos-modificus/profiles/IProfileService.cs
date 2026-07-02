using Magos.Modificus.SharedMods;

namespace Magos.Modificus.Profiles;

/// <summary>
/// Profile + per-profile mod-list management. Owns the profile data model,
/// its on-disk persistence, and the projection of the mod list into a staged
/// mod root (symlinks) + <c>mods.lst</c> for the Enginseer runtime.
/// </summary>
/// <remarks>
/// <para><b>Phase 1 → Phase 2 stability:</b> the storage implementation swapped
/// (per-profile <c>mods/</c> dir → shared-first + <c>staged/</c> symlinks)
/// without changing the surface:</para>
/// <list type="bullet">
/// <item><see cref="PrepareModRoot"/> abstracts "give me the <c>--mod-path</c>" —
/// Phase 1 returned the per-profile <c>mods/</c> dir; Phase 2 returns a staged
/// dir built from shared-first resolution (signature unchanged, impl swapped).</item>
/// <item><see cref="ModListEntry"/> grew <see cref="ModListEntry.Policy"/>
/// (additive; default Latest — Phase 1 callers unaffected).</item>
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
    /// Creates a new profile: generates the id, scaffolds its directory tree
    /// (<c>staged/</c> + <c>diverged/</c>) + persists an empty <c>profile.json</c>.
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
    /// = true) with the default policy (<see cref="ModVersionPolicy.Latest"/>).
    /// <b>List entry only — does NOT fetch or install mod files.</b>
    /// Idempotent: adding a name already in the list is a no-op.
    /// </summary>
    /// <remarks>
    /// Phase 1-compatible overload. The spec's <c>AddMod(id, name, policy=Latest)</c>
    /// is realized as this overload because C# default-parameter values must be
    /// compile-time constants and <see cref="ModVersionPolicy"/> is a reference
    /// type (can't be <c>const</c>); this overload preserves the no-policy call
    /// site <c>AddMod(id, "DMF")</c> unaffected.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void AddMod(Guid id, string modName);

    /// <summary>
    /// Adds a mod entry to the end of the list (<see cref="ModListEntry.Enabled"/>
    /// = true) with an explicit version policy. <b>List entry only — does NOT
    /// fetch or install mod files.</b> Idempotent: adding a name already in the
    /// list is a no-op (the existing entry's policy is left unchanged).
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    void AddMod(Guid id, string modName, ModVersionPolicy policy);

    /// <summary>
    /// Changes a profile mod's <see cref="ModListEntry.Policy"/> and recomputes
    /// the divergence resolution against the shared store:
    /// <list type="bullet">
    /// <item><b>share→diverge:</b> records the policy (the profile now needs a
    /// local copy). The actual file acquisition (creating <c>diverged/&lt;mod&gt;/</c>
    /// at the profile's version) is <b>Phase 4</b>; staging looks for it and
    /// skips + warns if Phase 4 hasn't placed it yet.</item>
    /// <item><b>diverge→share:</b> the local copy is no longer needed — drops
    /// <c>diverged/&lt;mod&gt;/</c> to reclaim space (re-divergence re-acquires).</item>
    /// </list>
    /// If the mod has no shared-store entry, only the policy is recorded (no
    /// divergence transition applies until acquisition populates the store).
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="modName"/> is not in the profile's list.
    /// </exception>
    void SetModPolicy(Guid id, string modName, ModVersionPolicy policy);

    /// <summary>
    /// Removes the mod entry and the mod's profile-local (<c>diverged/</c>)
    /// files, if any. A missing local copy for a listed mod is graceful (not a
    /// crash). The shared-store copy is <b>not</b> touched (other profiles may
    /// still share it).
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// <paramref name="id"/> is unknown, or <paramref name="modName"/> is not in the profile's list.
    /// </exception>
    void RemoveMod(Guid id, string modName);

    /// <summary>
    /// Regenerates the profile's staged mod root (the <c>--mod-path</c>) from the
    /// current shared-first resolution, and writes <c>mods.lst</c> from the
    /// successfully-staged enabled mods in <see cref="ModListEntry.Order"/>.
    /// Idempotent (each call clears + rebuilds <c>staged/</c>). Returns the
    /// <c>--mod-path</c> to pass to the Enginseer launcher.
    /// </summary>
    /// <remarks>
    /// Symlinks, not copies (download once, store once). A symlink-creation
    /// failure (e.g. Windows without symlink permissions / Developer Mode) throws
    /// <see cref="SymlinkStagingException"/> — it never silently copies. A mod
    /// that resolves to Diverge but whose <c>diverged/</c> copy is absent (Phase 4
    /// hasn't acquired it) is skipped with a warning, not a crash.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> is unknown.</exception>
    /// <exception cref="SymlinkStagingException">A symlink could not be created.</exception>
    string PrepareModRoot(Guid id);
}
