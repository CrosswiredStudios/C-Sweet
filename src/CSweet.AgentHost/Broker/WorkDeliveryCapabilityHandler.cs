using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

public sealed class WorkDeliveryCapabilityHandler(IWorkDeliveryService service) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanHandle(string capability) => WorkDeliveryCapabilities.All.Contains(capability);
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }
    private async Task<CapabilityResult> HandleCoreAsync(AgentSession session, RequestCapability request, CancellationToken ct)
    {
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability) || !Guid.TryParse(session.BusinessId, out var org) ||
            !Guid.TryParse(session.InstallationId, out var actor))
            return Error(request, PlatformCapabilityErrorCode.Denied, "The installation has no delivery capability grant.");
        T Read<T>() => JsonSerializer.Deserialize<T>(request.Payload.Span, Json) ?? throw new JsonException("The delivery payload is required.");
        try
        {
            object result = request.Capability switch
            {
                WorkDeliveryCapabilities.Read => await service.ReadAsync(org, actor, Read<ReadWorkDeliveryPlansRequest>(), ct),
                WorkDeliveryCapabilities.Configure => await service.ConfigureAsync(org, actor, Read<ConfigureWorkDeliveryPlanRequest>(), ct),
                WorkDeliveryCapabilities.Control => await service.ControlAsync(org, actor, Read<ControlWorkDeliveryPlanRequest>(), ct),
                WorkDeliveryCapabilities.Accept => await service.AcceptAsync(org, actor, Read<DecideWorkDeliveryAcceptanceRequest>(), ct),
                WorkDeliveryCapabilities.Recover => await service.RecoverAsync(org, actor, Read<RecoverWorkDeliveryRequest>(), ct),
                WorkDeliveryCapabilities.Review => await service.CompleteReviewAsync(org, actor, Read<CompleteWorkDeliveryReviewRequest>(), ct),
                WorkDeliveryCapabilities.Evidence => await service.ReadEvidenceAsync(org, actor, Read<ReadWorkDeliveryEvidenceRequest>(), ct),
                _ => throw new ArgumentException("Unknown delivery capability.")
            };
            return new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json",
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(result, Json)) };
        }
        catch (UnauthorizedAccessException error) { return Error(request, PlatformCapabilityErrorCode.Denied, error.Message); }
        catch (KeyNotFoundException error) { return Error(request, PlatformCapabilityErrorCode.NotFound, error.Message); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        { return Error(request, PlatformCapabilityErrorCode.Conflict, error.Message); }
    }
    private static CapabilityResult Error(RequestCapability request, PlatformCapabilityErrorCode code, string message) => new()
    { RequestId = request.RequestId, Succeeded = false, ContentType = "application/json", Error = message,
      Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, message), Json)) };
}
