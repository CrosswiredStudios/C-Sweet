using CSweet.Compute.Contracts;
using System.Data;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

/// <summary>Consumes enrolled-provider evidence. This service is never an agent capability.</summary>
public sealed class ComputeResultReconciler(CSweetDbContext db, ComputeProviderResultVerifier verifier,
    TimeProvider clock, IAuditEventWriter audit)
{
    public async Task<bool> ApplyAsync(SignedComputeProviderResult envelope, CancellationToken token)
    {
        try { return await ApplyCoreAsync(envelope, token); }
        catch (UnauthorizedAccessException)
        {
            await audit.AppendAsync(new("compute.provider-result.rejected.v1", Category: "Infrastructure", Outcome: "Rejected",
                Actor: new("ComputeProvider", IdentityVerified: false), ErrorCode: "invalid_provider_evidence",
                UseAmbientOrganization: false), token);
            throw;
        }
    }

    private async Task<bool> ApplyCoreAsync(SignedComputeProviderResult envelope, CancellationToken token)
    {
        var result = await verifier.VerifyAsync(envelope, token);
        var digest = ComputeBroker.Digest(envelope.PayloadJson);
        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            var operation = await db.ComputeOperations.SingleOrDefaultAsync(x => x.Id == result.OperationId, token)
                ?? throw new UnauthorizedAccessException("Compute operation is unavailable.");
            var environment = await db.ComputeEnvironments.SingleAsync(x => x.Id == operation.EnvironmentId, token);
            if (result.EnvironmentId != environment.Id || result.OrganizationId != environment.OrganizationId ||
                result.InstallationId != environment.InstallationId || operation.OrganizationId != result.OrganizationId ||
                operation.InstallationId != result.InstallationId || result.ProviderId != environment.ProviderId ||
                result.NodeId != environment.ProviderNodeId || result.Action != operation.Action ||
                result.Generation != operation.Generation || result.SpecificationDigest != ComputeBroker.Digest(environment.SpecificationJson))
                throw new UnauthorizedAccessException("Compute evidence does not match its recorded operation and placement.");
            if (environment.Generation != result.Generation || operation.Status == "Superseded") return false;
            if (result.Sequence < operation.LastResultSequence) return false;
            if (result.Sequence == operation.LastResultSequence)
            {
                if (digest != operation.LastResultDigest) throw new UnauthorizedAccessException("Compute result sequence has conflicting evidence.");
                return false;
            }
            if (operation.Status == "Completed") return false;
            if (operation.Status is not ("Dispatching" or "Reconciling") || result.ObservedAt < operation.CreatedAt ||
                !AllowedState(operation.Action, result.State) ||
                result.TeardownConfirmed != (result.State == ComputeLifecycleState.Destroyed) ||
                result.TeardownConfirmed && operation.Action != InfrastructureActions.Destroy ||
                result.ResourceId is { } resource && (resource.Length is < 1 or > 256 || resource.Any(char.IsControl)) ||
                environment.ProviderResourceId is { } known && result.ResourceId != known ||
                result.State is ComputeLifecycleState.Ready or ComputeLifecycleState.Busy or ComputeLifecycleState.Stopped && result.ResourceId is null ||
                result.FailureCode is { } failure && !ComputeSpecification.Identifier(failure))
                throw new UnauthorizedAccessException("Compute result state is invalid for this operation.");
            if (operation.Action is InfrastructureActions.Execute or InfrastructureActions.PublishPort)
            {
                var workload = operation.WorkloadJson is null ? null : JsonSerializer.Deserialize<ComputeWorkload>(operation.WorkloadJson, ComputeProtocol.Json);
                var output = result.Workload ?? throw new UnauthorizedAccessException("Workload evidence is missing.");
                if (output.ErrorCode is not (null or "outcome-unknown" or "guest-unavailable") ||
                    output.ErrorCode is not null && (output.Command is not null || output.Url is not null || output.UrlExpiresAt is not null))
                    throw new UnauthorizedAccessException("Workload evidence has conflicting terms.");
                if (output.ErrorCode is null && operation.Action == InfrastructureActions.Execute)
                {
                    var command = output.Command;
                    if (workload?.Command is not { } requested || command is null || command.RequestId != requested.RequestId ||
                        command.StandardOutput is null || command.StandardError is null ||
                        (long)command.StandardOutput.Length + command.StandardError.Length > requested.MaximumOutputBytes ||
                        output.Url is not null || output.UrlExpiresAt is not null)
                        throw new UnauthorizedAccessException("Command evidence exceeds the recorded request.");
                }
                if (output.ErrorCode is null && operation.Action == InfrastructureActions.PublishPort &&
                    (workload?.PublishPort is null || output.Command is not null || output.UrlExpiresAt is null ||
                     output.UrlExpiresAt > environment.LeaseExpiresAt || !Uri.TryCreate(output.Url, UriKind.Absolute, out var url) ||
                     url.Scheme != "http" || url.Host != "127.0.0.1" || url.Port < 1024 || url.AbsolutePath != "/" ||
                     url.Query != "" || url.Fragment != "" || url.UserInfo != ""))
                    throw new UnauthorizedAccessException("Publication evidence is invalid.");
            }
            else if (result.Workload is not null) throw new UnauthorizedAccessException("Unexpected workload evidence.");
            var now = clock.GetUtcNow();
            operation.LastResultSequence = result.Sequence; operation.LastResultDigest = digest; operation.Revision++;
            environment.ProviderResourceId ??= result.ResourceId;
            operation.ResultJson = result.Workload is null ? null : JsonSerializer.Serialize(result.Workload, ComputeBroker.Json);
            environment.State = result.State; environment.LastFailureCode = result.FailureCode;
            environment.UpdatedAt = now;
            var completed = result.State is ComputeLifecycleState.Ready or ComputeLifecycleState.Busy or
                ComputeLifecycleState.Stopped or ComputeLifecycleState.Destroyed || operation.Action is InfrastructureActions.Execute or InfrastructureActions.PublishPort;
            operation.Status = completed ? "Completed" : "Reconciling";
            operation.FailureCode = result.FailureCode;
            operation.NextAttemptAt = now.AddSeconds(30);
            environment.NextAttemptAt = completed ? null : operation.NextAttemptAt;
            if (completed) operation.CompletedAt = now;
            // Only explicit evidence from the current destroy operation releases physical capacity.
            if (result.TeardownConfirmed) environment.TeardownConfirmedAt = now;
            var id = Guid.NewGuid();
            var evidence = new AuditEventWriteRequest("compute.provider-result.v1", Category: "Infrastructure",
                Outcome: completed ? "Completed" : "Observed", OrganizationId: environment.OrganizationId,
                EntityType: "ComputeEnvironment", EntityId: environment.Id, Actor: new("ComputeProvider"),
                OccurredAt: now, UseAmbientOrganization: false, EventId: id,
                MetadataJson: JsonSerializer.Serialize(new { operation.Id, operation.Action, operation.Generation,
                    environment.InstallationId, environment.ProviderId, environment.ProviderNodeId,
                    result.Sequence, result.State, result.TeardownConfirmed, operation.AuthorityJson }));
            db.AuditOutbox.Add(new() { Id = id, CreatedAt = now, RequestJson = JsonSerializer.Serialize(evidence) });
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return true;
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private static bool AllowedState(string action, ComputeLifecycleState state) => action switch
    {
        InfrastructureActions.Provision or InfrastructureActions.Start or InfrastructureActions.Restart => state is
            ComputeLifecycleState.Provisioning or ComputeLifecycleState.Bootstrapping or ComputeLifecycleState.Ready or
            ComputeLifecycleState.Busy or ComputeLifecycleState.Failed,
        InfrastructureActions.Execute or InfrastructureActions.PublishPort => state is ComputeLifecycleState.Ready or ComputeLifecycleState.Failed,
        InfrastructureActions.Stop => state is ComputeLifecycleState.Stopping or ComputeLifecycleState.Stopped or ComputeLifecycleState.Failed,
        InfrastructureActions.Destroy => state is ComputeLifecycleState.Destroying or ComputeLifecycleState.Destroyed or ComputeLifecycleState.Failed,
        _ => false
    };
}
