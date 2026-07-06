using Magos.Modificus.Mods;

namespace Magos.Modificus.Integrations;

/// <summary>
/// Acquires a mod from a remote source: resolves the download link, fetches the
/// mod's metadata, downloads the archive to a temp file, and imports it into the
/// unified mod repository via <see cref="IModImportService.Import"/>. The caller
/// (the nxm download handler, or Stage 5's per-mod update button) owns profile
/// registration: this service returns the <c>(containerId, versionId)</c> pair
/// and the caller feeds it to <c>IProfileService.AddMod</c>.
/// </summary>
/// <remarks>
/// <para>
/// The interface accommodates both Nexus and GitHub, but only the Nexus method is
/// implemented in Stage 3 (there is no GitHub-acquisition trigger yet; Stage 5's
/// update button for GitHub-sourced mods is where <c>AcquireFromGitHubAsync</c>
/// would land). The signature carries an <see cref="IProgress{T}"/> so Stage 5
/// can wire a per-row progress indicator without retooling the seam.</para>
/// <para>
/// <b>No degraded metadata fallback.</b> If the metadata fetch (mod name or file
/// version) fails, the acquisition fails with a clear error. A mod stored under
/// its numeric id as a name is worse than a clean failure message; the caller
/// surfaces the error and nothing partial lands in the repository.</para>
/// <para>
/// <b>Temp file cleanup.</b> The downloaded archive lives in a temp file that is
/// deleted once <see cref="IModImportService.Import"/> returns (the import
/// extracts/copies the content into the repository, so the source archive is no
/// longer needed). On any failure the temp file is also deleted, so no partial
/// state is left on disk.</para>
/// </remarks>
public interface IModAcquisitionService
{
    /// <summary>
    /// Downloads a Nexus mod file, extracts it into the repository via
    /// <see cref="IModImportService.Import"/>, and returns the
    /// <c>(containerId, versionId)</c> the caller feeds to
    /// <c>IProfileService.AddMod</c>.
    /// </summary>
    /// <param name="gameDomain">The Nexus game domain (the host of the
    /// <c>nxm://</c> URL, e.g. <c>warhammer40kdarktide</c>).</param>
    /// <param name="modId">The Nexus mod id.</param>
    /// <param name="fileId">The Nexus file id (the specific release to
    /// download).</param>
    /// <param name="nxmKey">The per-file download key from the <c>nxm://</c> URL,
    /// or <c>null</c> when absent. When both <paramref name="nxmKey"/> and
    /// <paramref name="nxmExpires"/> are non-null, the free-user download-link
    /// endpoint is used (the key + expiry are per-file tokens for free users);
    /// otherwise the premium (auth-only) endpoint is used. The key is NOT a
    /// substitute for auth: the caller gates on
    /// <c>NexusConfig.AuthMethod != None</c> first.</param>
    /// <param name="nxmExpires">The per-file download expiry (epoch seconds) from
    /// the <c>nxm://</c> URL, or <c>null</c> when absent.</param>
    /// <param name="progress">Optional cumulative-bytes progress receiver (Stage 5
    /// wires this to a per-row indicator). <c>null</c> in Stage 3 (the nxm handler
    /// has no progress UI).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <c>(containerId, versionId)</c> of the imported mod. The
    /// import service resolves (or creates) the container keyed by
    /// <see cref="NexusSource.ModId"/> and adds the version keyed by the file's
    /// version string.</returns>
    /// <exception cref="NexusApiException">The Nexus API returned a non-success
    /// response (download links, mod info, or mod files). Thrown by
    /// <see cref="INexusClient"/>.</exception>
    /// <exception cref="InvalidOperationException">The mod metadata was unusable:
    /// the mod name was empty, or the requested <paramref name="fileId"/> was not
    /// listed among the mod's files. No degraded fallback.</exception>
    /// <exception cref="System.IO.IOException">The archive download or the temp
    /// file could not be completed.</exception>
    /// <exception cref="System.IO.InvalidDataException">The downloaded archive is
    /// malformed (propagated from <see cref="IModImportService.Import"/>).</exception>
    Task<(Guid ContainerId, string VersionId)> AcquireFromNexusAsync(
        string gameDomain,
        int modId,
        int fileId,
        string? nxmKey = null,
        long? nxmExpires = null,
        IProgress<long>? progress = null,
        CancellationToken ct = default);
}
