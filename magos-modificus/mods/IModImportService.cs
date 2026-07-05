namespace Magos.Modificus.Mods;

/// <summary>
/// Imports a local mod source (a folder OR a <c>.zip</c> archive) into the
/// global mod repository. The mod-list UI's add flow (picker + drag-and-drop)
/// goes through this seam: the UI never touches the filesystem directly.
/// </summary>
/// <remarks>
/// <para>
/// Import resolves (or creates) the container for the source, validates the
/// source's structure, then extracts a <c>.zip</c> / copies a folder into the
/// repository-managed opaque version folder via
/// <see cref="IModRepository.AddVersion"/>. Container dedup: Untracked by name,
/// Nexus by mod id, GitHub by owner/repo. Version dedup: re-importing the same
/// <paramref name="version"/> tag reuses its folder (refreshed); a new tag
/// creates a new version + flips <see cref="ModVersion.IsLatest"/>.</para>
/// <para>
/// <b>Source validation (both kinds):</b> the source must contain exactly one
/// base directory with a <c>&lt;base&gt;.mod</c> descriptor inside it (the
/// descriptor filename matches the base folder name). A <c>.zip</c> is inspected
/// <em>before</em> extraction (single top-level folder, no loose top-level
/// files, matching descriptor); a folder is checked directly (non-empty +
/// matching descriptor). An invalid source throws
/// <see cref="InvalidOperationException"/> immediately, placing no files and
/// creating no container/version. The validated base folder is preserved under
/// <c>&lt;versionFolder&gt;/&lt;base&gt;/</c> for both kinds (the folder import
/// copies the folder <em>itself</em>, not its contents), which is what staging's
/// base-folder discovery relies on. Mods bake their folder name into their code,
/// so the base folder name is load-bearing at staging time.</para>
/// <para>
/// This service does NOT touch profile mod lists: the caller adds the profile
/// reference via <c>IProfileService.AddMod</c> after the import succeeds (order
/// matters: import the repository copy, then reference it from the profile). The
/// caller is expected to catch a structure-validation (or I/O) failure per mod
/// and surface it rather than crash.</para>
/// <para>
/// <b>Base-name collision hard-block:</b> the add flow peeks the base name (via
/// <see cref="GetBaseName"/>) + the would-be container (via
/// <see cref="FindExistingContainer"/>) BEFORE importing, then asks
/// <c>IProfileService.GetBaseNameCollision</c> whether any existing profile mod
/// (a different container) resolves to the same base name. If so, the import is
/// refused: nothing is created. Two mods with the same base folder name can't
/// coexist in one profile (the loader can't tell them apart). The collision
/// block lives at the add flow (not in <see cref="Import"/> itself), so direct
/// programmatic imports remain unconditional.</para>
/// <para>
/// Registered via <c>AddMods()</c>. Resolves <see cref="IModRepository"/>,
/// <see cref="Magos.Modificus.General.IConfigLoader"/> (for the live
/// <c>ModsFolder</c>), and an <c>ILogger&lt;ModImportService&gt;</c> from the
/// container.</para>
/// </remarks>
public interface IModImportService
{
    /// <summary>
    /// Imports a local mod source into the global repository, resolving (or
    /// creating) the container + version.
    /// </summary>
    /// <param name="sourcePath">Absolute path to a folder OR a <c>.zip</c>
    /// archive. A path ending in <c>.zip</c> (ordinal ignore-case) is extracted;
    /// anything else is treated as a folder path and recursively copied.</param>
    /// <param name="modName">The container display name + the untracked dedup
    /// key. Confined to a single direct child of the mod root: it must not
    /// contain path separators, <c>..</c>, or be an absolute path.</param>
    /// <param name="source">The mod's source provenance (Untracked / Nexus /
    /// GitHub). Nexus/GitHub dedup by their source identity; Untracked by
    /// <paramref name="modName"/>.</param>
    /// <param name="version">The raw release tag string (e.g. <c>"1.2"</c>,
    /// <c>"v2.0.1"</c>); the version's dedup key within the container. Pass
    /// <see cref="string.Empty"/> for an untracked/local import with no tag.</param>
    /// <returns>The <c>(containerId, versionString)</c> pair the caller feeds to
    /// <c>IProfileService.AddMod(profileId, containerId, policy)</c>.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="modName"/>
    /// is null/whitespace, or is not a single direct child of the mod root
    /// (it contains path separators, <c>..</c>, or is an absolute path).</exception>
    /// <exception cref="FileNotFoundException">Thrown if <paramref name="sourcePath"/>
    /// does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the source's
    /// structure is invalid (not exactly one base directory with a matching
    /// <c>&lt;base&gt;.mod</c> descriptor). No files are placed and no
    /// container/version is created.</exception>
    /// <exception cref="System.IO.IOException">Thrown on any I/O failure (copy,
    /// delete, extract).</exception>
    /// <exception cref="System.IO.InvalidDataException">Thrown if a <c>.zip</c>
    /// archive is malformed.</exception>
    (Guid ContainerId, string VersionString) Import(string sourcePath, string modName, ModSource source, string version);

