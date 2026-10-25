using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Confirm runtime destruction through the certified workload broker, never a plugin-supplied locator.</summary>
public sealed class PluginRuntimeDataDeletionParticipant(CSweetDbContext db,IAgentWorkloadRunner runner):IPluginAccountDataDeletionParticipant
{
    public string Store=>"external:runtime";
    public async Task<bool> DeleteAsync(Guid accountId,Guid? organizationId,Guid consumerInstallationId,Guid recordId,CancellationToken ct)
    {
        var copy=await db.PluginAccountDataCopies.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountGroupId==accountId && x.ConsumerInstallationId==consumerInstallationId &&
            x.RecordId==recordId && x.Store==Store && x.OrganizationId==organizationId,ct);
        if(copy==null || recordId!=consumerInstallationId) return false;
        if(copy.DeletedAt!=null) return true;
        if(!await db.PluginAccountGroups.AnyAsync(x=>x.Id==accountId && x.State==PluginAccountState.Disconnecting,ct)) return false;
        var deletion=await db.PluginAccountDeletions.AsNoTracking().SingleAsync(x=>x.AccountGroupId==accountId,ct);
        var instances=await db.AgentRuntimeInstances.Where(x=>x.AgentInstallationId==consumerInstallationId && x.QueuedAt<=deletion.RequestedAt).ToArrayAsync(ct);
        using var erasure=db.BeginProviderDataErasure();
        foreach(var instance in instances)
        {
            instance.MemoryResetRequestedAt ??=DateTimeOffset.UtcNow; instance.MemoryResetReasonCode="provider_data_erased"; instance.BrokerTokenHash="";
            await db.SaveChangesAsync(ct);
            if(instance.ProviderInstanceId is { } providerId)
            {
                if(instance.IsolationProviderId is not { } isolation) return false;
                var handle=new IsolationWorkloadHandle(isolation,instance.Id,providerId,WorkloadKind.Runtime);
                try
                {
                    if(await runner.InspectAsync(handle,ct)!=null) { await runner.StopAsync(handle,TimeSpan.FromSeconds(5),ct); await runner.DestroyAsync(handle,ct); }
                    if(await runner.InspectAsync(handle,ct)!=null) return false;
                }
                catch(Exception e) when(e is AgentWorkloadException or HttpRequestException) { return false; }
            }
            if(AgentRuntimeInstance.IsActive(instance.Status))
            { instance.TransitionTo(AgentRuntimeStatus.Stopping,DateTimeOffset.UtcNow,"provider_data_erased");instance.TransitionTo(AgentRuntimeStatus.Cancelled,DateTimeOffset.UtcNow,"provider_data_erased"); }
            instance.LogExcerpt=null;instance.MemoryResetCompletedAt=DateTimeOffset.UtcNow;
            foreach(var item in await db.AgentRuntimeEvents.Where(x=>x.AgentRuntimeInstanceId==instance.Id).ToArrayAsync(ct)) {item.PayloadJson=null;item.Reason="provider_data_erased";}
            await db.SaveChangesAsync(ct);
        }
        return true;
    }
}
