using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class AgentCoordinationCapabilityHandler(
    CSweetDbContext db,
    IAgentCoordinationService coordination) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool CanHandle(string capability) => capability is
        CommunicationCapabilities.CoordinationStart or
        CommunicationCapabilities.CoordinationStartWork or
        CommunicationCapabilities.CoordinationRespond or
        CommunicationCapabilities.CoordinationRead or
        CommunicationCapabilities.CoordinationList or
        CommunicationCapabilities.CoordinationResume or
        CommunicationCapabilities.CoordinationCancel;

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(
        AgentSession session, RequestCapability request, CancellationToken cancellationToken)
    {
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                $"The installation is not granted {request.Capability}.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The installation identity is invalid.");
        var actorId = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (!actorId.HasValue)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The installation is not assigned to an active agent employee.");
        try
        {
            object? result = request.Capability switch
            {
                CommunicationCapabilities.CoordinationStart => await coordination.StartAsync(
                    organizationId, actorId.Value, installationId,
                    Read<StartAgentCoordinationRequest>(request), cancellationToken),
                CommunicationCapabilities.CoordinationStartWork => await coordination.StartWorkAsync(
                    organizationId, actorId.Value, installationId,
                    Read<StartWorkItemCoordinationRequest>(request), cancellationToken),
                CommunicationCapabilities.CoordinationRespond => await coordination.RespondAsync(
                    organizationId, actorId.Value, installationId,
                    Read<RespondToAgentCoordinationRequest>(request), cancellationToken),
                CommunicationCapabilities.CoordinationRead => await coordination.ReadAsync(
                    organizationId, actorId.Value,
                    Read<ReadAgentCoordinationRequest>(request).SessionId, cancellationToken),
                CommunicationCapabilities.CoordinationList => await ListAsync(
                    organizationId, actorId.Value,
                    Read<ListAgentCoordinationRequest>(request), cancellationToken),
                CommunicationCapabilities.CoordinationResume => await coordination.ResumeAsync(
                    organizationId, actorId.Value, installationId,
                    Read<ResumeAgentCoordinationRequest>(request), cancellationToken),
                CommunicationCapabilities.CoordinationCancel => await coordination.CancelAsync(
                    organizationId, actorId.Value, false,
                    Read<CancelAgentCoordinationRequest>(request), cancellationToken),
                _ => null
            };
            return result is null
                ? Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound,
                    "The coordination session was not found.")
                : Success(request.RequestId, result);
        }
        catch (JsonException)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed,
                "The coordination payload is not valid JSON.");
        }
        catch (ArgumentException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message);
        }
    }

    private async Task<AgentCoordinationSessions> ListAsync(
        Guid organizationId,
        Guid actorId,
        ListAgentCoordinationRequest request,
        CancellationToken cancellationToken) =>
        new(await coordination.ListAsync(
            organizationId, actorId, request.ChatId, request.ActiveOnly, cancellationToken));

    private static T Read<T>(RequestCapability request) =>
        JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions)
        ?? throw new JsonException("The payload is empty.");

    private static CapabilityResult Success(string requestId, object payload) => new()
    {
        RequestId = requestId,
        Succeeded = true,
        ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))
    };

    private static CapabilityResult Failure(
        string requestId, PlatformCapabilityErrorCode code, string message) => new()
    {
        RequestId = requestId,
        Succeeded = false,
        ContentType = "application/json",
        Error = message,
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(
            new PlatformCapabilityError(code, message), JsonOptions))
    };
}
