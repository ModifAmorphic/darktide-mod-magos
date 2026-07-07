using System.Buffers.Binary;
using System.Text;

namespace Modificus.Curator.Nxm;

/// <summary>
/// Length-prefixed UTF-8 framing for the one-message-per-connection nxm IPC
/// protocol. Frame layout:
/// <code>
/// [4 bytes: little-endian uint32 payload length N][N bytes: UTF-8 URL string]
/// </code>
/// Payloads are capped at <see cref="MaxPayloadBytes"/> (8 KiB); nxm URLs are
/// short, and the cap is a defense against a misbehaving or hostile client
/// asking the server to buffer unbounded data.
/// </summary>
/// <remarks>
/// AOT-safe: only raw byte and UTF-8 IO. No reflection, no JSON. The handler
/// exe (native AOT) writes frames via <see cref="WriteUrlAsync"/>; the Curator
/// IPC server reads them via <see cref="ReadUrlAsync"/>.
/// </remarks>
public static class NxmIpcFraming
{
    /// <summary>The maximum payload length the framing accepts (8 KiB).</summary>
    public const int MaxPayloadBytes = 8 * 1024;

    private const int LengthPrefixBytes = 4;

    /// <summary>
    /// Writes <paramref name="url"/> as a single framed message to
    /// <paramref name="stream"/> and flushes. Throws
    /// <see cref="NxmIpcFramingException"/> if the UTF-8 encoding of the URL
    /// exceeds <see cref="MaxPayloadBytes"/>.
    /// </summary>
    public static async Task WriteUrlAsync(Stream stream, string url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(url);

        byte[] payload;
        try
        {
            payload = Encoding.UTF8.GetBytes(url);
        }
        catch (Exception ex) when (ex is ArgumentException or EncoderFallbackException)
        {
            throw new NxmIpcFramingException("Failed to UTF-8 encode the URL payload.", ex);
        }

        if (payload.Length > MaxPayloadBytes)
            throw new NxmIpcFramingException(
                $"URL payload ({payload.Length} bytes) exceeds the {MaxPayloadBytes}-byte maximum.");

        var prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);

        await stream.WriteAsync(prefix.AsMemory(0, LengthPrefixBytes), ct).ConfigureAwait(false);
        await stream.WriteAsync(payload.AsMemory(0, payload.Length), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a single framed message from <paramref name="stream"/>. Returns
    /// the decoded URL string, or <c>null</c> if the peer closed the connection
    /// cleanly before sending any bytes (no message). Throws
    /// <see cref="NxmIpcFramingException"/> on a malformed length prefix or a
    /// connection that drops mid-frame.
    /// </summary>
    public static async Task<string?> ReadUrlAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[LengthPrefixBytes];
        if (!await ReadExactAsync(stream, prefix, ct).ConfigureAwait(false))
            return null; // clean close at a frame boundary, no message.

        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length == 0 || length > MaxPayloadBytes)
            throw new NxmIpcFramingException(
                $"Invalid payload length prefix {length} (max {MaxPayloadBytes}).");

        var payload = new byte[length];
        // Throws NxmIpcFramingException on a mid-frame close.
        await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);

        try
        {
            return Encoding.UTF8.GetString(payload);
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
        {
            throw new NxmIpcFramingException("Failed to UTF-8 decode the payload.", ex);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes. Returns
    /// <c>false</c> for a clean close before any byte was read; throws
    /// <see cref="NxmIpcFramingException"/> if the close arrives mid-read
    /// (partial frame).
    /// </summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Wrap transport errors so the server's per-connection catch
                // logs a single, descriptive message.
                throw new NxmIpcFramingException("Read failed during a framed message.", ex);
            }

            if (read == 0)
            {
                if (total == 0)
                    return false; // clean close at the boundary.
                throw new NxmIpcFramingException("Connection closed mid-frame.");
            }

            total += read;
        }

        return true;
    }
}

/// <summary>
/// Raised by <see cref="NxmIpcFraming"/> for a malformed frame (bad length
/// prefix, mid-frame close, encoding failure, transport error). The IPC server
/// catches these per-connection, logs, and continues accepting.
/// </summary>
public sealed class NxmIpcFramingException : Exception
{
    public NxmIpcFramingException(string message) : base(message) { }
    public NxmIpcFramingException(string message, Exception inner) : base(message, inner) { }
}
