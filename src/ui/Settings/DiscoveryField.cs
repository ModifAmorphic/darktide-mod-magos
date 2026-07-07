namespace Modificus.Curator.UI.Settings;

/// <summary>
/// Whether a discovery field is browsed with the folder picker or the file
/// picker. Determines which storage-provider picker the view code-behind opens
/// when the field's Browse button is clicked.
/// </summary>
public enum DiscoveryBrowseKind
{
    /// <summary>
    /// Open the folder picker (the field expects a directory: Steam install,
    /// compatdata).
    /// </summary>
    Folder,

    /// <summary>
    /// Open the file picker (the field expects a single file: the Darktide
    /// binary, the Proton script).
    /// </summary>
    File,
}

/// <summary>
/// One discovery field's metadata: the canonical field name (matching the
/// property names of <see cref="Steam.DiscoveryResult"/> as carried by
/// <c>LaunchResult.MissingDiscoveryFields</c>), the resx key for its
/// human-readable label, and which storage-provider picker browses it. The
/// single source of truth shared by the Settings window (all four fields) and
/// the discovery escape-hatch (only the missing ones), so the two surfaces
/// cannot diverge on field identity, label, or browse kind.
/// </summary>
/// <param name="FieldName">The canonical discovery field name. Matches the
/// values carried by <c>LaunchResult.MissingDiscoveryFields</c>:
/// <c>SteamInstallPath</c>, <c>DarktideGameBinaryPath</c>,
/// <c>CompatdataPath</c>, <c>ProtonBinaryPath</c>.</param>
/// <param name="LabelResxKey">The resx key for the localized human-readable
/// label shown next to the field's TextBox.</param>
/// <param name="BrowseKind">Which storage-provider picker opens when the
/// field's Browse button is clicked.</param>
public sealed record DiscoveryField(
    string FieldName,
    string LabelResxKey,
    DiscoveryBrowseKind BrowseKind);

/// <summary>
/// The catalog of discovery fields + lookup by canonical name. The order of
/// <see cref="All"/> (Steam, Darktide, compatdata, Proton) is the order the
/// Settings window renders the rows top to bottom. Field names match the
/// property names of <see cref="Steam.DiscoveryResult"/> exactly, because
/// <c>IPlatformLaunchStrategy.RequiredDiscoveryFields</c> emits those names
/// and they flow untouched through <c>LaunchResult.MissingDiscoveryFields</c>
/// to the escape-hatch.
/// </summary>
public static class DiscoveryFields
{
    /// <summary>
    /// Steam client install directory (folder picker). Becomes
    /// <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c> at launch.
    /// </summary>
    public static readonly DiscoveryField SteamInstall = new(
        FieldName: "SteamInstallPath",
        LabelResxKey: "Discovery_SteamInstallLabel",
        BrowseKind: DiscoveryBrowseKind.Folder);

    /// <summary>
    /// Native path to <c>Darktide.exe</c> (file picker). Passed as
    /// <c>--game-binary</c> (Z:\-translated under Proton on Linux).
    /// </summary>
    public static readonly DiscoveryField DarktideBinary = new(
        FieldName: "DarktideGameBinaryPath",
        LabelResxKey: "Discovery_DarktideBinaryLabel",
        BrowseKind: DiscoveryBrowseKind.File);

    /// <summary>
    /// Wine prefix / compatdata directory (folder picker, Linux only). Becomes
    /// <c>STEAM_COMPAT_DATA_PATH</c> at launch.
    /// </summary>
    public static readonly DiscoveryField Compatdata = new(
        FieldName: "CompatdataPath",
        LabelResxKey: "Discovery_CompatdataLabel",
        BrowseKind: DiscoveryBrowseKind.Folder);

    /// <summary>
    /// <c>proton</c> script path (file picker, Linux only). Invoked as
    /// <c>proton run</c> to launch the launcher under Wine.
    /// </summary>
    public static readonly DiscoveryField ProtonBinary = new(
        FieldName: "ProtonBinaryPath",
        LabelResxKey: "Discovery_ProtonBinaryLabel",
        BrowseKind: DiscoveryBrowseKind.File);

    /// <summary>
    /// All four discovery fields, in the canonical render order. The Settings
    /// window iterates this list; the escape-hatch filters it by the missing
    /// field names it was handed.
    /// </summary>
    public static readonly IReadOnlyList<DiscoveryField> All = new[]
    {
        SteamInstall,
        DarktideBinary,
        Compatdata,
        ProtonBinary,
    };

    /// <summary>
    /// Looks up a discovery field by its canonical name. Returns <c>null</c>
    /// for an unknown name (graceful: a future field name the catalog does not
    /// know yet is dropped silently rather than crashing the escape-hatch).
    /// </summary>
    /// <param name="fieldName">The canonical field name (case-sensitive,
    /// ordinal match).</param>
    public static DiscoveryField? Find(string fieldName) =>
        All.FirstOrDefault(f => string.Equals(f.FieldName, fieldName, StringComparison.Ordinal));
}
