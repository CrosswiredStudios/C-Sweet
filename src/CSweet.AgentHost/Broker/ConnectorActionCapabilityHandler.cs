using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class ConnectorActionCapabilityHandler(ConnectorActionService actions) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public bool CanHandle(string capability) => capability is PlatformCapabilities.ConnectorActionRequest or PlatformCapabilities.ConnectorActionRead or PlatformCapabilities.ConnectorActionCancel;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        CapabilityResult response;
        try
        {
            if (!Guid.TryParse(session.BusinessId, out var organizationId) || !Guid.TryParse(session.InstallationId, out var requesterId) ||
                request.Payload.Span.Length > 64 * 1024 || !CanHandle(request.Capability))
                throw new UnauthorizedAccessException();
            using var payload = JsonDocument.Parse(request.Payload.ToByteArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (payload.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            _ = ConnectorRequestMaterializer.Hash(payload.RootElement); // Reject ambiguous duplicate properties.
            var action = request.Capability switch
            {
                PlatformCapabilities.ConnectorActionRequest => await actions.RequestAsync(organizationId, requesterId, payload.RootElement.Deserialize<RequestConnectorAction>(Json)!, ct),
                PlatformCapabilities.ConnectorActionRead => await actions.ReadAsync(organizationId, requesterId, payload.RootElement.Deserialize<ReadConnectorAction>(Json)!, ct),
                _ => await actions.CancelAsync(organizationId, requesterId, payload.RootElement.Deserialize<CancelConnectorAction>(Json)!, ct)
            };
            response = new() { RequestId = request.RequestId, Succeeded = true,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(action, Json)) };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or
            UnauthorizedAccessException or DbUpdateConcurrencyException)
        {
            response = new() { RequestId = request.RequestId, Succeeded = false, FailureCode = "connector.action.unavailable",
                Error = "The action is unavailable or changed. Check its current status, permissions and account before continuing.",
                Payload = JsonPayload.FromUtf8("{\"isError\":true}") };
        }
        yield return response;
    }
}
