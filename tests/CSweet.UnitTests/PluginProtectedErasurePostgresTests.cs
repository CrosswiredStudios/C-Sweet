using System.Text;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData(false)][InlineData(true)]
    public async Task PluginEvidenceCleanupUsesExistingWorkTombstonesAndRefusesPrivateNativeContext(bool privateContext)
    {
        await using var fixture=await DurabilityFixture.CreateAsync(postgres:true);
        var protection=new EphemeralDataProtectionProvider();var accountId=Guid.NewGuid();var workId=Guid.NewGuid();
        await using(var seed=fixture.Context())
        {
            await seed.Database.ExecuteSqlRawAsync(WorkMemoryErasure.InstallTriggers);
            using var cleanup=seed.BeginProviderDataErasure();
            seed.PluginAccountGroups.Add(new(){Id=accountId,PluginInstallationId=fixture.InstallationId,State=PluginAccountState.Disconnecting});
            seed.PluginAccountDeletions.Add(new(){AccountGroupId=accountId,PluginInstallationId=fixture.InstallationId,RequestedAt=DateTimeOffset.UtcNow});
            foreach(var (store,id) in new[]{(nameof(AgentWorkItem),workId),("external:retained-evidence",fixture.InstallationId),("consumer-origin",fixture.InstallationId)})
                seed.PluginAccountDataCopies.Add(new(){Id=Guid.NewGuid(),AccountGroupId=accountId,OrganizationId=fixture.OrganizationId,
                    ConsumerInstallationId=fixture.InstallationId,Store=store,RecordId=id,CreatedAt=DateTimeOffset.UtcNow.AddMinutes(-1)});
            seed.AgentWorkItems.Add(new(){Id=workId,AgentInstallationId=fixture.InstallationId,OrganizationId=fixture.OrganizationId.ToString("D"),
                Name="provider-specific secret",IdempotencyKey="private-work-key",ProtectedPayload=protection.CreateProtector("CSweet.AgentWorkInbox.v1").Protect("{\"provider\":\"private\"}"u8.ToArray()),
                NativeWorkInputReceiptJson=privateContext?"{}":null});
            await seed.SaveChangesAsync();
        }
        await using(var db=fixture.Context())
            Assert.Equal(!privateContext,await PluginRetainedEvidenceDeletionParticipant.EraseAsync(db,protection,accountId,fixture.OrganizationId,fixture.InstallationId,default));
        await using var check=fixture.Context();var work=await check.AgentWorkItems.SingleAsync(x=>x.Id==workId);
        if(privateContext){Assert.Null(work.MemoryErasedAt);Assert.Equal("provider-specific secret",work.Name);}
        else
        {
            Assert.NotNull(work.MemoryErasedAt);Assert.Equal("{}",Encoding.UTF8.GetString(protection.CreateProtector("CSweet.AgentWorkInbox.v1").Unprotect(work.ProtectedPayload)));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>check.AgentWorkItems.Where(x=>x.Id==workId).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ProtectedPayload,new byte[]{1,2,3})));
        }
    }
}
