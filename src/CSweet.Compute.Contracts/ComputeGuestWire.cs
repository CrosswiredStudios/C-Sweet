using System.Buffers.Binary;
using System.Text.Json;

namespace CSweet.Compute.Contracts;

public sealed record ComputeGuestWireRequest(int Version, Guid Challenge, ComputeGuestCommand? Command = null, int? ForwardPort = null);
public sealed record ComputeGuestForwardReady(int Version, Guid Challenge, bool Connected);
public sealed record ComputeGuestWireResult(int Version, Guid Challenge, Guid RequestId, int? ExitCode,
    bool TimedOut, byte[] StandardOutput, byte[] StandardError, bool Truncated, string? ErrorCode = null);

/// <summary>Framing only. The provider must bind transport to an authorized owned VM; never use an agent-selected endpoint.</summary>
public static class ComputeGuestWire
{
    public const int Port = 2763;
    public const int MaximumRequestBytes = 65536;
    public const int MaximumResultBytes = 2 * 1024 * 1024;
    public static async Task<T> ReadAsync<T>(Stream stream, int maximumBytes, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 2 || length > maximumBytes) throw new InvalidDataException("Guest frame exceeds its limit.");
        var payload = new byte[length]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload, ComputeProtocol.Json) ?? throw new InvalidDataException("Guest frame is missing.");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, int maximumBytes, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, ComputeProtocol.Json);
        if (payload.Length < 2 || payload.Length > maximumBytes) throw new InvalidDataException("Guest frame exceeds its limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
}
