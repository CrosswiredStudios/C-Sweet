using CSweet.Agent.SDK;
using CSweet.Contracts.Agents;
using CSweet.Infrastructure.Setup;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CSweet.AgentHost.Broker;

public interface IPlatformCapabilityDispatcher
{
    IAsyncEnumerable<CapabilityResult> InvokeAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken);
}

public sealed class PlatformCapabilityDispatcher(
    IEnumerable<IPlatformCapabilityHandler> handlers,
    PluginSetupAssistancePolicy? setupPolicy = null) : IPlatformCapabilityDispatcher
{
    private readonly IReadOnlyList<IPlatformCapabilityHandler> _handlers = handlers.ToList();

    public async IAsyncEnumerable<CapabilityResult> InvokeAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? denial = null;
        if (setupPolicy is not null)
        {
            try
            {
                if (!Guid.TryParse(session.InstallationId, out var installationId))
                    throw new UnauthorizedAccessException("The installation identity is invalid.");
                using var document = JsonDocument.Parse(request.Payload.IsEmpty ? "{}" : System.Text.Encoding.UTF8.GetString(request.Payload.Span));
                await setupPolicy.ValidateCapabilityAsync(session.BusinessId, installationId,
                    request.Capability, document.RootElement, cancellationToken);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or JsonException or InvalidOperationException)
            { denial = "The installation may not invoke this capability in its current setup context."; }
        }
        if (denial is not null)
        {
            yield return Failure(request.RequestId, denial);
            yield break;
        }
        var requested = session.Grant.RequestedCapabilities ?? new HashSet<string>(StringComparer.Ordinal);
        if (!IsImplicitPlatformCapability(request.Capability) && !requested.Contains(request.Capability))
        {
            yield return Failure(request.RequestId,
                $"Agent '{session.AgentId}' may not request '{request.Capability}'.");
            yield break;
        }

        var handler = _handlers.FirstOrDefault(candidate => candidate.CanHandle(request.Capability));
        if (handler is null)
            yield return Failure(request.RequestId, $"No platform handler provides '{request.Capability}'.");
        else
            await foreach (var result in handler.HandleAsync(session, request, cancellationToken))
                yield return result;
    }

    private static bool IsImplicitPlatformCapability(string capability) =>
        capability is CSweet.Contracts.GenAi.GenAiCapabilities.JobRead or CSweet.Contracts.GenAi.GenAiCapabilities.JobCancel ||
        capability == AgentLifecycleCapabilities.CompleteOnboarding;

    private static CapabilityResult Failure(string requestId, string error) => new()
    {
        RequestId = requestId,
        Succeeded = false,
        ContentType = "application/json",
        Error = error,
        Payload = JsonPayload.FromUtf8("{\"isError\":true}")
    };
}
