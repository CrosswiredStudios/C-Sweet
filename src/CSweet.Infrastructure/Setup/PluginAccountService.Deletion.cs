using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Notifications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class PluginAccountService
{
    private static bool IsOrigin(PluginAccountDataCopy copy) => copy.Store is "consumer-origin" or "memory-origin";
    private async Task<IReadOnlyList<PluginDeletionView>> DeletionViews(Guid installationId,CancellationToken ct)
    {
        var rows=await db.PluginAccountDeletions.AsNoTracking().Where(x=>x.PluginInstallationId==installationId).OrderByDescending(x=>x.RequestedAt).Take(20).ToArrayAsync(ct);
        var result=new List<PluginDeletionView>();
        foreach(var row in rows) result.Add(new(row.AccountGroupId,row.Status,row.TargetAt,row.Deadline,row.LocalCompletedAt!=null,row.CompletedAt!=null,
            await db.PluginAccountDataCopies.CountAsync(x=>x.AccountGroupId==row.AccountGroupId && x.DeletedAt==null && x.Store.StartsWith("external:"),ct)));
        return result;
    }

    private async Task ProcessDeletions(CancellationToken ct)
    {
        foreach(var job in await db.PluginAccountDeletions.Where(x=>x.CompletedAt==null).OrderBy(x=>x.RequestedAt).Take(20).ToArrayAsync(ct))
        {
            // The account is already fenced off. Every operation below is repeatable after a crash.
            using var erasure=db.BeginProviderDataErasure();
            var group=await db.PluginAccountGroups.SingleAsync(x=>x.Id==job.AccountGroupId,ct);
            if(group.State!=PluginAccountState.Disconnecting) continue;
            job.Revision++;
            try { await db.SaveChangesAsync(ct); } catch(DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); continue; }
            var connectionIds=await db.PluginConnections.Where(x=>x.AccountGroupId==group.Id).Select(x=>x.Id).ToArrayAsync(ct);
            foreach(var execution in await db.ConnectorExecutions.Where(x=>connectionIds.Contains(x.ConnectionId) && x.Status!="Erased").ToArrayAsync(ct))
            {
                execution.PlanJson="{}"; execution.ResultJson=null; execution.ResourceId=""; execution.IdempotencyKey=$"erased-{execution.Id:N}";
                execution.InputHash=""; execution.PlanHash=""; execution.Status="Erased"; execution.Revision++;
                if(execution.ApprovalId is { } approval) await EraseRecord("ActionProposal",approval,ct);
            }
            foreach(var attempt in await db.PluginAccountSetupSessions.Where(x=>x.AccountGroupId==group.Id).ToArrayAsync(ct))
            {
                await secrets.RemoveAsync(job.PluginInstallationId,VerifierKey(attempt.Id),ct);
                if(attempt.RetiredCredentialKey is { } retired) await secrets.RemoveAsync(job.PluginInstallationId,retired,ct);
                attempt.ObservedResourcesJson="[]"; attempt.SeenPageTokensJson="[]"; attempt.PageToken=null;
                attempt.VerifiedSubject=null; attempt.VerifiedName=null; attempt.IdentityAuthority=null; attempt.RetiredCredentialKey=null;
                attempt.Status="Erased"; attempt.Revision++;
            }
            foreach(var resource in await db.PluginAccountResources.Where(x=>x.AccountGroupId==group.Id).ToArrayAsync(ct))
            { resource.DisplayName="Disconnected resource"; resource.Handle=null; resource.ExternalResourceId=$"erased-{resource.Id:N}"; resource.IsAvailable=false; resource.Revision++; }
            foreach(var connection in await db.PluginConnections.Where(x=>x.AccountGroupId==group.Id).ToArrayAsync(ct))
            { connection.ExternalAccountId=null; connection.ExternalAccountName=null; connection.BoundResourceId=null; connection.GrantedScopesJson="[]"; connection.NextInventoryCheckAt=null; }
            group.DisplayName="Disconnected account"; group.VerifiedSubject=null; group.IdentityAuthority=null; group.IdentityKind=PluginAccountIdentityKind.Administrative;
            var copies=await db.PluginAccountDataCopies.Where(x=>x.AccountGroupId==group.Id && x.DeletedAt==null).ToArrayAsync(ct);
            foreach(var copy in copies.Where(x=>!x.Store.StartsWith("external:") && !IsOrigin(x)))
                if(await EraseRecord(copy.Store,copy.RecordId,ct)) { copy.DeletedAt=DateTimeOffset.UtcNow; copy.Revision++; }
            // Unknown/new primary stores must block the primary receipt, not silently disappear
            // behind a completed timestamp while the overall job happens to remain pending.
            job.LocalCompletedAt=copies.Any(x=>x.DeletedAt==null && !IsOrigin(x) && !x.Store.StartsWith("external:"))
                ? null : job.LocalCompletedAt ?? DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            foreach(var copy in copies.Where(x=>x.Store.StartsWith("external:") && x.DeletedAt==null))
            {
                var participant=(deletionParticipants ?? []).SingleOrDefault(x=>x.Store==copy.Store);
                if(participant==null) continue;
                try
                {
                    if(await participant.DeleteAsync(group.Id,copy.OrganizationId,copy.ConsumerInstallationId,copy.RecordId,ct))
                    { copy.DeletedAt=DateTimeOffset.UtcNow; copy.Revision++; await db.SaveChangesAsync(ct); }
                }
                catch(HttpRequestException) { /* Durable retry, no processor response or private data in logs. */ }
            }
            var outstanding=copies.Any(x=>x.DeletedAt==null && !IsOrigin(x)) ||
                await db.PluginAccountRevocationJobs.AnyAsync(x=>x.AccountGroupId==group.Id && x.Status!="RevokedDataDeletionPending",ct);
            job.Status=outstanding ? (DateTimeOffset.UtcNow>=job.Deadline?"DeadlineExceeded":DateTimeOffset.UtcNow>=job.TargetAt?"TargetExceeded":"WaitingForConfirmation") : "Completed";
            if(outstanding && job.LocalCompletedAt==null && DateTimeOffset.UtcNow<job.TargetAt) job.Status="PrimaryReviewRequired";
            if(!outstanding)
            {
                foreach(var origin in copies.Where(IsOrigin)) { origin.DeletedAt=DateTimeOffset.UtcNow; origin.Revision++; }
                job.CompletedAt=DateTimeOffset.UtcNow; group.State=PluginAccountState.Disconnected; group.Revision++;
                await audit.WriteAsync("plugin-account.deletion.confirmed",nameof(PluginAccountDeletion),job.AccountGroupId,
                    "Host-owned data and every recorded external deletion obligation have independently confirmed cleanup. Provider revocation has a separate receipt.",cancellationToken:ct);
            }
            job.Revision++; await db.SaveChangesAsync(ct);
        }
    }

    private async Task<bool> EraseRecord(string store,Guid id,CancellationToken ct)
    {
        if(!CSweetDbContext.ProviderContentFields.TryGetValue(store,out var fields)) return false;
        var model=db.Model.GetEntityTypes().SingleOrDefault(x=>x.ClrType.Name==store);
        if(model==null) return false;
        var entity=await db.FindAsync(model.ClrType,[id],ct);
        if(entity==null) return true;
        if(entity is MemoryEpisodeEnrichmentJob) return false; // Only the native reviewed erasure transaction may remove accepted memory inputs.
        // These records participate in the platform's reviewed memory-erasure closure and
        // append-only evidence guards. Account cleanup must not bypass those guards, fabricate
        // their receipts, or certify primary completion before that existing workflow finishes.
        if(entity is AgentWorkItem protectedWork)
        {
            if(protectedWork.MemoryErasedAt!=null) return true;
            if(protectedWork.Status is AgentWorkStatus.Pending or AgentWorkStatus.Leased) protectedWork.Status=AgentWorkStatus.Cancelled;
            return false;
        }
        if(entity is ChatTurn protectedTurn)
        {
            if(protectedTurn.MemoryErasedAt!=null) return true;
            protectedTurn.Status=ChatTurnStatus.Cancelled;protectedTurn.LeaseOwner=null;protectedTurn.LeaseUntil=null;
            return false;
        }
        if(entity is AgentRunLog protectedLog) return protectedLog.MemoryErasedAt!=null;
        if(entity is PluginOperationalState state) { db.PluginOperationalStates.Remove(state); return true; }
        if(entity is Artifact artifact)
            foreach(var revision in await db.ArtifactRevisions.Where(x=>x.ArtifactId==artifact.Id).Select(x=>x.Id).ToArrayAsync(ct))
                await EraseRecord("ArtifactRevision",revision,ct);
        if(entity is AgentWorkAttempt attempt && await db.AgentWorkItems.AnyAsync(x=>x.Id==attempt.AgentWorkItemId && x.MemoryErasedAt!=null,ct)) return true;
        if(entity is AgentWorkProgress progress && await db.AgentWorkItems.AnyAsync(x=>x.Id==progress.AgentWorkItemId && x.MemoryErasedAt!=null,ct)) return true;
        if(entity is ChatTurnTraceEvent trace && await db.ChatTurns.AnyAsync(x=>x.Id==trace.ChatTurnId && x.MemoryErasedAt!=null,ct)) return true;
        if(entity is ConversationMessage message)
        {
            // Preserve captured/private context until the existing audience/hold-aware
            // erasure transaction has fenced extraction and written its immutable receipt.
            if(await db.MemoryCaptureOutbox.AnyAsync(j=>j.ConversationMessageId==message.Id,ct) &&
                (!await db.MemorySourceInvalidations.AnyAsync(i=>i.SourceMessageId==message.Id && i.ReasonCode==CSweet.Infrastructure.Core.MemoryCaptureErasure.FailureCode,ct) ||
                 !await db.MemoryErasureReceipts.AnyAsync(r=>r.EpisodeId==message.Id && db.CoreConversations.Any(c=>c.Id==message.ConversationId && c.OrganizationId==r.OrganizationId),ct))) return false;
            // Notification caches can contain the serialized message. Erase the scoped conversation's
            // cached event payloads, not unrelated human-authored primary messages or media assets.
            foreach(var cached in await db.CommunicationEventOutbox.Where(x=>x.ChatId==message.ConversationId).ToArrayAsync(ct))
            { cached.DataJson="{}"; cached.LastError="provider_data_erased"; cached.Status=CommunicationEventOutboxStatus.DeadLettered; }
            foreach(var cached in await db.ApplicationRealtimeOutbox.Where(x=>x.ChatId==message.ConversationId).ToArrayAsync(ct))
            { cached.DataJson="{}"; cached.LastError="provider_data_erased"; cached.Status=ApplicationRealtimeOutboxStatus.DeadLettered; }
        }
        var entry=db.Entry(entity);
        foreach(var field in fields.Where(f=>model.FindProperty(f)!=null))
        {
            var property=entry.Property(field); var type=property.Metadata.ClrType;
            property.CurrentValue=type==typeof(byte[])?Array.Empty<byte>():property.Metadata.IsNullable?null:field.EndsWith("Json",StringComparison.Ordinal)?"{}":"[Removed with connected account]";
        }
        if(entity is Artifact a) {a.ArchivedAt=DateTimeOffset.UtcNow;a.DocumentStatus=ArtifactDocumentStatus.Archived;}
        if(entity is WorkTask task) {task.ArchivedAt=DateTimeOffset.UtcNow;task.IsExecutable=false;task.Revision++;}
        if(entity is ConnectorExecution execution) { execution.IdempotencyKey=$"erased-{execution.Id:N}";execution.InputHash="";execution.PlanHash="";execution.Status="Erased"; execution.Revision++; }
        return true;
    }
}
