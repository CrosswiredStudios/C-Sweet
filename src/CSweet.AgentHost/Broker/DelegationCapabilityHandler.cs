using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class DelegationCapabilityHandler(CSweetDbContext db, ITrackedDelegationService service) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool CanHandle(string capability) => DelegationCapabilities.All.Contains(capability);
    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    { yield return await Execute(session, request, cancellationToken); }

    private async Task<CapabilityResult> Execute(AgentSession session, RequestCapability request, CancellationToken token)
    {
        try
        {
            if (!session.Grant.RequestedCapabilities.Contains(request.Capability) ||
                !Guid.TryParse(session.BusinessId, out var org) || !Guid.TryParse(session.InstallationId, out var installation))
                throw new UnauthorizedAccessException("The capability is not granted to this installation.");
            var actor = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == org &&
                x.AgentInstallationId == installation && x.IsActive && x.ArchivedAt == null).Select(x => x.Id).SingleAsync(token);
            object value = request.Capability switch
            {
                DelegationCapabilities.Create => await service.CreateAsync(org, actor, Read<DelegateWithTrackingRequest>(request), token),
                DelegationCapabilities.Read => await service.ReadAsync(org, actor, Read<ReadDelegationRequest>(request), token),
                DelegationCapabilities.Respond => await service.RespondAsync(org, actor, Read<DelegationResponseRequest>(request), token),
                DelegationCapabilities.Publish => await service.PublishAsync(org, actor, Read<PublishDelegationReportRequest>(request), token),
                DelegationCapabilities.Cancel => await service.CancelAsync(org, actor, Read<CancelDelegationRequest>(request), token),
                DelegationCapabilities.Follow => await service.FollowAsync(org, actor, Read<FollowWorkRequest>(request), true, token),
                DelegationCapabilities.Unfollow => await service.FollowAsync(org, actor, Read<FollowWorkRequest>(request), false, token),
                DelegationCapabilities.ReadTracking => await service.ReadTrackingAsync(org, actor, Read<ReadWorkTrackingRequest>(request).WorkItemId, token),
                DelegationCapabilities.Progress => await service.ProgressAsync(org, actor, Read<WorkProgressRequest>(request), token),
                DelegationCapabilities.Pending => await service.PendingAsync(org, actor, Read<PendingTrackingRequest>(request).Limit, token),
                DelegationCapabilities.Processed => await service.ProcessedAsync(org, actor, Read<ProcessTrackingUpdateRequest>(request), token),
                _ => throw new ArgumentException("Unknown capability.")
            };
            return new() { RequestId = request.RequestId, Succeeded = true, ContentType = "application/json",
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, Json)) };
        }
        catch (Exception e) when (e is JsonException or ArgumentException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException or DbUpdateConcurrencyException)
        {
            var code = e is UnauthorizedAccessException ? PlatformCapabilityErrorCode.Denied :
                e is KeyNotFoundException ? PlatformCapabilityErrorCode.NotFound :
                e is InvalidOperationException or DbUpdateConcurrencyException ? PlatformCapabilityErrorCode.Conflict : PlatformCapabilityErrorCode.ValidationFailed;
            return new() { RequestId = request.RequestId, Succeeded = false, ContentType = "application/json", Error = e.Message,
                Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, e.Message), Json)) };
        }
    }
    private static T Read<T>(RequestCapability request) => JsonSerializer.Deserialize<T>(request.Payload.Span, Json) ?? throw new JsonException("Empty payload.");
}
