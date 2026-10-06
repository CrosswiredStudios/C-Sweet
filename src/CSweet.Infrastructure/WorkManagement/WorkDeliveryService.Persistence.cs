using Microsoft.EntityFrameworkCore;
using Npgsql;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    private async Task RevokeTaskArtifactGrantsAsync(WorkDeliveryPlan plan, CancellationToken ct)
    {
        var taskIds = Scopes(plan).Where(x => x.Scope == WorkExecutionScopes.Story).SelectMany(x => x.ChildIds).ToArray();
        var stageIds = await db.WorkStageExecutions.Where(x => x.ItemExecution != null && taskIds.Contains(x.ItemExecution.WorkItemId))
            .Select(x => x.Id).ToListAsync(ct);
        var grants = await db.ScopedActionGrants.Where(x => x.OrganizationId == plan.OrganizationId && x.GrantedBySubjectKind == GrantSubjectKind.AutomationIdentity &&
            x.GrantedBySubjectId.HasValue && stageIds.Contains(x.GrantedBySubjectId.Value) && x.Action == "artifact.read" && x.RevokedAt == null).ToListAsync(ct);
        foreach (var grant in grants) { grant.RevokedAt = clock.GetUtcNow(); grant.Revision++; }
    }
    private async Task SaveDeliveryAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException postgres &&
            (postgres.SqlState == PostgresErrorCodes.SerializationFailure ||
             postgres.SqlState == PostgresErrorCodes.UniqueViolation && postgres.ConstraintName is { } constraint &&
                (constraint.StartsWith("IX_WorkDelivery", StringComparison.Ordinal) ||
                 constraint.StartsWith("IX_AgentPlatformEventOutbox", StringComparison.Ordinal) ||
                 constraint.StartsWith("IX_ApplicationRealtimeOutbox", StringComparison.Ordinal))))
        {
            // A competing transaction may insert its atomic outbox/receipt before
            // this transaction updates the revision row. Both races require the
            // caller to re-read current authorized state, never replay stale scope.
            throw new DbUpdateConcurrencyException("Delivery changed concurrently; re-read the current plan and retry with its revision.", error);
        }
    }
}
