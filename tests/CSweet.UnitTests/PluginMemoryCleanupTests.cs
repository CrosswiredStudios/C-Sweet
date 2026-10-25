using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Setup;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using F=CSweet.UnitTests.PluginAccountFlowTests.Fixture;

namespace CSweet.UnitTests;

public sealed class PluginMemoryCleanupTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static MemoryErasureReceipt Receipt(F f,Guid source,DateTimeOffset created,object[]? runtimes=null)
    {
        var employee=Guid.NewGuid();var partition=new MemoryPartition(f.Business.ToString("D"));
        return new(){Id=Guid.NewGuid(),OrganizationId=f.Business,EmployeeId=employee,EpisodeId=source,OperationId=Guid.NewGuid(),
            ActorApplicationUserId=f.Admin,ActorOrganizationUserId=Guid.NewGuid(),RequestHash=new string('a',64),CreatedAt=created,
            InventoryJson=JsonSerializer.Serialize(new{audiences=new[]{partition},pendingRuntimes=runtimes ?? [],turnIds=Array.Empty<Guid>(),
                owners=new[]{new{employeeId=employee,partition}},ownershipVersion=1},Json)};
    }
    private static async Task<(Guid Account,Guid Source,DateTimeOffset Written)> Pending(F f)
    {
        var begin=await f.Begin();await f.Complete(begin);await f.Service.ImportAsync(begin.SetupId,default);
        var account=(await f.Service.GetAsync(f.Admin,f.Id,default)).Accounts.Single();
        var source=Guid.NewGuid();var written=DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach(var store in new[]{"external:memory","memory-origin"})
            f.Inner.Db.PluginAccountDataCopies.Add(new(){Id=Guid.NewGuid(),AccountGroupId=account.Id,OrganizationId=f.Business,
                ConsumerInstallationId=f.Inner.Requester.Id,Store=store,RecordId=store=="memory-origin"?source:f.Inner.Requester.Id,CreatedAt=written});
        await f.Inner.Db.SaveChangesAsync();var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);
        await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);
        return(account.Id,source,written);
    }
    private static Task<bool> Confirm(F f,Guid account)=>new PluginMemoryDeletionParticipant(f.Inner.Db).DeleteAsync(account,f.Business,f.Inner.Requester.Id,f.Inner.Requester.Id,default);

    [Fact]
    public async Task NativeReceiptConfirmsRecordedSourceAndSurvivesAWorkerRestart()
    {
        await using var f=await F.Create();var p=await Pending(f);
        Assert.False(await Confirm(f,p.Account));
        f.Inner.Db.MemoryErasureReceipts.Add(Receipt(f,p.Source,DateTimeOffset.UtcNow));await f.Inner.Db.SaveChangesAsync();
        Assert.True(await Confirm(f,p.Account));Assert.True(await Confirm(f,p.Account));
        f.DeletionParticipants.Add(new PluginMemoryDeletionParticipant(f.Inner.Db));await f.Service.ProcessPendingAsync(default);
        Assert.NotNull((await f.Inner.Db.PluginAccountDataCopies.SingleAsync(c=>c.Store=="external:memory")).DeletedAt);
        Assert.Null((await f.Inner.Db.PluginAccountDeletions.SingleAsync()).CompletedAt); // Backups still need their own proof.
    }
    [Theory]
    [InlineData("other-tenant")][InlineData("other-source")][InlineData("stale")][InlineData("legacy")]
    [InlineData("malformed")][InlineData("duplicate")][InlineData("future")][InlineData("missing-reset")]
    public async Task UnrelatedStaleMalformedOrIncompleteEvidenceNeverConfirms(string failure)
    {
        await using var f=await F.Create();var p=await Pending(f);
        var receipt=Receipt(f,p.Source,DateTimeOffset.UtcNow);
        switch(failure)
        {
            case "other-tenant":receipt.OrganizationId=Guid.NewGuid();break;
            case "other-source":receipt.EpisodeId=Guid.NewGuid();break;
            case "stale":receipt.CreatedAt=p.Written.AddSeconds(-1);break;
            case "legacy":receipt.InventoryJson=receipt.InventoryJson.Replace("\"ownershipVersion\":1","\"ownershipVersion\":0");break;
            case "malformed":receipt.InventoryJson="{}";break;
            case "duplicate":receipt.InventoryJson=receipt.InventoryJson.Replace("\"ownershipVersion\":1","\"ownershipVersion\":0,\"ownershipVersion\":1");break;
            case "future":receipt.CreatedAt=DateTimeOffset.UtcNow.AddDays(1);break;
            case "missing-reset":receipt=Receipt(f,p.Source,DateTimeOffset.UtcNow,[new{id=Guid.NewGuid(),requestedAt=p.Written}]);break;
        }
        f.Inner.Db.MemoryErasureReceipts.Add(receipt);await f.Inner.Db.SaveChangesAsync();Assert.False(await Confirm(f,p.Account));
    }
    [Fact]
    public async Task QueuedGenericInputStillRequiresNativeErasureEvenWhenAReceiptExists()
    {
        await using var f=await F.Create();var p=await Pending(f);
        // Synthetic authoritative rows model an uncompleted queue erasure; no new content is written after disconnect.
        using(var scope=f.Inner.Db.BeginProviderDataErasure())
        {
            f.Inner.Db.MemoryEpisodeEnrichmentJobs.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Business,InstallationId=f.Inner.Requester.Id,
                EmployeeId=Guid.NewGuid(),EpisodeId=p.Source,SourceJson="{}",SourceHash="test"});
            f.Inner.Db.MemoryErasureReceipts.Add(Receipt(f,p.Source,DateTimeOffset.UtcNow));await f.Inner.Db.SaveChangesAsync();
        }
        Assert.False(await Confirm(f,p.Account));
    }
    [Fact]
    public async Task ConfirmationRequiresExactRecordedAccountOrganizationAndConsumer()
    {
        await using var f=await F.Create();var p=await Pending(f);f.Inner.Db.MemoryErasureReceipts.Add(Receipt(f,p.Source,DateTimeOffset.UtcNow));await f.Inner.Db.SaveChangesAsync();
        var adapter=new PluginMemoryDeletionParticipant(f.Inner.Db);
        Assert.False(await adapter.DeleteAsync(p.Account,Guid.NewGuid(),f.Inner.Requester.Id,f.Inner.Requester.Id,default));
        Assert.False(await adapter.DeleteAsync(p.Account,f.Business,f.Inner.Requester.Id,Guid.NewGuid(),default));
        Assert.False(await adapter.DeleteAsync(Guid.NewGuid(),f.Business,f.Inner.Requester.Id,f.Inner.Requester.Id,default));
        Assert.True(await Confirm(f,p.Account));
    }

    [Fact]
    public async Task AReviewedRootReceiptCanConfirmAnExactlyRecordedErasedDescendant()
    {
        await using var f=await F.Create();var p=await Pending(f);var receipt=Receipt(f,Guid.NewGuid(),DateTimeOffset.UtcNow);
        var inventory=System.Text.Json.Nodes.JsonNode.Parse(receipt.InventoryJson)!;
        inventory["erasedEpisodes"]=JsonSerializer.SerializeToNode(new[]{new{id=p.Source,partition=new MemoryPartition(f.Business.ToString("D"))}},Json);
        receipt.InventoryJson=inventory.ToJsonString();f.Inner.Db.MemoryErasureReceipts.Add(receipt);await f.Inner.Db.SaveChangesAsync();
        Assert.True(await Confirm(f,p.Account));
    }

    [Fact]
    public async Task RuntimeResetMustActuallyCompleteBeforeTheReceiptConfirms()
    {
        await using var f=await F.Create();var p=await Pending(f);var requested=p.Written;
        var runtime=new AgentRuntimeInstance{Id=Guid.NewGuid(),AgentInstallationId=f.Inner.Requester.Id,QueuedAt=p.Written};
        f.Inner.Db.AgentRuntimeInstances.Add(runtime);
        f.Inner.Db.MemoryErasureReceipts.Add(Receipt(f,p.Source,DateTimeOffset.UtcNow,[new{id=runtime.Id,requestedAt=requested}]));await f.Inner.Db.SaveChangesAsync();
        Assert.False(await Confirm(f,p.Account));
        runtime.TransitionTo(AgentRuntimeStatus.Cancelled,DateTimeOffset.UtcNow);runtime.MemoryResetCompletedAt=requested.AddSeconds(-1);await f.Inner.Db.SaveChangesAsync();
        Assert.False(await Confirm(f,p.Account));
        runtime.MemoryResetCompletedAt=DateTimeOffset.UtcNow;await f.Inner.Db.SaveChangesAsync();Assert.True(await Confirm(f,p.Account));
    }

    [Fact]
    public async Task MissingMessageAndCaptureRowsAreNotProofOfMemoryErasure()
    {
        await using var f=await F.Create();var p=await Pending(f);
        var source=await f.Inner.Db.PluginAccountDataCopies.SingleAsync(c=>c.Store=="memory-origin");source.Store=nameof(ConversationMessage);await f.Inner.Db.SaveChangesAsync();
        Assert.False(await Confirm(f,p.Account));
        f.Inner.Db.MemoryErasureReceipts.Add(Receipt(f,p.Source,DateTimeOffset.UtcNow));await f.Inner.Db.SaveChangesAsync();Assert.True(await Confirm(f,p.Account));
    }

    [Fact]
    public async Task GenericMemoryJobsGetDurableSourceLineageBeforeTheirInputsCanBeErased()
    {
        await using var f=await F.Create();var begin=await f.Begin();await f.Complete(begin);await f.Service.ImportAsync(begin.SetupId,default);
        var account=(await f.Service.GetAsync(f.Admin,f.Id,default)).Accounts.Single();
        await f.Service.SaveGrantAsync(f.Admin,f.Id,account.Id,new(f.Business,"SelectedResources",[account.Resources[0].Id],["read"],false,null,f.Digest),default);
        var grant=await f.Inner.Db.PluginAccountBusinessGrants.SingleAsync();
        await f.Service.BindBusinessResourceAsync(f.Manager,f.Business,f.Id,new(f.Inner.Requester.Id,"account",account.Resources[0].Id,account.Authorizations.Single().Id,grant.Revision,f.Digest),default);
        var job=new MemoryEpisodeEnrichmentJob{Id=Guid.NewGuid(),OrganizationId=f.Business,EmployeeId=Guid.NewGuid(),InstallationId=f.Inner.Requester.Id,
            EpisodeId=Guid.NewGuid(),SourceJson="{\"private\":true}"};
        f.Inner.Db.MemoryEpisodeEnrichmentJobs.Add(job);await f.Inner.Db.SaveChangesAsync();
        Assert.Contains(await f.Inner.Db.PluginAccountDataCopies.ToArrayAsync(),c=>c.Store=="memory-origin" && c.RecordId==job.EpisodeId && c.AccountGroupId==account.Id);
        Assert.Contains(await f.Inner.Db.PluginAccountDataCopies.ToArrayAsync(),c=>c.Store=="external:memory" && c.ConsumerInstallationId==f.Inner.Requester.Id);
        var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);
        await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);
        job.AcceptedExtractionJson="{\"late\":true}";
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Inner.Db.SaveChangesAsync());
    }
}
