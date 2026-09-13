using System.Buffers.Binary;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

public sealed record ComputeGuestReadinessRequest(int Version, Guid Challenge);
public sealed record ComputeGuestReadinessResponse(int Version, Guid Challenge, string OperatingSystem,
    string Architecture, bool AcceptingWork);

/// <summary>Guest readiness is untrusted liveness, not certification or infrastructure authority.</summary>
public static class ComputeGuestReadiness
{
    public const int MaximumFrameBytes = 4096;

    /// <summary>The caller must bind the transport to the exact owned VM before invoking this probe.</summary>
    public static async Task<bool> ProbeAsync(Func<CancellationToken, Task<Stream>> connect,
        string operatingSystem, string architecture, CancellationToken token)
    {
        if (!ComputeSpecification.Identifier(operatingSystem) || !ComputeSpecification.Identifier(architecture))
            throw new ArgumentException("Approved operating system and architecture are required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            await using var stream = await connect(linked.Token);
            var request = new ComputeGuestReadinessRequest(1, Guid.NewGuid());
            await WriteAsync(stream, request, linked.Token);
            var response = await ReadAsync<ComputeGuestReadinessResponse>(stream, linked.Token);
            return response.Version == 1 && response.Challenge == request.Challenge &&
                response.OperatingSystem == operatingSystem && response.Architecture == architecture && response.AcceptingWork;
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is IOException or JsonException or TimeoutException or OperationCanceledException) { return false; }
    }

    /// <summary>Guest-side handler. Readiness is supplied by the installed work runtime, never a request field.</summary>
    public static async Task RespondAsync(Stream input, Stream output,
        Func<CancellationToken, Task<bool>> acceptingWork, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var request = await ReadAsync<ComputeGuestReadinessRequest>(input, linked.Token);
        if (request.Version != 1 || request.Challenge == Guid.Empty)
            throw new InvalidDataException("Guest readiness request is invalid.");
        var os = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" :
            throw new PlatformNotSupportedException("This guest readiness implementation supports Linux and Windows.");
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var ready = await acceptingWork(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        await WriteAsync(output, new ComputeGuestReadinessResponse(1, request.Challenge, os, architecture, ready), linked.Token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 2 or > MaximumFrameBytes) throw new InvalidDataException("Guest readiness frame exceeds its limit.");
        var payload = new byte[length]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload, ComputeProtocol.Json) ?? throw new InvalidDataException("Guest readiness frame is missing.");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, ComputeProtocol.Json);
        if (payload.Length is < 2 or > MaximumFrameBytes) throw new InvalidDataException("Guest readiness frame exceeds its limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
}
