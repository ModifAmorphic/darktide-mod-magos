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

/// <summary>
/// Handles a routed <c>nxm://oauth/callback</c> URL. Stage 1 ships a no-op
/// default that logs; Stage 2's OIDC browser flow registers a real
/// implementation that awaits the routed callback, matched by <c>state</c>,
/// for its pending authorize request.
/// </summary>
/// <remarks>
/// <b>DI convention:</b> the default no-op is registered by
/// <c>AddNxm()</c>. A real handler is registered AFTER <c>AddNxm()</c> via
/// <c>AddSingleton&lt;INxmOAuthCallbackHandler, ...&gt;()</c> (the last
/// registration wins in MS DI). Stage 2 does exactly that.
/// </remarks>
public interface INxmOAuthCallbackHandler
{
    /// <summary>Handles a parsed OAuth-callback URL.</summary>
    Task HandleAsync(NxmOAuthCallbackUrl url, CancellationToken ct = default);
}
