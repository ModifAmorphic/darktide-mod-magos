namespace Magos.Modificus.SharedMods;

/// <summary>
/// Imports a local mod source (a folder OR a <c>.zip</c> archive) into the
/// global shared store. The mod-list UI's add flow (picker + drag-and-drop)
/// goes through this seam: the UI never touches the filesystem directly.
/// </summary>
/// <remarks>
/// <para>
/// Import does the file work (recursive copy for a folder;
/// <see cref="System.IO.Compression.ZipFile.ExtractToDirectory"/> for a
/// <c>.zip</c>) into <c>&lt;SharedModsFolder&gt;/&lt;modName&gt;/</c>, then
/// upserts the shared-store entry via <see cref="ISharedModStore.Add"/> with the
/// declared source + version + the resolved path. First import of a mod name
/// establishes the shared copy (the existing shared-first staging); a re-import
/// upserts: replaces the files + the manifest entry.</para>
/// <para>
/// This service does NOT touch profile mod lists: the caller adds the profile
/// reference via <c>IProfileService.AddMod</c> after the import succeeds (order
/// matters: import the shared copy, then reference it from the profile).</para>
/// <para>
/// Registered via <c>AddSharedMods()</c>. Resolves <see cref="ISharedModStore"/>,
/// <c>MagosConfig</c> (for <c>SharedModsFolder</c>), and an
/// <c>ILogger&lt;ModImportService&gt;</c> from the container.</para>
/// </remarks>
public interface IModImportService
{
    /// <summary>
    /// Imports a local mod source into the global shared store.
    /// </summary>
    /// <param name="sourcePath">Absolute path to a folder OR a <c>.zip</c>
    /// archive. A path ending in <c>.zip</c> (ordinal ignore-case) is extracted;
    /// anything else is treated as a folder path and recursively copied.</param>
    /// <param name="modName">The shared-store key + the on-disk folder name
    /// (<c>&lt;SharedModsFolder&gt;/&lt;modName&gt;</c>), confined to a single
    /// direct child of the shared root: it must not contain path separators,
    /// <c>..</c>, or be an absolute path. An existing folder of this name is
    /// deleted first (upsert semantics).</param>
    /// <param name="source">The mod's source provenance (Local / Nexus / GitHub),
    /// recorded on the entry so a pinned version is legible.</param>
    /// <param name="version">The raw release tag string (e.g. <c>"1.2"</c>,
    /// <c>"v2.0.1"</c>); recorded as <see cref="SharedModEntry.ActualVersion"/>.
    /// Pass <see cref="string.Empty"/> for an untracked/local import.</param>
    /// <returns>The upserted <see cref="SharedModEntry"/> (carries the recorded
    /// <see cref="SharedModEntry.Source"/> + <see cref="SharedModEntry.ActualVersion"/>
    /// + <see cref="SharedModEntry.Path"/>).</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="modName"/>
    /// is null/whitespace, or is not a single direct child of the shared root
    /// (it contains path separators, <c>..</c>, or is an absolute path).</exception>
    /// <exception cref="FileNotFoundException">Thrown if <paramref name="sourcePath"/>
    /// does not exist.</exception>
    /// <exception cref="System.IO.IOException">Thrown on any I/O failure (copy,
    /// delete, extract).</exception>
    /// <exception cref="System.IO.InvalidDataException">Thrown if a <c>.zip</c>
    /// archive is malformed.</exception>
    SharedModEntry Import(string sourcePath, string modName, ModSource source, string version);
}
