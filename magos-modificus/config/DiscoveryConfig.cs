namespace Magos.Modificus.Config;

/// <summary>
/// User-supplied overrides for Steam/Darktide discovery. Bound from the
/// <c>Discovery</c> section of <see cref="MagosConfig"/> by the config loader
/// in <c>Magos.Modificus.General</c>. Every field is nullable and defaults to
/// <c>null</c>, meaning "auto-discover this field": an absent section yields a
/// fully-defaulted (all-null) instance, which leaves discovery unchanged.
/// </summary>
/// <remarks>
/// <para>
/// The upcoming Settings window (Phase 3 Track C, Phase 2) + escape-hatch dialog
/// write these; <see cref="General.IConfigLoader"/> persists them, and
/// <c>SteamService.Discover()</c> reads them live (one <c>Load()</c> per call)
/// so a Settings write is visible on the next discovery pass.</para>
/// <para>
/// <b>Trust convention:</b> a supplied value is used as-is with no re-verify
/// (the user said "use this"). A wrong path surfaces later, at launch, as a
/// <c>LaunchResult.Status</c> of <c>Error</c>; the user then corrects it. Null
/// or whitespace means "fall back to whatever the platform discoverer found"
/// (which may itself be null, reported via <c>DiscoveryResult.Status</c>).</para>
/// <para>
/// Field mapping to <see cref="Steam.DiscoveryResult"/> (the overlay replaces
/// the auto-discovered value when non-null/non-whitespace):
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
    /// = auto-discover.
    /// </summary>
    public string? UserSteamInstallPath { get; set; }

    /// <summary>
    /// User override for the native path to <c>Darktide.exe</c>. Null/whitespace
    /// = auto-discover.
    /// </summary>
    public string? UserDarktideGameBinaryPath { get; set; }

    /// <summary>
    /// User override for the Wine prefix (compatdata) directory
    /// (the value for <c>STEAM_COMPAT_DATA_PATH</c>). Linux only; ignored on
    /// Windows (native). Null/whitespace = auto-discover.
    /// </summary>
    public string? UserCompatdataPath { get; set; }

    /// <summary>
    /// User override for the <c>proton</c> script path used for
    /// <c>proton run</c>. Linux only; ignored on Windows (native).
    /// Null/whitespace = auto-discover.
    /// </summary>
    public string? UserProtonBinaryPath { get; set; }
}
