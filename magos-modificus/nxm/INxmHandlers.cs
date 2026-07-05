namespace Magos.Modificus.Nxm;

/// <summary>
/// Handles a routed <c>nxm://</c> mod-download URL (the result of clicking
/// "Mod manager download" on a Nexus Mods file page). Stage 1 ships a no-op
/// default that logs; Stage 3 registers a real implementation that downloads
/// via the Nexus client and imports into the unified repository.
/// </summary>
/// <remarks>
/// <b>DI convention:</b> the default no-op is registered by
/// <c>AddNxm()</c>. A real handler is registered AFTER <c>AddNxm()</c> via
/// <c>AddSingleton&lt;INxmModDownloadHandler, ...&gt;()</c> (the last
/// registration wins in MS DI). Stage 3 does exactly that.
/// </remarks>
public interface INxmModDownloadHandler
{
    /// <summary>Handles a parsed mod-download URL.</summary>
    Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default);
}
