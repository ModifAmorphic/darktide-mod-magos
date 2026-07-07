using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Nxm;

/// <summary>
/// The no-op <see cref="INxmModDownloadHandler"/>. Logs the parsed URL
/// at Information and returns. Replaced by the real acquisition flow (a real
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
