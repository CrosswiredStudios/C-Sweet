using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;



/// <summary>Node-authenticated recovery reads and claims; discovery never grants execution authority.</summary>
public sealed class ComputeProviderWorkService(CSweetDbContext db, ComputeProviderWorkRequestVerifier verifier,
    ComputeDispatchAuthorizer authorizer, IAuditEventWriter audit, TimeProvider clock)
{
    public async Task<ComputeProviderWorkRequest> AuthorizeWakeAsync(SignedComputeProviderWorkRequest envelope, CancellationToken token)
    {
        var node = await verifier.VerifyAsync(envelope, token);
        if (node.OperationId.HasValue || node.AfterOperationId.HasValue || node.ResultSequence.HasValue)
            throw new UnauthorizedAccessException("Notification subscriptions cannot select work or receipts.");
        await audit.AppendAsync(new("compute.provider-wake.subscribe.v1", Category: "Infrastructure", Outcome: "Read",
            OrganizationId: node.OrganizationId, Actor: new("ComputeProvider", IdentityVerified: true), UseAmbientOrganization: false,
            MetadataJson: JsonSerializer.Serialize(new { node.NodeId, node.ProviderId, node.RequestId })), token);
        return node;
    }
    public async Task<ComputeProviderWorkPage> DiscoverAsync(SignedComputeProviderWorkRequest envelope, CancellationToken token)
    {
        var node = await verifier.VerifyAsync(envelope, token);
        if (node.OperationId.HasValue || node.ResultSequence.HasValue) throw new UnauthorizedAccessException("Discovery cannot select an operation.");
        var now = clock.GetUtcNow();
        var ids = await (from operation in db.ComputeOperations.AsNoTracking()
                         join environment in db.ComputeEnvironments.AsNoTracking() on operation.EnvironmentId equals environment.Id
                         where environment.OrganizationId == node.OrganizationId && operation.OrganizationId == node.OrganizationId &&
                             environment.ProviderNodeId == node.NodeId && environment.ProviderId == node.ProviderId &&
                             environment.Generation == operation.Generation && environment.TeardownConfirmedAt == null &&
                             (operation.Status == "Pending" || operation.Status == "Dispatching" || operation.Status == "Reconciling") &&
                             operation.NextAttemptAt <= now && (operation.DispatchLeaseExpiresAt == null || operation.DispatchLeaseExpiresAt <= now) &&
                             (!node.AfterOperationId.HasValue || operation.Id.CompareTo(node.AfterOperationId.Value) > 0)
                         orderby operation.Id
                         select operation.Id).Take(100).ToArrayAsync(token);
        // A sealed read audit must complete before any operation identity leaves Core.
        await audit.AppendAsync(new("compute.provider-work.read.v1", Category: "Infrastructure", Outcome: "Read",
            OrganizationId: node.OrganizationId, Actor: new("ComputeProvider", IdentityVerified: true), UseAmbientOrganization: false,
            MetadataJson: JsonSerializer.Serialize(new { node.NodeId, node.ProviderId, node.RequestId, OperationIds = ids })), token);
        return new(ids, ids.Length == 100 ? ids[^1] : null);
    }

    public async Task<ComputeResultAcknowledgement?> ReadResultReceiptAsync(SignedComputeProviderWorkRequest envelope, CancellationToken token)
    {
        var node = await verifier.VerifyAsync(envelope, token);
        if (node.OperationId is not { } id || node.ResultSequence is not { } sequence || node.ResultDigest is not { } digest)
            throw new UnauthorizedAccessException("Receipt lookup requires an exact operation, sequence and digest.");
        var state = await (from operation in db.ComputeOperations.AsNoTracking()
                           join environment in db.ComputeEnvironments.AsNoTracking() on operation.EnvironmentId equals environment.Id
                           where operation.Id == id && operation.OrganizationId == node.OrganizationId &&
                               environment.OrganizationId == node.OrganizationId && environment.ProviderNodeId == node.NodeId && environment.ProviderId == node.ProviderId
                           select new { operation.LastResultSequence, operation.LastResultDigest, operation.Status, operation.CompletedAt, operation.Generation,
                               CurrentGeneration = environment.Generation }).SingleOrDefaultAsync(token);
        ComputeResultDisposition? disposition = state is null ? null :
            state.LastResultSequence == sequence && state.LastResultDigest == digest ? ComputeResultDisposition.Recorded :
            state.LastResultSequence == sequence ? null :
            state.Status == "Superseded" || state.CurrentGeneration > state.Generation ? ComputeResultDisposition.Superseded :
            state.Status == "Completed" && state.CompletedAt.HasValue && state.LastResultSequence > 0 && state.LastResultDigest is not null
                ? ComputeResultDisposition.Completed : null;
        await audit.AppendAsync(new("compute.provider-result.receipt-read.v1", Category: "Infrastructure", Outcome: "Read",
            OrganizationId: node.OrganizationId, Actor: new("ComputeProvider", IdentityVerified: true), UseAmbientOrganization: false,
            MetadataJson: JsonSerializer.Serialize(new { node.NodeId, node.RequestId, OperationId = id, Sequence = sequence, Digest = digest, Disposition = disposition })), token);
        // Completion/supersession permits retiring this exact pending evidence only; it never confirms its physical state or teardown.
        return disposition.HasValue ? new(id, sequence, digest, false, disposition.Value) : null;
    }
    public async Task<ComputeDispatchPacket?> ClaimAsync(SignedComputeProviderWorkRequest envelope, CancellationToken token)
    {
        var node = await verifier.VerifyAsync(envelope, token);
        if (node.OperationId is not { } id || node.AfterOperationId.HasValue || node.ResultSequence.HasValue)
            throw new UnauthorizedAccessException("A dispatch claim must select exactly one operation.");
        return await authorizer.ClaimForNodeAsync(id, node, token);
    }
}