    /// <summary>
    /// Peeks the mod's base folder name from a source <em>without</em> creating
    /// a container or version (a read-only validation). Used by the add flow to
    /// pre-check a base-name collision against the active profile BEFORE
    /// importing.
    /// </summary>
    /// <param name="sourcePath">Absolute path to a folder OR a <c>.zip</c> archive
    /// (same detection as <see cref="Import"/>: a <c>.zip</c> extension, ordinal
    /// ignore-case, is inspected as an archive; anything else is treated as a
    /// folder path).</param>
    /// <returns>The validated base folder name (the single top-level directory for
    /// a <c>.zip</c>, the picked folder's own name for a folder). The descriptor
    /// filename (<c>&lt;base&gt;.mod</c>) matches the base folder name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> is
    /// null.</exception>
    /// <exception cref="FileNotFoundException">Thrown if <paramref name="sourcePath"/>
    /// does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the source's structure
    /// is invalid (the same validation as <see cref="Import"/>: not exactly one
    /// base directory with a matching <c>&lt;base&gt;.mod</c> descriptor). No
    /// files are placed and no container/version is created.</exception>
    /// <exception cref="System.IO.IOException">Thrown on any I/O failure reading
    /// the source.</exception>
    /// <exception cref="System.IO.InvalidDataException">Thrown if a <c>.zip</c>
    /// archive is malformed.</exception>
    /// <remarks>
    /// This is a pure peek: it validates the source structure (reusing the same
    /// private validation as <see cref="Import"/>, so the two cannot drift) and
    /// returns the base name, but creates no container, no version, and places no
    /// files. The add flow calls this before <see cref="Import"/> to decide
    /// whether the import would collide with an existing profile mod; the
    /// subsequent <see cref="Import"/> re-validates (validation is cheap, and
    /// <see cref="Import"/> must remain self-validating for direct callers).
    /// </remarks>
    string GetBaseName(string sourcePath);

    /// <summary>
    /// Resolves the container an import of <c>(<paramref name="source"/>,
    /// <paramref name="modName"/>)</c> <em>would</em> dedup to, if one already
    /// exists, without creating anything. Returns <c>null</c> if the import would
    /// create a new container.
    /// </summary>
    /// <param name="source">The mod's source provenance. Untracked dedups by
    /// <paramref name="modName"/>; Nexus/GitHub dedup by source identity.</param>
    /// <param name="modName">The container display name + the untracked dedup
    /// key.</param>
    /// <returns>The existing container the import would reuse, or <c>null</c> if
    /// it would create a new one.</returns>
    /// <remarks>
    /// Used by the add flow to exclude a re-add of a mod already in the profile
    /// from the base-name collision check: a re-add resolves to the same
    /// container, and <c>IProfileService.AddMod</c> is idempotent on its
    /// containerId, so it must NOT be flagged as a collision. Mirrors
    /// <see cref="Import"/>'s container resolution minus the create, so the dedup
    /// rules live in exactly one place (this service).
    /// </remarks>
    ModContainer? FindExistingContainer(ModSource source, string modName);
}
