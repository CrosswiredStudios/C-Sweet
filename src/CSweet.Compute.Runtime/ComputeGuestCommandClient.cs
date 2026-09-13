using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

/// <summary>One command exchange over a provider-selected owned-VM connection. Never retries an uncertain execution.</summary>
public static class ComputeGuestCommandClient
{
    public static async Task<ComputeGuestWireResult> ExecuteAsync(Func<CancellationToken, Task<Stream>> connect,
        ComputeGuestCommand request, string operatingSystem, CancellationToken token)
    {
        var command = request.ValidateAndSnapshot(operatingSystem);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds + 15));
        var challenge = Guid.NewGuid();
        await using var stream = await connect(deadline.Token);
        await ComputeGuestWire.WriteAsync(stream, new ComputeGuestWireRequest(1, challenge, command),
            ComputeGuestWire.MaximumRequestBytes, deadline.Token);
        var result = await ComputeGuestWire.ReadAsync<ComputeGuestWireResult>(stream, ComputeGuestWire.MaximumResultBytes, deadline.Token);
        if (result.Version != 1 || result.Challenge != challenge || result.RequestId != command.RequestId ||
            result.StandardOutput is null || result.StandardError is null ||
            (long)result.StandardOutput.Length + result.StandardError.Length > command.MaximumOutputBytes ||
            result.ErrorCode is not (null or "invalid-command" or "guest-unavailable" or "execution-failed") ||
            (result.ErrorCode is not null && (result.ExitCode is not null || result.TimedOut || result.Truncated ||
                result.StandardOutput.Length != 0 || result.StandardError.Length != 0)) ||
            (result.ErrorCode is null && (result.TimedOut ? result.ExitCode is not null : result.ExitCode is null)))
            throw new InvalidDataException("Guest execution response is invalid; execution outcome is unknown.");
        return result;
    }
}
