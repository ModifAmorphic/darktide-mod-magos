using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Nxm;

/// <summary>
/// The default <see cref="INxmRouter"/>. Parses the raw URL via
/// <see cref="NxmUrlParser"/>, dispatches mod-download URLs to the resolved
/// <see cref="INxmModDownloadHandler"/>, logs collection URLs as "unsupported
/// in v1", logs OAuth-callback URLs as "handled by the loopback listener, not
/// the nxm handler" (Magos OAuth uses RFC 8252 loopback redirect, independent of
/// the <c>nxm://</c> handler), and logs unparseable URLs as a warning. Handler
/// exceptions are caught at this boundary so one bad handler invocation cannot
/// kill the IPC accept loop.
/// </summary>
/// <remarks>
/// The mod-download handler is injected (not resolved from the provider per-call)
/// because it is a singleton; the router captures the last-registered handler at
/// construction time, which is exactly the override semantic Stage 3 relies on.
/// </remarks>
internal sealed class NxmRouter : INxmRouter
{
    private readonly INxmModDownloadHandler _modDownload;
    private readonly ILogger<NxmRouter> _logger;

    public NxmRouter(
        INxmModDownloadHandler modDownload,
        ILogger<NxmRouter> logger)
    {
        _modDownload = modDownload ?? throw new ArgumentNullException(nameof(modDownload));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task RouteAsync(string rawUrl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawUrl);

        if (!NxmUrlParser.TryParse(rawUrl, out var url))
        {
            _logger.LogWarning("Unparseable nxm URL ignored: {Url}", rawUrl);
            return;
        }

        try
        {
            switch (url)
            {
                case NxmModDownloadUrl mod:
                    await _modDownload.HandleAsync(mod, ct).ConfigureAwait(false);
                    break;
                case NxmOAuthCallbackUrl:
                    // Nexus OAuth in Magos uses a loopback HTTP redirect (RFC 8252),
                    // independent of the nxm handler. The parser still recognizes
                    // the shape (so it parses cleanly rather than classifying as
                    // unknown); the router just drops it. In normal operation no
                    // such URL is delivered over IPC because the loopback listener
                    // receives the callback, not the nxm handler.
                    _logger.LogInformation(
                        "OAuth callbacks are handled by the loopback listener, not the nxm handler; dropping {Url}.",
                        rawUrl);
                    break;
                case NxmCollectionUrl:
                    _logger.LogInformation("Collection URLs are not supported in v1: {Url}", rawUrl);
                    break;
                default:
                    _logger.LogWarning("Unrecognized nxm URL type ({Type}): {Url}", url.GetType().Name, rawUrl);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Boundary catch: a single handler failure must not take down the
            // IPC server. The accept loop's per-connection catch is the second
            // line of defense; this is the first, with a more specific message.
            _logger.LogError(ex, "nxm handler invocation failed for {Url}", rawUrl);
        }
    }
}
