using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magos.Modificus.Nxm.Tests;

/// <summary>
/// <see cref="NxmIpcServer"/>: the accept loop routes a client-delivered URL to
/// the router; per-connection failures (garbage frame, a throwing router) do
/// not kill the server (the next connection still works); single-instance
/// enforcement is process enumeration before any pipe work (another Magos pid ->
/// <see cref="NxmSingleInstanceException"/> before the pipe ctor runs; alone ->
/// bind proceeds and a URL round-trips); and a pipe-bind <see cref="IOException"/>
/// degrades gracefully (no throw, a warning is logged, the accept loop is not
/// started).
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
        using var server = new NxmIpcServer(
            router, NullLogger<NxmIpcServer>.Instance, pipeName, AloneGuard());

        server.Bind();
        var loopTask = Task.Run(() => server.RunAsync(cts.Token));

        var url = "nxm://warhammer40kdarktide/mods/8/files/5820?key=K";
        await SendOneAsync(pipeName, url, cts.Token);

        await router.WaitForCountAsync(1, cts.Token);
        Assert.Equal(url, router.Routes[0]);

        await CancelAndWaitAsync(loopTask, cts);
    }

    [Fact]
    public async Task Garbage_frame_does_not_kill_the_server()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var pipeName = UniquePipeName();
        var router = new FakeRouter();
        using var server = new NxmIpcServer(
            router, NullLogger<NxmIpcServer>.Instance, pipeName, AloneGuard());

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

        await CancelAndWaitAsync(loopTask, cts);
    }

    [Fact]
    public async Task Throwing_router_does_not_kill_the_server()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var pipeName = UniquePipeName();
        var router = new FakeRouter(throwOnFirst: true);
        using var server = new NxmIpcServer(
            router, NullLogger<NxmIpcServer>.Instance, pipeName, AloneGuard());

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

        await CancelAndWaitAsync(loopTask, cts);
    }

    [Fact]
    public void Single_instance_check_with_another_process_throws_before_pipe_binding()
    {
        var pipeName = UniquePipeName();
        // Fake enumerator reports another live Magos pid exists.
        var guard = new SingleInstanceGuard((_, _) => new[] { 99999 });

        // A factory that flips a flag if the pipe ctor is ever reached. The
        // single-instance check must throw BEFORE this runs.
        var pipeFactoryCalled = false;
        var factory = new Func<string, NamedPipeServerStream>(_ =>
        {
            pipeFactoryCalled = true;
            throw new InvalidOperationException("pipe factory must not be called");
        });

        using var server = new NxmIpcServer(
            new FakeRouter(), NullLogger<NxmIpcServer>.Instance, pipeName, guard, factory);

        var ex = Assert.Throws<NxmSingleInstanceException>(server.Bind);
        Assert.Equal(pipeName, ex.PipeName);
        Assert.False(server.IsBound);
        Assert.False(pipeFactoryCalled, "Pipe ctor must not run when the single-instance check fails.");
    }

    [Fact]
    public async Task Single_instance_check_alone_proceeds_and_round_trips()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var pipeName = UniquePipeName();
        var router = new FakeRouter();
        // Fake enumerator reports no other process: bind must proceed.
        var guard = new SingleInstanceGuard((_, _) => Array.Empty<int>());
        using var server = new NxmIpcServer(
            router, NullLogger<NxmIpcServer>.Instance, pipeName, guard);

        Assert.False(server.IsBound);
        server.Bind();
        Assert.True(server.IsBound);

        var loopTask = Task.Run(() => server.RunAsync(cts.Token));

        var url = "nxm://warhammer40kdarktide/mods/8/files/5820?key=K";
        await SendOneAsync(pipeName, url, cts.Token);
        await router.WaitForCountAsync(1, cts.Token);
        Assert.Equal(url, router.Routes[0]);

        await CancelAndWaitAsync(loopTask, cts);
    }

    [Fact]
    public async Task Pipe_bind_failure_degrades_without_throwing()
    {
        var pipeName = UniquePipeName();
        var guard = new SingleInstanceGuard((_, _) => Array.Empty<int>());
        var logger = new CapturingLogger<NxmIpcServer>();
        // Factory simulates a pipe bind failure (leftover socket, permissions, etc.).
        var failingFactory = new Func<string, NamedPipeServerStream>(
            _ => throw new IOException("simulated pipe bind failure"));
        using var server = new NxmIpcServer(
            new FakeRouter(), logger, pipeName, guard, failingFactory);

        // Single-instance check passes (no other instance); pipe ctor throws
        // IOException -> degraded (no throw, not bound, warning logged).
        server.Bind();
        Assert.False(server.IsBound);
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("Failed to bind the nxm IPC pipe"));

        // The accept loop must not be startable on a degraded server. The
        // composition root checks IsBound first; a caller that forgets gets a
        // clear InvalidOperationException rather than a silent no-op.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.RunAsync(new CancellationTokenSource().Token));
    }

    [Fact]
    public async Task RunAsync_without_bind_throws()
    {
        using var server = new NxmIpcServer(new FakeRouter(), NullLogger<NxmIpcServer>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.RunAsync(new CancellationTokenSource().Token));
    }

    // ---- helpers ---------------------------------------------------------

    private static async Task CancelAndWaitAsync(Task loopTask, CancellationTokenSource cts)
    {
        // RunAsync exits via OperationCanceledException when cancellation lands
        // inside WaitForConnectionAsync, or via a normal return when the
        // while-check observes cancellation first. Either is a valid stop; the
        // accept-loop tests tolerate both (a real hang still fails via
        // TestTimeout). The strict Assert.ThrowsAnyAsync<OCE> flakes here because
        // the delivery-gate (WaitForCountAsync) returns before the server has
        // parked back in WaitForConnectionAsync, leaving a window where the
        // while-check sees cancellation first.
        cts.Cancel();
        try
        {
            await loopTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static SingleInstanceGuard AloneGuard()
        // The default production enumerator reads the host process table, which
        // is non-deterministic under the test runner (multiple dotnet/testhost
        // processes). Positive-path tests inject an alone-reporting guard so
        // the single-instance check passes unconditionally.
        => new((_, _) => Array.Empty<int>());

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

    /// <summary>
    /// A minimal capturing logger for asserting which levels/messages were
    /// emitted (used by the degraded-bind test to verify the warning).
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
