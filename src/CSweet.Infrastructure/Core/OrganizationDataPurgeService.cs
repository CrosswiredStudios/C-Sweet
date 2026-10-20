using CSweet.Application.Core;
using CSweet.Application.Compute;
using CSweet.Application.Setup;
using CSweet.Compute.Contracts;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Core;

public sealed class OrganizationDataPurgeService(
    CSweetDbContext dbContext,
    IBusinessAgentInstallationCleanup agentCleanup,
    ILogger<OrganizationDataPurgeService> logger,
    IComputeBroker? computeBroker = null,
    TimeProvider? timeProvider = null,
    BusinessStorageCleanup? storageCleanup = null) : IOrganizationDataPurgeService
{
    // A missed provider result must not leave deletion permanently busy. This is
    // an attention threshold, never evidence that VM or disk teardown succeeded.
    internal static readonly TimeSpan ComputeCleanupStallTimeout = TimeSpan.FromMinutes(10);

    private static readonly HashSet<Type> PreservedEntityTypes =
    [
        typeof(AuditEvent),
        typeof(MemoryCaptureRetryReceipt),
        typeof(MemoryEpisodeRetryReceipt),
        typeof(MemoryEpisodeReextractionReceipt),
        typeof(MemoryExtractionInputReceipt), // Removed through its job/message cascade, after quiescing.
        typeof(MemoryReviewReceipt),
        typeof(MemoryErasureReceipt),
        typeof(MemoryTransferReceipt),
        typeof(AgentMemoryReadReceipt), // Removed only through runtime/installation cascade after quiescing.
        typeof(MemoryCaptureExclusion),
        typeof(Worker)
    ];

    public async Task PurgeAsync(Guid organizationId, CancellationToken cancellationToken = default, bool retryStalledComputeCleanup = false)
    {
        // The provider owns VM and private-disk teardown. Keep its durable records
        // until it has confirmed physical cleanup. Stopped VMs may still have disks.
        if (await dbContext.ComputeEnvironments.AsNoTracking().AnyAsync(
                environment => environment.OrganizationId == organizationId &&
                    environment.TeardownConfirmedAt == null, cancellationToken))
        {
            await RequestComputeCleanupAsync(organizationId, retryStalledComputeCleanup, cancellationToken);
            throw new OrganizationDeletionException(
                "Compute cleanup is in progress. The business will be deleted automatically after VM and disk teardown is confirmed.",
                errorCode: "compute_cleanup_pending");
        }
        try
        {
            await agentCleanup.QuiesceAsync(organizationId, cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException or CSweet.Infrastructure.Setup.AgentCleanupPendingException)
        {
            logger.LogInformation(exception, "Agent schedules changed while stopping business {OrganizationId}; deletion will resume.", organizationId);
            throw new OrganizationDeletionException(
                "Agent workloads are being stopped. Business deletion will continue automatically.",
                exception, errorCode: "agent_cleanup_pending");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not quiesce agent workloads for organization {OrganizationId}.", organizationId);
            throw new OrganizationDeletionException(
                "The business could not be deleted because its agent workloads could not be stopped. Check agent runtime health, then retry the deletion.",
                exception);
        }

        if (storageCleanup is not null)
        {
            var request = await dbContext.ResourceCleanupRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Kind == CSweet.Infrastructure.Setup.ResourceCleanupQueue.BusinessStorage && x.ResourceId == organizationId, cancellationToken);
            if (request?.CompletedAt is null)
            {
                if (request is null)
                {
                    await CSweet.Infrastructure.Setup.ResourceCleanupQueue.EnqueueAsync(dbContext,
                        CSweet.Infrastructure.Setup.ResourceCleanupQueue.BusinessStorage, organizationId, organizationId,
                        DateTimeOffset.UtcNow, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                throw new OrganizationDeletionException("Business storage cleanup is in progress and will retry automatically.",
                    errorCode: "storage_cleanup_pending");
            }
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            if (dbContext.Database.IsRelational())
            {
                await DeleteScopedRelationalRowsAsync(organizationId, cancellationToken);
                // Set-based deletes bypass change tracking. Clear stale tracked dependents before
                // EF applies the final organization cascade so it does not delete rows twice.
                dbContext.ChangeTracker.Clear();
            }
            else
            {
                await DeleteKnownRestrictiveRowsAsync(organizationId, cancellationToken);
            }

            var organization = await dbContext.CoreOrganizations
                .SingleOrDefaultAsync(x => x.Id == organizationId, cancellationToken);
            if (organization is not null)
            {
                dbContext.CoreOrganizations.Remove(organization);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            logger.LogError(exception, "Could not purge organization {OrganizationId}.", organizationId);
            throw new OrganizationDeletionException(
                "The business could not be deleted because its data cleanup did not complete. The database cleanup was rolled back; agent workloads remain disabled. Retry the deletion.",
                exception);
        }
    }

    private async Task RequestComputeCleanupAsync(Guid organizationId, bool retryStalledComputeCleanup, CancellationToken cancellationToken)
    {
        if (computeBroker is null)
            throw new OrganizationDeletionException(
                "The business is awaiting confirmed VM and disk teardown. Release its instances on Compute, then retry deletion after cleanup is confirmed.",
                errorCode: "compute_cleanup_failed");

        // Bound each request and skip work already durably queued for this generation.
        // Failed/blocked destroy operations can be retried with fresh current authority.
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var stalledBefore = now - ComputeCleanupStallTimeout;
        var environments = await dbContext.ComputeEnvironments.AsNoTracking()
            .Where(environment => environment.OrganizationId == organizationId && environment.TeardownConfirmedAt == null &&
                !dbContext.ComputeOperations.Any(operation => operation.EnvironmentId == environment.Id &&
                    operation.Generation == environment.Generation && operation.Action == InfrastructureActions.Destroy &&
                    (operation.Status == "Pending" || operation.Status == "Dispatching" || operation.Status == "Reconciling") &&
                    (!retryStalledComputeCleanup || operation.CreatedAt > stalledBefore || operation.DispatchLeaseExpiresAt > now)))
            .OrderBy(environment => environment.Id).Take(100).ToListAsync(cancellationToken);
        var failed = false;
        foreach (var environment in environments)
        {
            try
            {
                // Use the broker so teardown intent, audit and provider wake commit together.
                // Never infer physical cleanup from an expired lease or a stopped/failed VM.
                await computeBroker.ChangeLifecycleAsync(organizationId, environment.InstallationId,
                    new(environment.Id, environment.Generation, InfrastructureActions.Destroy,
                        $"business-delete:{environment.Id:N}:{environment.Generation}"), cancellationToken);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
            {
                failed = true;
                logger.LogWarning(exception, "Could not request compute cleanup for business {OrganizationId}, environment {EnvironmentId}.",
                    organizationId, environment.Id);
            }
        }
        if (failed)
            throw new OrganizationDeletionException(
                "Compute cleanup could not be requested for every instance. Open Compute to check or release the remaining instances, then retry deleting the business.",
                errorCode: "compute_cleanup_failed");
    }

    internal static IReadOnlyList<IEntityType> ScopedEntityTypes(IModel model) =>
        model.GetEntityTypes()
            .Where(entity => entity.BaseType is null &&
                             entity.FindPrimaryKey() is not null &&
                             entity.GetTableName() is not null &&
                             entity.ClrType != typeof(Organization) &&
                             !PreservedEntityTypes.Contains(entity.ClrType) &&
                             ScopeProperty(entity) is not null)
            .ToList();

    private async Task DeleteScopedRelationalRowsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        // Approvals are scoped through their artifact rather than by their own
        // OrganizationId column. Their restrictive revision foreign key prevents
        // revisions from being removed before the artifact cascade can reach them.
        await dbContext.CoreApprovals
            .Where(approval => dbContext.CoreArtifacts.Any(artifact =>
                artifact.Id == approval.ArtifactId && artifact.OrganizationId == organizationId))
            .ExecuteDeleteAsync(cancellationToken);

        // Package members have no OrganizationId and restrict artifact deletion.
        await dbContext.ArtifactPackageMembers
            .Where(member => dbContext.CoreArtifacts.Any(artifact =>
                artifact.Id == member.ArtifactId && artifact.OrganizationId == organizationId))
            .ExecuteDeleteAsync(cancellationToken);

        // Other indirect dependents can likewise restrict deletion of business-owned
        // parents. Keep these deletes in the same transaction as the scoped purge.
        await dbContext.BenchmarkAssessments
            .Where(assessment => dbContext.BenchmarkTrials.Any(trial =>
                trial.Id == assessment.TrialId && trial.OrganizationId == organizationId))
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.BenchmarkWakes
            .Where(wake => wake.OrganizationId == organizationId ||
                wake.TrialId != null && dbContext.BenchmarkTrials.Any(trial =>
                    trial.Id == wake.TrialId && trial.OrganizationId == organizationId))
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.ConversationParticipants
            .Where(participant => dbContext.CoreOrganizationUsers.Any(user =>
                user.Id == participant.OrganizationUserId && user.OrganizationId == organizationId))
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.AgentCoordinationTurns
            .Where(turn => dbContext.CoreOrganizationUsers.Any(user =>
                user.Id == turn.SpeakerOrganizationUserId && user.OrganizationId == organizationId))
            .ExecuteDeleteAsync(cancellationToken);

        var businessId = organizationId.ToString("D");
        await dbContext.WorkTaskIntegrationReceipts.Where(x => x.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.WorkDeliveryExecutions.Where(x => dbContext.WorkDeliveryPlans.Any(p => p.Id == x.PlanId && p.OrganizationId == organizationId))
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.WorkExecutionAttempts
            .Where(attempt => attempt.AgentWorkItemId != null &&
                dbContext.AgentWorkItems.Any(workItem =>
                    workItem.Id == attempt.AgentWorkItemId && workItem.OrganizationId == businessId))
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.WorkItemDependencies
            .Where(dependency =>
                dbContext.CoreWorkTasks.Any(workItem =>
                    workItem.Id == dependency.WorkItemId && workItem.OrganizationId == organizationId) ||
                dbContext.CoreWorkTasks.Any(workItem =>
                    workItem.Id == dependency.DependsOnWorkItemId && workItem.OrganizationId == organizationId))
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.WorkItemExecutions
            .Where(execution => dbContext.CoreWorkTasks.Any(workItem =>
                workItem.Id == execution.WorkItemId && workItem.OrganizationId == organizationId))
            .ExecuteDeleteAsync(cancellationToken);

        var scopedTypes = ScopedEntityTypes(dbContext.Model);
        var tables = PurgeTables(scopedTypes);
        var sqlHelper = dbContext.GetService<ISqlGenerationHelper>();

        foreach (var table in ChildFirst(tables, scopedTypes))
        {
            var tableIdentifier = sqlHelper.DelimitIdentifier(table.Name, table.Schema);
            var columnIdentifier = sqlHelper.DelimitIdentifier(table.ColumnName);
            var sql = table.ValueKind == ScopeValueKind.Guid
                ? $"DELETE FROM {tableIdentifier} WHERE {columnIdentifier} = {{0}}"
                : $"DELETE FROM {tableIdentifier} WHERE {columnIdentifier} IN ({{0}}, {{1}}, {{2}}, {{3}})";
            object[] values = table.ValueKind == ScopeValueKind.Guid ? [organizationId] :
                [organizationId.ToString("D"), organizationId.ToString("N"), organizationId.ToString("D").ToUpperInvariant(), organizationId.ToString("N").ToUpperInvariant()];
            await dbContext.Database.ExecuteSqlRawAsync(sql, values, cancellationToken);
        }

        // Business onboarding records use ResultOrganizationId rather than OrganizationId.
        await dbContext.BusinessOnboardingOperations
            .Where(x => x.ResultOrganizationId == organizationId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task DeleteKnownRestrictiveRowsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        dbContext.AgentCoordinationTurns.RemoveRange(await dbContext.AgentCoordinationTurns
            .Where(x => x.Session!.OrganizationId == organizationId)
            .ToListAsync(cancellationToken));
        dbContext.AgentCoordinationSessions.RemoveRange(await dbContext.AgentCoordinationSessions
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken));
        dbContext.TeamMemberships.RemoveRange(await dbContext.TeamMemberships
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken));
        dbContext.BusinessOnboardingOperations.RemoveRange(await dbContext.BusinessOnboardingOperations
            .Where(x => x.ResultOrganizationId == organizationId)
            .ToListAsync(cancellationToken));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static ScopePropertyDescriptor? ScopeProperty(IEntityType entity)
    {
        if (entity.FindProperty("OrganizationId") is { } organizationProperty)
        {
            var type = Nullable.GetUnderlyingType(organizationProperty.ClrType) ?? organizationProperty.ClrType;
            if (type == typeof(Guid))
                return new ScopePropertyDescriptor(organizationProperty, ScopeValueKind.Guid);
            if (type == typeof(string))
                return new ScopePropertyDescriptor(organizationProperty, ScopeValueKind.String);
        }

        if (entity.ClrType == typeof(AgentInstallation) && entity.FindProperty("BusinessId") is { } businessProperty)
            return new ScopePropertyDescriptor(businessProperty, ScopeValueKind.String);

        if (entity.ClrType == typeof(ExecutionWorkloadAssignment) && entity.FindProperty("BusinessId") is { } assignmentProperty)
            return new ScopePropertyDescriptor(assignmentProperty, ScopeValueKind.String);

        return null;
    }

    private static IReadOnlyDictionary<TableKey, PurgeTable> PurgeTables(IReadOnlyList<IEntityType> entityTypes)
    {
        var tables = new Dictionary<TableKey, PurgeTable>();
        foreach (var entity in entityTypes)
        {
            var tableName = entity.GetTableName()!;
            var schema = entity.GetSchema();
            var store = StoreObjectIdentifier.Table(tableName, schema);
            var scope = ScopeProperty(entity)!;
            var columnName = scope.Property.GetColumnName(store)
                ?? throw new InvalidOperationException($"No scope column was mapped for {entity.DisplayName()}.");
            var key = new TableKey(tableName, schema);
            tables.TryAdd(key, new PurgeTable(tableName, schema, columnName, scope.ValueKind));
        }

        return tables;
    }

    private static IReadOnlyList<PurgeTable> ChildFirst(
        IReadOnlyDictionary<TableKey, PurgeTable> tables,
        IReadOnlyList<IEntityType> entityTypes)
    {
        var edges = tables.Keys.ToDictionary(x => x, _ => new HashSet<TableKey>());
        var incoming = tables.Keys.ToDictionary(x => x, _ => 0);

        foreach (var entity in entityTypes)
        {
            var dependent = new TableKey(entity.GetTableName()!, entity.GetSchema());
            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var principalType = foreignKey.PrincipalEntityType;
                if (principalType.GetTableName() is not { } principalName)
                    continue;
                var principal = new TableKey(principalName, principalType.GetSchema());
                if (dependent == principal || !tables.ContainsKey(principal) || !edges[dependent].Add(principal))
                    continue;
                incoming[principal]++;
            }
        }

        var ready = new Queue<TableKey>(incoming.Where(x => x.Value == 0).Select(x => x.Key));
        var ordered = new List<TableKey>(tables.Count);
        while (ready.TryDequeue(out var current))
        {
            ordered.Add(current);
            foreach (var principal in edges[current])
            {
                if (--incoming[principal] == 0)
                    ready.Enqueue(principal);
            }
        }

        if (ordered.Count != tables.Count)
        {
            throw new InvalidOperationException("Organization-scoped tables contain a foreign-key cycle that must be classified explicitly.");
        }

        return ordered.Select(x => tables[x]).ToList();
    }

    private sealed record ScopePropertyDescriptor(IProperty Property, ScopeValueKind ValueKind);
    private sealed record PurgeTable(string Name, string? Schema, string ColumnName, ScopeValueKind ValueKind);
    private readonly record struct TableKey(string Name, string? Schema);
    private enum ScopeValueKind { Guid, String }
}
