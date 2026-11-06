using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Core;

namespace CSweet.AgentHost.Broker;

public sealed class ResourceFulfillmentCapabilityHandler(ResourceFulfillmentService resources) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanHandle(string capability) => capability is ResourceCapabilities.Request or ResourceCapabilities.Read or ResourceCapabilities.Decide;
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        CapabilityResult result;
        try
        {
            if (!CanHandle(request.Capability) || !session.Grant.RequestedCapabilities.Contains(request.Capability) ||
                !Guid.TryParse(session.BusinessId, out var organization) || !Guid.TryParse(session.InstallationId, out var installation))
                throw new UnauthorizedAccessException("The resource capability is not granted.");
            object value = request.Capability switch
            {
                ResourceCapabilities.Request => await resources.RequestAsync(organization, installation,
                    request.Payload.ToElement().Deserialize<ResourceRequest>(Json)!, token),
                ResourceCapabilities.Read => await resources.ReadAsync(organization, installation,
                    request.Payload.ToElement().Deserialize<ResourceRead>(Json)!, token),
                _ => await resources.DecideAsync(await resources.ActorAsync(organization, installation, token),
                    request.Payload.ToElement().Deserialize<ResourceDecision>(Json)!, token)
            };
            result = new() { RequestId = request.RequestId, Succeeded = true, Payload = JsonPayload.From(value, Json) };
        }
        catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
        {
            result = new() { RequestId = request.RequestId, Succeeded = false,
                FailureCode = error is UnauthorizedAccessException ? "denied" : "validation_failed", Error = error.Message, Retryable = false };
        }
        yield return result;
    }
}
