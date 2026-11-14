using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Governed project closure binds approved scope to an explicitly declared accepted final product.</summary>
public static class WorkstreamDeliveryCompletionValidator
{
    public static async Task ValidateAsync(CSweetDbContext db, Workstream project, WorkstreamDeliveryCompletion proof, CancellationToken ct)
    {
        var plan = await db.WorkDeliveryPlans.AsNoTracking().Include(p=>p.Executions).Include(p=>p.Findings)
            .SingleOrDefaultAsync(p=>p.Id==proof.PlanId&&p.OrganizationId==project.OrganizationId&&p.WorkstreamId==project.Id,ct);
        if (plan is null || plan.Status!="Completed" || plan.ScopeRevision!=proof.ScopeRevision || plan.Findings.Any(f=>f.Status=="Open"))
            throw new InvalidOperationException("Project completion requires a completed current final-product plan without unresolved findings.");
        var scopes=WorkDeliveryService.Decode<WorkDeliveryScopeSnapshot[]>(plan.ScopesJson);
        if (!scopes.Any(s=>s.TicketWorkflow?.ProjectCompletionSource==proof.ApprovedSource) ||
            scopes.Any(s=>!plan.Executions.Any(e=>e.ScopeRevision==plan.ScopeRevision&&e.Scope==s.Scope&&e.WorkItemId==s.ItemId&&e.Status=="Completed")))
            throw new InvalidOperationException("Completion must use an explicitly declared final product bound to the approved source.");
        await RequireCurrentSourceAsync(db,project.OrganizationId,project.Id,proof.ApprovedSource,ct);
        if (await db.CoreWorkTasks.AnyAsync(t=>t.Board!=null&&t.Board.WorkstreamId==project.Id&&t.ArchivedAt==null&&
            t.Status!=WorkTaskStatus.Completed&&t.Status!=WorkTaskStatus.Cancelled,ct))
            throw new InvalidOperationException("Complete or explicitly dispose the remaining project work before closure.");
    }

    internal static async Task RequireCurrentSourceAsync(CSweetDbContext db,Guid org,Guid project,WorkApprovedSource source,CancellationToken ct)
    {
        if (!await db.ArtifactRevisions.AsNoTracking().AnyAsync(r=>r.Id==source.RevisionId&&r.ArtifactId==source.DocumentId&&
            r.OrganizationId==org&&r.ContentSha256==source.Digest&&r.Status==ArtifactRevisionStatus.Accepted&&
            r.Artifact!.WorkstreamId==project&&r.Artifact.AcceptedRevisionId==r.Id&&r.Artifact.ArchivedAt==null,ct))
            throw new InvalidOperationException("The final product's approved source was changed, revoked or belongs to another project.");
    }
}
