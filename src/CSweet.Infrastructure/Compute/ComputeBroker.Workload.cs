using System.Data;
using System.Text.Json;
using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed partial class ComputeBroker
{
    public async Task<ComputeOperationView> SubmitWorkloadAsync(Guid organizationId, Guid installationId,
        RequestComputeWorkload request, CancellationToken token)
    {
        try
        {
            if (request.EnvironmentId == Guid.Empty || request.ExpectedGeneration < 1 || !Key(request.IdempotencyKey) || request.Workload is null)
                throw new ArgumentException("A workload, current generation and stable key are required.");
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            var environment = await db.ComputeEnvironments.SingleOrDefaultAsync(x => x.Id == request.EnvironmentId &&
                x.OrganizationId == organizationId && x.InstallationId == installationId, token)
                ?? throw new UnauthorizedAccessException("Environment unavailable.");
            var spec = JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, Json)!;
            var workload = request.Workload.Validate(spec.OperatingSystem);
            var action = workload.Command is null ? InfrastructureActions.PublishPort : InfrastructureActions.Execute;
            var actions = action == InfrastructureActions.PublishPort ? new[] { action, InfrastructureActions.Inbound } : new[] { action };
            var grants = await GrantsAsync(organizationId, installationId, environment.WorkstreamId, token);
            var now = clock.GetUtcNow();
            var authority = new List<ComputeActionAuthorization>();
            foreach (var required in actions)
            {
                await RequireActorAsync(organizationId, installationId, environment.WorkstreamId, required, token);
                RequireAction(grants, required, now);
                var grant = grants.FirstOrDefault(g => g.Action == required && g.ExpiresAt >= environment.LeaseExpiresAt &&
                    g.Id != Guid.Empty && g.Revision > 0 && Allows(g.ConstraintsJson))
                    ?? throw new UnauthorizedAccessException("Workload exceeds current grants.");
                authority.Add(new(grant.Id, grant.Revision, required, grant.ExpiresAt!.Value));
            }
            var json = JsonSerializer.Serialize(workload, Json);
            var digest = Digest(JsonSerializer.Serialize(new { request.EnvironmentId, request.ExpectedGeneration, workload }, Json));
            var prior = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.InstallationId == installationId && x.IdempotencyKey == request.IdempotencyKey, token);
            if (prior is not null)
            {
                if (prior.RequestDigest != digest) throw new InvalidOperationException("Request key already has different terms.");
                return OperationView(prior);
            }
            if (environment.Generation != request.ExpectedGeneration || environment.State != ComputeLifecycleState.Ready ||
                environment.DesiredState != ComputeDesiredState.Running || environment.LeaseExpiresAt <= now.AddSeconds(45) ||
                environment.TeardownConfirmedAt is not null || environment.ProviderResourceId is null)
                throw new InvalidOperationException("A ready, leased environment and its current generation are required.");
            if (await db.ComputeOperations.AnyAsync(x => x.EnvironmentId == environment.Id &&
                (x.Status == "Pending" || x.Status == "Dispatching" || x.Status == "Reconciling" || x.Status == "Blocked"), token))
                throw new InvalidOperationException("Environment has outstanding work.");
            environment.Generation = checked(environment.Generation + 1);
            environment.State = ComputeLifecycleState.Busy; environment.UpdatedAt = now; environment.NextAttemptAt = now;
            var operation = new ComputeOperation { Id = Guid.NewGuid(), OrganizationId = organizationId, InstallationId = installationId,
                EnvironmentId = environment.Id, Generation = environment.Generation, Action = action, IdempotencyKey = request.IdempotencyKey,
                RequestDigest = digest, WorkloadJson = json, AuthorityJson = JsonSerializer.Serialize(authority, Json), CreatedAt = now, NextAttemptAt = now };
            db.ComputeOperations.Add(operation);
            QueueAudit(environment, "Accepted", authority, action);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return OperationView(operation);

            bool Allows(string constraintsJson)
            {
                try
                {
                    var constraints = JsonSerializer.Deserialize<ComputeGrantConstraints>(constraintsJson, Json);
                    return constraints?.Allows(spec, 0) == true &&
                        (constraints.EnvironmentId is null || constraints.EnvironmentId == environment.Id) &&
                        (workload.PublishPort is null || constraints.AllowedPublishedPorts?.Contains(workload.PublishPort.Value) == true);
                }
                catch (JsonException) { return false; }
            }
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    public async Task<ComputeOperationView> ReadOperationAsync(Guid organizationId, Guid installationId, Guid operationId, CancellationToken token)
    {
        var operation = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId &&
            x.OrganizationId == organizationId && x.InstallationId == installationId, token)
            ?? throw new UnauthorizedAccessException("Operation unavailable.");
        await ReadAsync(organizationId, installationId, operation.EnvironmentId, token);
        return OperationView(operation);
    }

    private static ComputeOperationView OperationView(ComputeOperation operation) => new(operation.Id, operation.EnvironmentId,
        operation.Generation, operation.Status, operation.FailureCode,
        operation.ResultJson is null ? null : JsonSerializer.Deserialize<ComputeWorkloadResult>(operation.ResultJson, Json));
}
