using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class ChiefLeadershipCapabilityHandler(IChiefLeadershipService leadership) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanHandle(string capability) => ChiefLeadershipCapabilities.All.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken token)
    {
        CapabilityResult result;
        try
        {
            if (!session.Grant.RequestedCapabilities.Contains(request.Capability) ||
                !Guid.TryParse(session.BusinessId, out var organization) || organization == Guid.Empty ||
                !Guid.TryParse(session.InstallationId, out var installation) || installation == Guid.Empty)
                throw new UnauthorizedAccessException("The leadership capability or installation identity is not authorized.");
            object response = request.Capability switch
            {
                ChiefLeadershipCapabilities.Read => await leadership.ReadAsync(organization, installation, Read<ReadChiefLeadershipRequest>(request), token),
                ChiefLeadershipCapabilities.Present => await leadership.PresentAsync(organization, installation, Read<PresentChiefFocusRequest>(request), token),
                ChiefLeadershipCapabilities.Capture => await leadership.CaptureAsync(organization, installation, Read<CaptureChiefFocusRequest>(request), token),
                ChiefLeadershipCapabilities.Assess => await leadership.AssessAsync(organization, installation, Read<RecordChiefAssessmentRequest>(request), token),
                ChiefLeadershipCapabilities.Control => await leadership.ControlAsync(organization, installation, Read<ControlChiefLeadershipRequest>(request), token),
                _ => throw new ArgumentException("Unsupported leadership capability.")
            };
            result = new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json",
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(response, Json)) };
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or UnauthorizedAccessException or InvalidOperationException or DbUpdateConcurrencyException)
        {
            var code = exception is UnauthorizedAccessException ? PlatformCapabilityErrorCode.Denied :
                exception is DbUpdateConcurrencyException ? PlatformCapabilityErrorCode.Conflict : PlatformCapabilityErrorCode.ValidationFailed;
            result = new() { RequestId = request.RequestId, Succeeded = false, ContentType = "application/json", Error = exception.Message,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, exception.Message), Json)) };
        }
        yield return result;
    }

    private static T Read<T>(RequestCapability request) => JsonSerializer.Deserialize<T>(request.Payload.Span, Json)
        ?? throw new ArgumentException("A typed leadership request is required.");
}
