using CSweet.Application.Core;
using CSweet.Application.Setup;
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
    ILogger<OrganizationDataPurgeService> logger) : IOrganizationDataPurgeService
{
    private static readonly HashSet<Type> PreservedEntityTypes =
    [
        typeof(AuditEvent),
        typeof(Worker)
    ];

    public async Task PurgeAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        try
        {
            await agentCleanup.QuiesceAsync(organizationId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not quiesce agent workloads for organization {OrganizationId}.", organizationId);
            throw new OrganizationDeletionException(
                "The business could not be deleted because its agent workloads could not be stopped. The workloads were disabled; retry the deletion.",
                exception);
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

        var scopedTypes = ScopedEntityTypes(dbContext.Model);
        var tables = PurgeTables(scopedTypes);
        var sqlHelper = dbContext.GetService<ISqlGenerationHelper>();

        foreach (var table in ChildFirst(tables, scopedTypes))
        {
            var tableIdentifier = sqlHelper.DelimitIdentifier(table.Name, table.Schema);
            var columnIdentifier = sqlHelper.DelimitIdentifier(table.ColumnName);
            var sql = $"DELETE FROM {tableIdentifier} WHERE {columnIdentifier} = {{0}}";
            object value = table.ValueKind == ScopeValueKind.Guid
                ? organizationId
                : organizationId.ToString("D");
            await dbContext.Database.ExecuteSqlRawAsync(sql, new[] { value }, cancellationToken);
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
