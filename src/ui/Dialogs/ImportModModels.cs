using Modificus.Curator.Mods;

namespace Modificus.Curator.UI.Dialogs;

/// <summary>
/// The request payload for the per-mod import modal
/// (<see cref="IDialogService.ShowImportModAsync"/>). Carries the pre-filled
/// mod name (derived from the folder / archive stem the user picked or dropped)
/// and the local source path the import service will copy / extract from.
/// </summary>
/// <remarks>
/// <see cref="ModName"/> is mutable: the import modal two-way binds its name field
/// here, so the user's edit (rename at import) flows back to the caller through
/// the same request object. This keeps <see cref="ImportModResult"/> focused on
/// source + version while still honoring the "editable name establishes the
/// canonical mod-store key" rule (spec §3c). On confirm, the modal trims +
/// writes back the edited name; the add flow then reads it for the canonical key.
/// </remarks>
public sealed record ImportModRequest
{
    /// <summary>The mod-store key + on-disk folder name. Pre-filled from the
    /// folder / archive stem; the modal two-way binds + writes the user's edit
    /// back here. The caller reads this after the modal returns to use the
    /// canonical (possibly edited) name.</summary>
    public string ModName { get; set; }

    /// <summary>Absolute path to a folder OR an archive on disk. Handed
    /// straight to <c>IModImportService.Import</c> when the modal is
    /// confirmed.</summary>
    public string SourcePath { get; set; }

    /// <summary>Creates the request with the pre-filled name + source path.</summary>
    public ImportModRequest(string modName, string sourcePath)
    {
        ModName = modName;
        SourcePath = sourcePath;
    }
}

/// <summary>
/// The outcome of a confirmed import modal: the parsed canonical
/// <see cref="ModSource"/> (Local / Nexus / GitHub, URL resolved to identity) +
/// the raw version string the user typed + the chosen version policy (Latest or
/// Pinned). A cancelled modal yields <c>null</c> (the caller stops the batch).
/// The version is a raw release tag (arbitrary GitHub / Nexus string), never
/// parsed or normalized.
/// </summary>
/// <param name="Source">The parsed canonical source. <see cref="UntrackedSource"/>
/// for an untracked import; <see cref="NexusSource"/> / <see cref="GitHubSource"/>
/// when the user supplied a URL the parser resolved.</param>
/// <param name="Version">The raw release tag string (e.g. <c>"1.2"</c>,
/// <c>"v2.0.1"</c>). <see cref="string.Empty"/> for a local / untracked import.</param>
/// <param name="Policy">The chosen version policy: <see cref="LatestPolicy"/>
/// (the default) tracks the newest release; <see cref="PinnedPolicy"/> freezes
/// the profile entry to the version being imported. For Pinned, the policy
/// carries an empty <see cref="PinnedPolicy.VersionId"/> here (the modal does not
/// know the opaque version id; the add flow substitutes the id returned by
/// <c>IModImportService.Import</c>).</param>
public sealed record ImportModResult(ModSource Source, string Version, ModVersionPolicy Policy);
