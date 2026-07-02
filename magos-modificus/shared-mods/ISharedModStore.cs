namespace Magos.Modificus.SharedMods;

/// <summary>
/// The global shared mod store — the manifest of mods that live shared-first
/// across profiles. Owns <c>&lt;SharedModsFolder&gt;/shared-manifest.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is the index; the mod files live at each entry's
/// <see cref="SharedModEntry.Path"/> (placed there by Phase 4 acquisition). This
/// interface manages the manifest only — <see cref="Add"/> assumes the files are
/// already at the path, and <see cref="Remove"/> drops the manifest entry (the
/// files are the acquisition's responsibility).</para>
/// <para>
/// Registered via <c>AddSharedMods()</c>; <c>ProfileService</c> depends on this
/// for staging (the shared-first allocation seam).</para>
/// </remarks>
public interface ISharedModStore
{
    /// <summary>All shared-store entries, in stored order.</summary>
    IReadOnlyList<SharedModEntry> List();

    /// <summary>Looks up an entry by mod name (ordinal). Null if absent.</summary>
    SharedModEntry? Get(string name);

    /// <summary>
    /// Adds or replaces (upsert) the entry in the manifest. <b>Assumes the mod
    /// files are already at <see cref="SharedModEntry.Path"/></b> — Phase 2
    /// manages the manifest, not the downloads (acquisition is Phase 4). A
    /// re-add with the same <see cref="SharedModEntry.Name"/> replaces the prior
    /// entry (idempotent update).
    /// </summary>
    void Add(SharedModEntry entry);

    /// <summary>
    /// Removes the entry from the manifest (idempotent — a missing name is a
    /// no-op). The mod files are <b>not</b> touched (they're the acquisition's
    /// responsibility; other profiles may still share them).
    /// </summary>
    void Remove(string name);
}
