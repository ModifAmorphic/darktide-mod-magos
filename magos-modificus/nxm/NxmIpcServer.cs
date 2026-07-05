using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace Magos.Modificus.Nxm;

/// <summary>
/// The Magos-side named pipe server that receives framed <c>nxm://</c> URLs
/// from handler-exe invocations and dispatches them to the
/// <see cref="INxmRouter"/>. Cross-platform via <see cref="NamedPipeServerStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single-instance enforcement is the pipe bind.</b> <see cref="Bind"/>
/// creates the <see cref="NamedPipeServerStream"/> with
/// <c>maxNumberOfServerInstances = 1</c> and holds it for the app's lifetime;
/// if another Magos process already owns the same pipe name, the ctor throws
/// <see cref="IOException"/>, which <see cref="Bind"/> surfaces as
/// <see cref="NxmSingleInstanceException"/>. The composition root treats that as
/// "another Magos is primary" and exits cleanly before the main window shows.
/// One mechanism, two purposes: URL delivery and single-instance.</para>
/// <para>
/// <see cref="RunAsync"/> is the accept loop: <c>WaitForConnectionAsync</c>,
/// read one framed URL, route it, <see cref="NamedPipeServerStream.Disconnect"/>
/// (keeps the server instance alive for the next client, unlike dispose +
/// recreate which would have to rebind the pipe name), loop. Per-connection
/// exceptions are logged and swallowed so a single bad client cannot kill the
/// server. The loop processes one connection at a time (acceptable for v1;
/// handler invocations are rare and short). Cancellation shuts the server down.
/// </para>
/// </remarks>
public sealed class NxmIpcServer : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The fixed, cross-user pipe name. No per-user suffix: this is a
    /// single-user gaming-app context. Concurrent OS users colliding on the
    /// pipe is out of scope for v1.
    /// </summary>
    public const string DefaultPipeName = "Magos.Nxm";

    private readonly INxmRouter _router;
    private readonly ILogger<NxmIpcServer> _logger;
    private readonly string _pipeName;

    // The single server stream, created by Bind() and held for the app's
    // lifetime. The accept loop Disconnect()s between clients to accept the next
    // one on the SAME stream (no rebind), which keeps the single-instance claim
    // continuous and avoids any socket-file cleanup race.
    private NamedPipeServerStream? _server;

    public NxmIpcServer(INxmRouter router, ILogger<NxmIpcServer> logger, string? pipeName = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pipeName = pipeName ?? DefaultPipeName;
    }

    /// <summary>
    /// Synchronously binds the named pipe. This is the single-instance claim:
    /// if another Magos process owns the pipe, throws
    /// <see cref="NxmSingleInstanceException"/>. Must be called once before
    /// <see cref="RunAsync"/>.
    /// </summary>
    public void Bind()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_server is not null)
            throw new InvalidOperationException("NxmIpcServer is already bound.");

        try
        {
            _server = CreateServerStream();
        }
        catch (IOException ex)
        {
            // The pipe name is already owned: another Magos is primary.
            throw new NxmSingleInstanceException(_pipeName, ex);
        }

        _logger.LogInformation("Bound nxm IPC pipe '{Pipe}' (single-instance claim).", _pipeName);
    }

    /// <summary>
    /// The accept loop. Assumes <see cref="Bind"/> succeeded. Runs until
    /// <paramref name="ct"/> cancels. Per-connection exceptions are logged and
    /// swallowed; the loop <see cref="NamedPipeServerStream.Disconnect"/>s between
    /// clients so the next <c>WaitForConnectionAsync</c> accepts on the same
    /// server instance.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_server is null)
            throw new InvalidOperationException("NxmIpcServer.Bind() must be called before RunAsync().");

        var server = _server;
        while (!ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ProcessConnectionAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException ex)
            {
                // Accept or transport-level failure on this connection. Log and
                // continue: one bad client must not kill the server.
                _logger.LogWarning(ex, "nxm IPC connection error on '{Pipe}'; continuing accept loop.", _pipeName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing an nxm IPC connection; continuing accept loop.");
            }
            finally
            {
                // Disconnect the current client so the next WaitForConnectionAsync
                // accepts a fresh one on the SAME server instance. IsConnected
                // guards the case where WaitForConnection itself threw before a
                // client was accepted. Disconnect (not Dispose) keeps the pipe
                // name claimed for the app's lifetime.
                try
                {
                    if (server.IsConnected)
                        server.Disconnect();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to disconnect the nxm IPC client (best-effort).");
                }
            }
        }
    }

    private async Task ProcessConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        string? url;
        try
        {
            url = await NxmIpcFraming.ReadUrlAsync(server, ct).ConfigureAwait(false);
        }
        catch (NxmIpcFramingException ex)
        {
            _logger.LogWarning(ex, "Malformed nxm IPC frame; ignoring connection.");
            return;
        }

        if (url is null)
        {
            // Clean close, no message. Nothing to do.
            return;
        }

        _logger.LogDebug("Received nxm URL via IPC: {Url}", url);
        await _router.RouteAsync(url, ct).ConfigureAwait(false);
    }

    private NamedPipeServerStream CreateServerStream()
        => new(_pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
               PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private bool _disposed;

    /// <summary>
    /// Disposes the server stream and marks the server disposed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _server?.Dispose();
        _server = null;
    }

    /// <summary>
    /// Asynchronously disposes the server stream and marks the server disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }
}

/// <summary>
/// Raised by <see cref="NxmIpcServer.Bind"/> when the named pipe is already
/// owned by another Magos process. The composition root catches this and exits
/// cleanly before showing the main window (single-instance enforcement).
/// </summary>
public sealed class NxmSingleInstanceException : Exception
{
    /// <summary>The pipe name that was contested.</summary>
    public string PipeName { get; }

    public NxmSingleInstanceException(string pipeName, Exception inner)
        : base($"Another Magos instance owns the nxm IPC pipe '{pipeName}'.", inner)
        => PipeName = pipeName;
}
