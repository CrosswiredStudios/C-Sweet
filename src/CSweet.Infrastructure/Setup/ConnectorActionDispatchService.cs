using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>One fresh scope per dispatch isolates failures and optimistic claims from unrelated actions.</summary>
public sealed class ConnectorActionDispatchService(CSweetDbContext db, ConnectorMutationExecutor executor,
    ConnectorActionApprovalService approvals)
{
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-5);
        var execution = await db.ConnectorExecutions.Where(x => x.ApprovalId != null &&
            (x.Status == "Approved" || x.Status == "Executing" && x.UpdatedAt < stale))
            .OrderBy(x => x.UpdatedAt).FirstOrDefaultAsync(ct);
        if (execution is null) return false;
        if (execution.Status == "Executing")
        {
            // A worker disappeared after taking its durable send fence. Never reclaim it for another send.
            execution.Status = "Indeterminate"; execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
            await approvals.QueueExecutionEventAsync(execution, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }
        try { await executor.ExecuteAsync(execution.OrganizationId, execution.RequesterInstallationId, execution.Id, execution.PlanHash, ct); }
        catch (DbUpdateConcurrencyException) { return true; } // Another worker or disconnect won the claim.
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await db.Entry(execution).ReloadAsync(ct);
            if (execution.Status == "Approved")
            {
                execution.Status = "Blocked"; execution.Revision++; execution.UpdatedAt = DateTimeOffset.UtcNow;
                await approvals.QueueExecutionEventAsync(execution, ct);
                await db.SaveChangesAsync(ct);
            }
            // The executor durably classifies errors after a claim. Provider exception text is never persisted.
        }
        return true;
    }
}
