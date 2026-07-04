namespace Magos.Modificus.Mods;

/// <summary>
/// Imports a local mod source (a folder OR a <c>.zip</c> archive) into the
/// global mod repository. The mod-list UI's add flow (picker + drag-and-drop)
/// goes through this seam: the UI never touches the filesystem directly.
/// </summary>
/// <remarks>
/// <para>
/// Import resolves (or creates) the container for the source, then extracts a
/// <c>.zip</c> / copies a folder into the repository-managed opaque version
/// folder via <see cref="IModRepository.AddVersion"/>. Container dedup:
/// Untracked by name, Nexus by mod id, GitHub by owner/repo. Version dedup:
/// re-importing the same <paramref name="version"/> tag reuses its folder
/// (refreshed); a new tag creates a new version + flips <see cref="ModVersion.IsLatest"/>.</para>
/// <para>
/// This service does NOT touch profile mod lists: the caller adds the profile
/// reference via <c>IProfileService.AddMod</c> after the import succeeds (order
/// matters: import the repository copy, then reference it from the profile).</para>
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
    /// <exception cref="System.IO.IOException">Thrown on any I/O failure (copy,
    /// delete, extract).</exception>
    /// <exception cref="System.IO.InvalidDataException">Thrown if a <c>.zip</c>
    /// archive is malformed.</exception>
    (Guid ContainerId, string VersionString) Import(string sourcePath, string modName, ModSource source, string version);
}
