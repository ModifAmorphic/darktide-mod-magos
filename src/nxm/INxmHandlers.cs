namespace Modificus.Curator.Nxm;

/// <summary>
/// Handles a routed <c>nxm://</c> mod-download URL (the result of clicking
/// "Mod manager download" on a Nexus Mods file page). A no-op default that logs
/// is registered by <c>AddNxm()</c>; a real implementation that downloads
/// via the Nexus client and imports into the unified repository is registered
/// afterward (last registration wins).
/// </summary>
/// <remarks>
/// <b>DI convention:</b> the default no-op is registered by
/// <c>AddNxm()</c>. A real handler is registered AFTER <c>AddNxm()</c> via
/// <c>AddSingleton&lt;INxmModDownloadHandler, ...&gt;()</c> (the last
/// registration wins in MS DI).
/// </remarks>
public interface INxmModDownloadHandler
{
    /// <summary>Handles a parsed mod-download URL.</summary>
    Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default);
}
