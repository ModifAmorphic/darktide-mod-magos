namespace Modificus.Curator.Config;

/// <summary>
/// User-supplied overrides for Steam/Darktide discovery. Bound from the
/// <c>Discovery</c> section of <see cref="CuratorConfig"/> by the config loader
/// in <c>Modificus.Curator.General</c>. Every field is nullable and defaults to
/// <c>null</c>, meaning "no override yet": on the first
/// <see cref="Steam.ISteamService.Discover"/> call, missing fields are healed
/// from the platform discoverer and persisted here so the next call is a fast
/// validation (no discoverer run). A user-supplied value is re-validated on
/// disk every call: if it ceases to exist, it is healed again.
/// </summary>
/// <remarks>
/// <para>
/// The Settings window + the discovery escape-hatch dialog write these;
/// <see cref="General.IConfigLoader"/> persists them, and
/// <c>SteamService.Discover()</c> reads them live (one <c>Load()</c> per call)
/// so a Settings write is visible on the next discovery pass.</para>
/// <para>
/// <b>Validate + heal + persist (Track C review fix):</b> a supplied value is
/// checked on disk (directory for Steam install + compatdata; file for the
/// Darktide binary + Proton script). A value that exists is kept as-is
/// (preserved across calls). A null/whitespace value, or one whose path no
/// longer exists, is <i>healed</i> from the platform discoverer when possible,
/// and the healed value is persisted back here (only that field; the others
/// are untouched). A field the discoverer also cannot resolve stays null and
/// is flagged via <see cref="Steam.DiscoveryResult.Status"/>.</para>
/// <para>
/// Field mapping to <see cref="Steam.DiscoveryResult"/> (the final path is the
/// override when it exists on disk, otherwise the discoverer's value):
/// <list type="table">
/// <listheader><term>Override</term><description>DiscoveryResult field</description></listheader>
/// <item><term><see cref="UserSteamInstallPath"/></term><description><see cref="Steam.DiscoveryResult.SteamInstallPath"/></description></item>
/// <item><term><see cref="UserDarktideGameBinaryPath"/></term><description><see cref="Steam.DiscoveryResult.DarktideGameBinaryPath"/></description></item>
/// <item><term><see cref="UserCompatdataPath"/></term><description><see cref="Steam.DiscoveryResult.CompatdataPath"/></description></item>
/// <item><term><see cref="UserProtonBinaryPath"/></term><description><see cref="Steam.DiscoveryResult.ProtonBinaryPath"/></description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class DiscoveryConfig
{
    /// <summary>
    /// User override for the Steam client install directory
    /// (the value for <c>STEAM_COMPAT_CLIENT_INSTALL_PATH</c>). Null/whitespace
    /// or a non-existent path is healed from the platform discoverer on the
    /// next <see cref="Steam.ISteamService.Discover"/> call.
    /// </summary>
    public string? UserSteamInstallPath { get; set; }

    /// <summary>
    /// User override for the native path to <c>Darktide.exe</c>. Null/whitespace
    /// or a non-existent path is healed from the platform discoverer on the
    /// next <see cref="Steam.ISteamService.Discover"/> call.
    /// </summary>
    public string? UserDarktideGameBinaryPath { get; set; }

    /// <summary>
    /// User override for the Wine prefix (compatdata) directory
    /// (the value for <c>STEAM_COMPAT_DATA_PATH</c>). Linux only; never checked
    /// or healed on Windows (native). Null/whitespace or a non-existent path is
    /// healed from the platform discoverer on the next
    /// <see cref="Steam.ISteamService.Discover"/> call.
    /// </summary>
    public string? UserCompatdataPath { get; set; }

    /// <summary>
    /// User override for the <c>proton</c> script path used for
    /// <c>proton run</c>. Linux only; never checked or healed on Windows
    /// (native). Null/whitespace or a non-existent path is healed from the
    /// platform discoverer on the next <see cref="Steam.ISteamService.Discover"/>
    /// call.
    /// </summary>
    public string? UserProtonBinaryPath { get; set; }
}
