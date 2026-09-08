using CSweet.Application.Setup;

namespace CSweet.AgentHost.Broker;

/// <summary>Rejects the retired caller-directed upload route, including direct broker invocations.</summary>
public sealed class PlatformMediaTransferCapabilityHandler : IPlatformCapabilityHandler
{
    public bool CanHandle(string capability) => capability == PluginPlatformCapabilities.MediaTransfer;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return new()
        {
            RequestId = request.RequestId, Succeeded = false, ContentType = "application/json",
            Error = "Caller-directed media transfer is unavailable. Use a declared connector operation and an exact approved action.",
            Payload = JsonPayload.FromUtf8("{\"isError\":true}")
        };
    }
}
