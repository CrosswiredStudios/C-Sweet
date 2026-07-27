using System.Text.Json;
using CSweet.Application.GenAi;
using CSweet.Contracts.GenAi;

namespace CSweet.AgentHost.Broker;

public sealed class PlatformGenAiCapabilityHandler(IGenAiJobService jobs) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool CanHandle(string capability) =>
        GenAiCapabilities.Operations.Contains(capability) ||
        capability is GenAiCapabilities.JobRead or GenAiCapabilities.JobCancel;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
        {
            yield return Failure(request.RequestId, "The agent session is not associated with a valid organization and installation.");
            yield break;
        }

        yield return await HandleCoreAsync(session, request, organizationId, installationId, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(
        AgentSession session,
        RequestCapability request,
        Guid organizationId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (GenAiCapabilities.ToOperation(request.Capability) is { } operation)
            {
                var input = Read<GenAiMediaRequest>(request);
                return Success(request.RequestId,
                    await jobs.StartAsync(organizationId, installationId, operation, input, cancellationToken));
            }

            var lookup = Read<GenAiJobLookupRequest>(request);
            var result = request.Capability == GenAiCapabilities.JobCancel
                ? await jobs.CancelAsync(lookup.JobId, organizationId, installationId, cancellationToken)
                : await jobs.GetAsync(lookup.JobId, organizationId, installationId, cancellationToken);
            return result is null ? Failure(request.RequestId, "GenAI job was not found.") : Success(request.RequestId, result);
        }
        catch (JsonException)
        {
            return Failure(request.RequestId, "The GenAI request payload is not valid JSON.");
        }
        catch (InvalidOperationException exception)
        {
            return Failure(request.RequestId, exception.Message);
        }
    }

    private static T Read<T>(RequestCapability request) =>
        JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions) ??
        throw new JsonException("Payload was empty.");

    private static CapabilityResult Success<T>(string requestId, T value) => new()
    {
        RequestId = requestId, Succeeded = true, ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
    };

    private static CapabilityResult Failure(string requestId, string message) => new()
    {
        RequestId = requestId, Succeeded = false, ContentType = "application/json", Error = message,
        Payload = JsonPayload.FromUtf8("{\"isError\":true}")
    };
}
