using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Nxm;

/// <summary>
/// The Stage 1 no-op <see cref="INxmModDownloadHandler"/>. Logs the parsed URL
/// at Information and returns. Replaced by the Stage 3 acquisition flow (a real
/// handler registered AFTER <c>AddNxm()</c> via <c>AddSingleton</c>, last
/// registration wins).
/// </summary>
internal sealed class NoOpNxmModDownloadHandler : INxmModDownloadHandler
{
    private readonly ILogger<NoOpNxmModDownloadHandler> _logger;

    public NoOpNxmModDownloadHandler(ILogger<NoOpNxmModDownloadHandler> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task HandleAsync(NxmModDownloadUrl url, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Received nxm mod-download: game={Game} mod={Mod} file={File} key={Key} expires={Expires} user={User}",
            url.Game, url.ModId, url.FileId,
            url.Key is null ? "(none)" : url.Key,
            url.Expires is null ? "(none)" : url.Expires,
            url.UserId is null ? "(none)" : url.UserId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Stage 1 no-op <see cref="INxmOAuthCallbackHandler"/>. Logs the parsed
/// callback at Information and returns. Replaced by the Stage 2 OIDC browser
/// flow (a real handler registered AFTER <c>AddNxm()</c> via
/// <c>AddSingleton</c>, last registration wins).
/// </summary>
internal sealed class NoOpNxmOAuthCallbackHandler : INxmOAuthCallbackHandler
{
    private readonly ILogger<NoOpNxmOAuthCallbackHandler> _logger;

    public NoOpNxmOAuthCallbackHandler(ILogger<NoOpNxmOAuthCallbackHandler> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task HandleAsync(NxmOAuthCallbackUrl url, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Received nxm oauth-callback: code={Code} state={State}", url.Code, url.State);
        return Task.CompletedTask;
    }
}
