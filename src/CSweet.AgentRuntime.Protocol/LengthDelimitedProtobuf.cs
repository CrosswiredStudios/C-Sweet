using System.Buffers.Binary;
using Google.Protobuf;

namespace CSweet.AgentRuntime.Protocol;

public static class LengthDelimitedProtobuf
{
    public const int DefaultMaximumFrameBytes = 1024 * 1024;
    public const int AbsoluteMaximumFrameBytes = 16 * 1024 * 1024;

    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        int maximumFrameBytes = DefaultMaximumFrameBytes,
        CancellationToken cancellationToken = default)
        where T : IMessage<T>
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        ValidateMaximum(maximumFrameBytes);

        var size = message.CalculateSize();
        if (size > maximumFrameBytes)
            throw new InvalidDataException($"The protobuf frame exceeds the {maximumFrameBytes}-byte limit.");

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(prefix, size);
        await stream.WriteAsync(prefix, cancellationToken);
        using var output = new CodedOutputStream(stream, leaveOpen: true);
        message.WriteTo(output);
        output.Flush();
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T?> ReadAsync<T>(
        Stream stream,
        MessageParser<T> parser,
        int maximumFrameBytes = DefaultMaximumFrameBytes,
        CancellationToken cancellationToken = default)
        where T : IMessage<T>
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(parser);
        ValidateMaximum(maximumFrameBytes);

        var prefix = new byte[sizeof(int)];
        var prefixRead = await ReadAtMostAsync(stream, prefix, cancellationToken);
        if (prefixRead == 0) return default;
        if (prefixRead != prefix.Length) throw new EndOfStreamException("The protobuf frame prefix was truncated.");

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length < 0 || length > maximumFrameBytes)
            throw new InvalidDataException($"The protobuf frame length {length} is outside the allowed range.");

        var payload = GC.AllocateUninitializedArray<byte>(length);
        var payloadRead = await ReadAtMostAsync(stream, payload, cancellationToken);
        if (payloadRead != length) throw new EndOfStreamException("The protobuf frame payload was truncated.");
        return parser.ParseFrom(payload);
    }

    private static async Task<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static void ValidateMaximum(int maximumFrameBytes)
    {
        if (maximumFrameBytes is < 1 or > AbsoluteMaximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
    }
}
