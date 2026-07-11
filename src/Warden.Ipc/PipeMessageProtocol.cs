using System.Buffers.Binary;
using System.Text.Json;

namespace Warden.Ipc;

/// <summary>
/// Length-prefixed JSON framing over any stream. Simple on purpose -- a custom binary
/// protocol isn't worth it for a channel that carries a handful of small, infrequent
/// messages.
/// </summary>
public static class PipeMessageProtocol
{
    public static async Task WriteAsync(Stream stream, PipeMessage message, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);

        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, body.Length);

        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one message, or <c>null</c> if the peer disconnected before sending one.
    /// </summary>
    public static async Task<PipeMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var lengthPrefix = new byte[4];
        if (!await ReadExactAsync(stream, lengthPrefix, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return JsonSerializer.Deserialize<PipeMessage>(body);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
