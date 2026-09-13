using System.Runtime.InteropServices;
using CSweet.Compute.Contracts;

namespace CSweet.Compute.Guest;

/// <summary>One request per parent-host connection. A lost execution response is an unknown outcome, never permission to replay.</summary>
public sealed class ComputeGuestConnection(ComputeGuestExecution execution)
{
    public async Task ServeAsync(Stream stream, CancellationToken token)
    {
        ComputeGuestWireRequest request;
        using (var read = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            read.CancelAfter(TimeSpan.FromSeconds(5));
            request = await ComputeGuestWire.ReadAsync<ComputeGuestWireRequest>(stream, ComputeGuestWire.MaximumRequestBytes, read.Token);
        }
        if (request.Version != 1 || request.Challenge == Guid.Empty) throw new InvalidDataException("Invalid guest request.");
        if (request.ForwardPort is { } port)
        {
            if (request.Command is not null || port is < 1024 or > 65535) throw new InvalidDataException("Invalid forward request.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, deadline.Token);
            await ReplyAsync(stream, new ComputeGuestForwardReady(1, request.Challenge, true), deadline.Token);
            await ComputeStreamRelay.RunAsync(stream, client.GetStream(), deadline.Token);
            return;
        }
        if (request.Command is null)
        {
            // Same response schema as ComputeGuestReadiness; old readiness probes remain compatible.
            await ReplyAsync(stream, new { version = 1, challenge = request.Challenge,
                operatingSystem = OperatingSystem.IsWindows() ? "windows" : "linux",
                architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), acceptingWork = execution.AcceptingWork }, token);
            return;
        }
        ComputeGuestWireResult response;
        try
        {
            var result = await execution.ExecuteAsync(request.Command, token);
            response = new(1, request.Challenge, result.RequestId, result.ExitCode, result.TimedOut,
                result.StandardOutput, result.StandardError, result.Truncated);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        {
            response = new(1, request.Challenge, request.Command.RequestId, null, false, [], [], false,
                error is ArgumentException ? "invalid-command" : error is InvalidOperationException ? "guest-unavailable" : "execution-failed");
        }
        await ReplyAsync(stream, response, token);
    }

    private static async Task ReplyAsync<T>(Stream stream, T value, CancellationToken token)
    {
        using var write = CancellationTokenSource.CreateLinkedTokenSource(token);
        write.CancelAfter(TimeSpan.FromSeconds(5));
        await ComputeGuestWire.WriteAsync(stream, value, ComputeGuestWire.MaximumResultBytes, write.Token);
    }
}
