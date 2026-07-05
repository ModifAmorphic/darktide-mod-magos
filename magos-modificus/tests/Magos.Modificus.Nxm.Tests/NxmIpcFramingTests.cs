using System.IO.Pipes;
using System.Text;

namespace Magos.Modificus.Nxm.Tests;

/// <summary>
/// <see cref="NxmIpcFraming"/>: write/read round-trip against a real named-pipe
/// pair, the 8 KiB max-payload cap on both write + read, mid-frame close
/// detection, and the clean-close-at-boundary "null" signal.
/// </summary>
public sealed class NxmIpcFramingTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WriteUrl_ReadUrl_round_trips_a_url()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var pipeName = UniquePipeName();
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
            maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var url = "nxm://warhammer40kdarktide/mods/8/files/5820?key=K&expires=1&user_id=2";

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            return await NxmIpcFraming.ReadUrlAsync(server, cts.Token);
        });

        await client.ConnectAsync(cts.Token);
        await NxmIpcFraming.WriteUrlAsync(client, url, cts.Token);

        var read = await serverTask;
        Assert.Equal(url, read);
    }

    [Fact]
    public async Task ReadUrl_returns_null_for_clean_close_before_any_data()
    {
        // An empty open stream reads 0 immediately (Position == Length), modeling
        // a peer that closed the connection cleanly before sending any bytes.
        // (Closing a MemoryStream would throw ObjectDisposedException instead,
        // which is not the real-pipe behavior we are testing.)
        using var stream = new MemoryStream();
        var result = await NxmIpcFraming.ReadUrlAsync(stream, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteUrl_rejects_over_length_payload()
    {
        // Build a URL whose UTF-8 encoding exceeds the 8 KiB cap.
        var oversized = new string('a', NxmIpcFraming.MaxPayloadBytes + 1);
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<NxmIpcFramingException>(
            () => NxmIpcFraming.WriteUrlAsync(stream, oversized, CancellationToken.None));
    }

    [Fact]
    public async Task ReadUrl_rejects_over_length_prefix()
    {
        // Craft a length prefix that exceeds the cap, then read.
        var prefix = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            prefix, (uint)(NxmIpcFraming.MaxPayloadBytes + 1));
        using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<NxmIpcFramingException>(
            () => NxmIpcFraming.ReadUrlAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadUrl_throws_on_mid_frame_close()
    {
        // Length prefix says 10 bytes, but only 3 are present. The open stream
        // yields the 7 bytes then returns 0 (EOF), modeling a mid-frame close.
        var prefix = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(prefix, 10);
        var frame = new byte[4 + 3];
        Buffer.BlockCopy(prefix, 0, frame, 0, 4);
        using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<NxmIpcFramingException>(
            () => NxmIpcFraming.ReadUrlAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadUrl_rejects_zero_length_prefix()
    {
        var frame = new byte[4]; // all zeros = length 0
        using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<NxmIpcFramingException>(
            () => NxmIpcFraming.ReadUrlAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task WriteUrl_writes_correct_length_prefixed_bytes()
    {
        var url = "nxm://test";
        using var stream = new MemoryStream();
        await NxmIpcFraming.WriteUrlAsync(stream, url, CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal(4 + Encoding.UTF8.GetByteCount(url), bytes.Length);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(url), length);
    }

    private static string UniquePipeName() => "magos-nxm-test-" + Guid.NewGuid().ToString("N");
}
