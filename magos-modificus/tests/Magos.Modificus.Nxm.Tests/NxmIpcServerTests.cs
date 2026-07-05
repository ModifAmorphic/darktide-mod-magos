using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.Nxm.Tests;

/// <summary>
/// <see cref="NxmIpcServer"/>: the accept loop routes a client-delivered URL to
/// the router; per-connection failures (garbage frame, a throwing router) do
/// not kill the server (the next connection still works); and the pipe bind is
/// the single-instance claim (a second Bind on the same pipe name throws
/// <see cref="NxmSingleInstanceException"/>).
/// </summary>
public sealed class NxmIpcServerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Client_message_is_routed_to_the_router()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var pipeName = UniquePipeName();
        var router = new FakeRouter();
        using var server = new NxmIpcServer(router, NullLogger<NxmIpcServer>.Instance, pipeName);

        server.Bind();
        var loopTask = Task.Run(() => server.RunAsync(cts.Token));

        var url = "nxm://warhammer40kdarktide/mods/8/files/5820?key=K";
        await SendOneAsync(pipeName, url, cts.Token);

        await router.WaitForCountAsync(1, cts.Token);
        Assert.Equal(url, router.Routes[0]);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loopTask);
    }

    [Fact]
    public async Task Garbage_frame_does_not_kill_the_server()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var pipeName = UniquePipeName();
        var router = new FakeRouter();
        using var server = new NxmIpcServer(router, NullLogger<NxmIpcServer>.Instance, pipeName);

        server.Bind();
        var loopTask = Task.Run(() => server.RunAsync(cts.Token));

        // Send a complete-but-invalid frame: a length prefix exceeding the cap.
        // The server reads the 4 bytes, rejects the over-length prefix, and must
        // keep accepting.
        var overLengthPrefix = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            overLengthPrefix, (uint)(NxmIpcFraming.MaxPayloadBytes + 1));
        await SendRawThenCloseAsync(pipeName, overLengthPrefix, cts.Token);

        // A second, well-formed connection must still be routed.
        var url = "nxm://oauth/callback?code=ABC&state=DEF";
        await SendOneAsync(pipeName, url, cts.Token);
        await router.WaitForCountAsync(1, cts.Token);
        Assert.Equal(url, router.Routes[0]);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loopTask);
    }

    [Fact]
    public async Task Throwing_router_does_not_kill_the_server()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var pipeName = UniquePipeName();
        var router = new FakeRouter(throwOnFirst: true);
        using var server = new NxmIpcServer(router, NullLogger<NxmIpcServer>.Instance, pipeName);

        server.Bind();
        var loopTask = Task.Run(() => server.RunAsync(cts.Token));

        // First delivery: router throws. The server must catch (at the router
        // boundary) and keep accepting.
        await SendOneAsync(pipeName, "nxm://warhammer40kdarktide/mods/1/files/1", cts.Token);
        await router.WaitForCountAsync(1, cts.Token);

        // Second delivery: must succeed (the router only throws once).
        var url = "nxm://oauth/callback?code=X&state=Y";
        await SendOneAsync(pipeName, url, cts.Token);
        await router.WaitForCountAsync(2, cts.Token);
        Assert.Equal(url, router.Routes[1]);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loopTask);
    }

    [Fact]
    public async Task Second_bind_on_same_pipe_throws_single_instance()
    {
        var pipeName = UniquePipeName();
        using var server1 = new NxmIpcServer(new FakeRouter(), NullLogger<NxmIpcServer>.Instance, pipeName);
        server1.Bind();

        // The first server holds the pipe. A second Bind on the same name must
        // throw NxmSingleInstanceException.
        using var server2 = new NxmIpcServer(new FakeRouter(), NullLogger<NxmIpcServer>.Instance, pipeName);
        var ex = Assert.Throws<NxmSingleInstanceException>(server2.Bind);
        Assert.Equal(pipeName, ex.PipeName);
    }

    [Fact]
    public async Task RunAsync_without_bind_throws()
    {
        using var server = new NxmIpcServer(new FakeRouter(), NullLogger<NxmIpcServer>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.RunAsync(new CancellationTokenSource().Token));
    }

    // ---- helpers ---------------------------------------------------------

    private static async Task SendOneAsync(string pipeName, string url, CancellationToken ct)
    {
        // Retry-connect until the server's accept loop is ready: the server may
        // be between connections (recreating its stream after a prior delivery),
        // and ConnectAsync to a not-yet-bound name throws IOException on Linux
        // (ENOENT) rather than waiting. Both that and the per-attempt timeout
        // are retried until the caller's token cancels.
        NamedPipeClientStream? client = null;
        try
        {
            while (client is null)
            {
                ct.ThrowIfCancellationRequested();
                var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    link.CancelAfter(TimeSpan.FromMilliseconds(200));
                    await probe.ConnectAsync(link.Token);
                    client = probe;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await probe.DisposeAsync();
                    await Task.Delay(20, ct);
                }
                catch (IOException) when (!ct.IsCancellationRequested)
                {
                    await probe.DisposeAsync();
                    await Task.Delay(20, ct);
                }
            }

            await NxmIpcFraming.WriteUrlAsync(client, url, ct);
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync();
        }
    }

    private static async Task SendRawThenCloseAsync(string pipeName, byte[] bytes, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
                link.CancelAfter(TimeSpan.FromMilliseconds(200));
                await probe.ConnectAsync(link.Token);
                await probe.WriteAsync(bytes, ct);
                await probe.FlushAsync(ct);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await probe.DisposeAsync();
                await Task.Delay(20, ct);
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                await probe.DisposeAsync();
                await Task.Delay(20, ct);
            }
            finally
            {
                await probe.DisposeAsync();
            }
        }
    }

    private static string UniquePipeName() => "magos-nxm-srv-" + Guid.NewGuid().ToString("N");

    // ---- fakes -----------------------------------------------------------

    private sealed class FakeRouter : INxmRouter
    {
        private readonly bool _throwOnFirst;
        private readonly List<string> _routes = new();
        private readonly Lock _gate = new();
        private bool _hasThrown;

        public FakeRouter(bool throwOnFirst = false) => _throwOnFirst = throwOnFirst;

        public IReadOnlyList<string> Routes
        {
            get
            {
                lock (_gate) return _routes.ToArray();
            }
        }

        public Task RouteAsync(string rawUrl, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _routes.Add(rawUrl);
                if (_throwOnFirst && !_hasThrown)
                {
                    _hasThrown = true;
                    // The route is recorded; the server's boundary catch will
                    // swallow the throw and keep accepting.
                    throw new InvalidOperationException("test: router throws on first delivery");
                }
            }

            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int count, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate) if (_routes.Count >= count) return;
                await Task.Delay(15, ct);
            }
        }
    }
}
