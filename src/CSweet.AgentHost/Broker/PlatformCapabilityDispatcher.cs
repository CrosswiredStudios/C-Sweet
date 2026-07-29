using CSweet.Agent.SDK;
using CSweet.Contracts.Agents;

namespace CSweet.AgentHost.Broker;

public interface IPlatformCapabilityDispatcher
{
    IAsyncEnumerable<CapabilityResult> InvokeAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken);
}

public sealed class PlatformCapabilityDispatcher(
    IEnumerable<IPlatformCapabilityHandler> handlers) : IPlatformCapabilityDispatcher
{
    private readonly IReadOnlyList<IPlatformCapabilityHandler> _handlers = handlers.ToList();

    public IAsyncEnumerable<CapabilityResult> InvokeAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken)
    {
        var requested = session.Grant.RequestedCapabilities ?? new HashSet<string>(StringComparer.Ordinal);
        if (!IsImplicitPlatformCapability(request.Capability) && !requested.Contains(request.Capability))
        {
            return Single(Failure(request.RequestId,
                $"Agent '{session.AgentId}' may not request '{request.Capability}'."));
        }

        var handler = _handlers.FirstOrDefault(candidate => candidate.CanHandle(request.Capability));
        return handler is null
            ? Single(Failure(request.RequestId, $"No platform handler provides '{request.Capability}'."))
            : handler.HandleAsync(session, request, cancellationToken);
    }

    private static bool IsImplicitPlatformCapability(string capability) =>
        capability is CSweet.Contracts.GenAi.GenAiCapabilities.JobRead or CSweet.Contracts.GenAi.GenAiCapabilities.JobCancel ||
        capability == AgentLifecycleCapabilities.CompleteOnboarding;

    private static async IAsyncEnumerable<CapabilityResult> Single(CapabilityResult result)
    {
        yield return result;
        await Task.CompletedTask;
    }

    private static CapabilityResult Failure(string requestId, string error) => new()
    {
        RequestId = requestId,
        Succeeded = false,
        ContentType = "application/json",
        Error = error,
        Payload = JsonPayload.FromUtf8("{\"isError\":true}")
    };
}
