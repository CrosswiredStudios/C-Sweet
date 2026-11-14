using System.Data;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Record verified delivery as a fact under an explicit grant; no scope change, lifecycle-stage jump or public launch.</summary>
public sealed class WorkstreamDeliveryCompletionService(CSweetDbContext db,TimeProvider clock)
{
    public async Task<CSweet.Agent.SDK.MutationResponse> CompleteAsync(Guid org,Guid actorId,CompleteWorkstreamDeliveryRequest request,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(request.IdempotencyKey)||request.IdempotencyKey.Length>200)
            throw new ArgumentException("Provide a bounded completion command key.");
        await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct):null;
        await new ProjectWorkPolicy(db,clock).LockAsync(org,ct);
        var project=await db.Workstreams.SingleAsync(p=>p.Id==request.WorkstreamId&&p.OrganizationId==org,ct);
        if(project.AccountableManagerOrganizationUserId!=actorId||!await db.CoreOrganizationUsers.AnyAsync(p=>p.Id==actorId&&p.OrganizationId==org&&p.IsActive&&p.ArchivedAt==null,ct)||
            !await db.ProjectParticipants.AnyAsync(p=>p.WorkstreamId==project.Id&&p.OrganizationId==org&&p.OrganizationUserId==actorId&&p.RemovedAt==null,ct))
            throw new UnauthorizedAccessException("Only the current active accountable manager records verified project delivery.");
        var digest=WorkDeliveryService.Digest(request);
        var replay=await db.WorkDeliveryMutationReceipts.AsNoTracking().SingleOrDefaultAsync(r=>r.OrganizationId==org&&r.IdempotencyKey==request.IdempotencyKey,ct);
        if(replay is not null) {
            if(replay.ActorId!=actorId||replay.Operation!="project-completion"||replay.PlanId!=request.Evidence.PlanId||replay.RequestDigest!=digest)
                throw new InvalidOperationException("The completion key belongs to a different command.");
            return new(true,project.Revision,null,"Verified delivery was already recorded.");
        }
        if(project.Revision!=request.ExpectedRevision)throw new DbUpdateConcurrencyException("The project changed before delivery completion.");
        if(project.Status is WorkstreamStatus.Cancelled or WorkstreamStatus.Completed)
            throw new InvalidOperationException("A terminal project cannot be reopened or completed by another delivery command.");
        await WorkstreamDeliveryCompletionValidator.ValidateAsync(db,project,request.Evidence,ct);
        var now=clock.GetUtcNow();project.Status=WorkstreamStatus.Completed;project.Revision++;project.UpdatedAt=now;
        db.ProjectManagerReservations.RemoveRange(await db.ProjectManagerReservations.Where(r=>r.WorkstreamId==project.Id).ToListAsync(ct));
        db.WorkDeliveryMutationReceipts.Add(new(){Id=Guid.NewGuid(),OrganizationId=org,ActorId=actorId,PlanId=request.Evidence.PlanId,
            Operation="project-completion",IdempotencyKey=request.IdempotencyKey,RequestDigest=digest,CreatedAt=now});
        var changed=new GenericResourceEvent(Guid.NewGuid(),now,new(org,project.Id,null,null,null,null,null,request.Evidence.PlanId,null,null),
            "Workstream",project.Id,project.Revision,project.ProfileKey??"workstream","delivery.completed",JsonSerializer.SerializeToElement(request.Evidence,WorkDeliveryService.Json));
        db.AgentPlatformEventOutbox.Add(new(){Id=Guid.NewGuid(),OrganizationId=org,EventType=WorkstreamEventNames.ChangedV2,
            DataJson=JsonSerializer.Serialize(changed,WorkDeliveryService.Json),IdempotencyKey=$"delivery-completed:{project.Id:N}:{project.Revision}",
            Status=AgentPlatformEventOutboxStatus.Pending,OccurredAt=now,NextAttemptAt=now});
        await db.SaveChangesAsync(ct);if(tx is not null)await tx.CommitAsync(ct);
        return new(true,project.Revision,null,"Verified project delivery recorded. Public launch was not authorized or performed.");
    }
}
