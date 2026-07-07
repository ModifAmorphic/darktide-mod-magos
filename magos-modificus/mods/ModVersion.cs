namespace Magos.Modificus.Mods;

/// <summary>
/// One version of a mod, persisted as an entry inside its
/// <see cref="ModContainer"/>'s <c>container.json</c>. The mod's files live at
/// <c>&lt;ModsFolder&gt;/&lt;containerUUID&gt;/&lt;Folder&gt;/</c>; this
/// record is the manifest line that maps that opaque folder to its display
/// <see cref="VersionString"/> + the <see cref="IsLatest"/> flag.
/// </summary>
/// <remarks>
/// <para>Immutable record: mutations go through <see cref="IModRepository.AddVersion"/>
/// / <see cref="IModRepository.RemoveVersion"/>, which rebuild the container's
/// manifest on disk. <see cref="Folder"/> is an opaque unique ID (UUID-derived)
/// never the raw version tag, so filesystem-illegal characters + sanitization
/// collisions are non-issues (the raw tag lives only in
/// <see cref="VersionString"/>, for display).</para>
/// <para>
/// <see cref="IsLatest"/> is a single flag on one version per container (the
/// one a <see cref="LatestPolicy"/> profile resolves to). Moving latest is a
/// one-field manifest edit; profiles resolve dynamically at stage time, so
/// <c>isLatest</c> changing never requires a profile-entry update.</para>
/// <para>
/// <see cref="ImportedAt"/> orders the versions (newest = the most recently
/// imported). <see cref="IModRepository.AddVersion"/> stamps it on insert;
/// re-importing the same <see cref="VersionString"/> reuses the existing entry
/// unchanged (no reordering).</para>
/// </remarks>
public sealed record ModVersion
{
    /// <summary>
    /// The opaque version-folder ID (UUID-derived). The version's files live at
    /// <c>&lt;ModsFolder&gt;/&lt;containerUUID&gt;/&lt;Folder&gt;/</c>.
    /// Never the raw version tag.
    /// </summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>
    /// The raw release tag (e.g. <c>"1.2"</c>, <c>"v2.0.1"</c>,
    /// <c>"1.0.0-beta"</c>), stored verbatim. Used for display + for resolving
    /// a <see cref="PinnedPolicy"/> pin to this version. Arbitrary source tags
    /// are not SemVer; never parsed or normalized at this layer.
    /// </summary>
    public string VersionString { get; init; } = string.Empty;

    /// <summary>
    /// Whether this is the container's current "latest" version (the one a
    /// <see cref="LatestPolicy"/> profile resolves to). Exactly one version per
    /// container carries this flag (the newest by <see cref="ImportedAt"/>);
    /// the repository flips it on add/remove.
    /// </summary>
    public bool IsLatest { get; init; }

    /// <summary>
    /// When this version was first imported (UTC). Orders the versions; the
    /// newest <see cref="ImportedAt"/> carries <see cref="IsLatest"/>.
    /// </summary>
    public DateTimeOffset ImportedAt { get; init; }

    /// <summary>
    /// When the underlying remote file was published (UTC), captured at
    /// acquisition time for remote-source mods (Nexus). <c>null</c> for
    /// non-remote / manual imports (folder/archive via the picker or
    /// drag-and-drop), which aren't update-checked anyway. Used by
    /// <c>UpdateCheckService</c> as the primary comparison basis against
    /// <see cref="Integrations.ModUpdate.LatestFileUpdateUtc"/>; falls back to
    /// <see cref="ImportedAt"/> when null (versions imported before this field
    /// existed, or non-Nexus sources).
    /// </summary>
    /// <remarks>
    /// Backward-compatible on disk: a <c>container.json</c> from before this
    /// field existed deserializes it to <c>null</c> (System.Text.Json default
    /// for a missing nullable property), and the update check falls back to
    /// <see cref="ImportedAt"/>. No migration. Owned by the acquisition layer
    /// (Integrations), which passes it through
    /// <see cref="IModImportService.Import"/> / <see cref="IModRepository.AddVersion"/>;
    /// those seams stay source-agnostic (the param is nullable + unused for
    /// non-Nexus).
    /// </remarks>
    public DateTimeOffset? RemoteUploadedAt { get; init; }
}
