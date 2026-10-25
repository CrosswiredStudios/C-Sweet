using CSweet.Application.Setup;
using System.Security.Cryptography;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CSweet.Infrastructure.Setup;

/// <summary>Uses a fresh scope and the existing protected-work writer, not a ledger/immutability override.</summary>
public sealed class PluginRetainedEvidenceDeletionParticipant(IServiceScopeFactory scopes):IPluginAccountDataDeletionParticipant
{
    public string Store=>"external:retained-evidence";
    public async Task<bool> DeleteAsync(Guid accountId,Guid? organizationId,Guid consumerInstallationId,Guid recordId,CancellationToken ct)
    {
        if(organizationId is not {} org || recordId!=consumerInstallationId)return false;
        await using var scope=scopes.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<CSweetDbContext>();var protection=scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        return await EraseAsync(db,protection,accountId,org,consumerInstallationId,ct);
    }
    internal static async Task<bool> EraseAsync(CSweetDbContext db,IDataProtectionProvider protection,Guid accountId,Guid organization,Guid consumer,CancellationToken ct)
    {
        if(!db.Database.IsNpgsql() || db.ChangeTracker.HasChanges())return false;
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        using var erasure=db.BeginProviderDataErasure();
        using var work=new MemoryWorkErasure(db,protection,TimeProvider.System);
        try
        {
            await work.AcquireAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"PluginAccountGroups\" WHERE \"Id\"={accountId} FOR UPDATE NOWAIT",ct);
            if(!await db.PluginAccountGroups.AnyAsync(g=>g.Id==accountId && g.State==PluginAccountState.Disconnecting,ct) ||
                !await db.PluginAccountDataCopies.AnyAsync(c=>c.AccountGroupId==accountId && c.OrganizationId==organization && c.ConsumerInstallationId==consumer &&
                    c.Store=="external:retained-evidence" && c.RecordId==consumer,ct))return false;
            var cutoff=await db.PluginAccountDeletions.Where(d=>d.AccountGroupId==accountId && d.CompletedAt==null).Select(d=>(DateTimeOffset?)d.RequestedAt).SingleOrDefaultAsync(ct);
            if(cutoff==null)return false;
            var copies=await db.PluginAccountDataCopies.AsNoTracking().Where(c=>c.AccountGroupId==accountId && c.OrganizationId==organization && c.ConsumerInstallationId==consumer).Take(4097).ToArrayAsync(ct);
            if(copies.Length>4096 || copies.Any(c=>c.CreatedAt>cutoff))return false;
            if(!await PluginAuditRetentionGuard.VerifyAsync(db,protection,organization,copies,ct))return false;
            var ids=copies.Where(c=>c.Store==nameof(AgentWorkItem)).Select(c=>c.RecordId).ToArray();
            var rows=await db.AgentWorkItems.AsNoTracking().Where(w=>ids.Contains(w.Id)).ToArrayAsync(ct);
            // Personal/native inputs require the human's existing audience-and-hold review.
            // A company-account disconnect is not authority over unrelated private context.
            if(rows.Any(w=>w.OrganizationId!=organization.ToString("D") || w.AgentInstallationId!=consumer || w.MemoryErasedAt==null &&
                (w.MemoryRecallReceiptJson!=null || w.NativeWorkInputReceiptJson!=null || w.SourceType=="chat-turn")))return false;
            var plan=await work.PrepareAsync(organization,rows.Select(w=>w.Id).ToArray(),[],ct);
            if(plan.Works.Any(w=>w.InstallationId!=consumer || !ids.Contains(w.Id)) || plan.Runtimes.Any(r=>r.InstallationId!=consumer))return false;
            var runtimes=plan.Runtimes.Select(r=>r.Id).ToArray();
            if(await db.AgentRuntimeInstances.AnyAsync(r=>runtimes.Contains(r.Id) && r.QueuedAt>cutoff,ct))return false;
            var chats=copies.Where(c=>c.Store=="ChatTurn").Select(c=>c.RecordId).ToArray();
            if(await db.ChatTurns.AnyAsync(t=>chats.Contains(t.Id) && t.MemoryErasedAt==null,ct))return false;
            var models=copies.Where(c=>c.Store==nameof(AgentRunLog)).Select(c=>c.RecordId).Except(plan.ModelRuns).ToArray();
            if(await db.AgentRunLogs.AnyAsync(m=>models.Contains(m.Id) && m.MemoryErasedAt==null,ct))return false;
            await work.StageAsync(plan,ct);
            db.QueueAudit(new AuditEventWriteRequest("plugin-account.protected-evidence.erased","Plugin",OrganizationId:organization,
                EntityType:"PluginAccountDataCopy",EntityId:accountId,Summary:"Scoped generated work erased; immutable audit content omission verified.",UseAmbientOrganization:false));
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;
        }
        catch(Exception e) when(e is DbUpdateException or InvalidOperationException or UnauthorizedAccessException or PostgresException or CryptographicException)
        {db.ChangeTracker.Clear();return false;}
    }
}
