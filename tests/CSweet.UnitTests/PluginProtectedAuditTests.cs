using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using F=CSweet.UnitTests.PluginAccountFlowTests.Fixture;
using CSweet.Domain.Core;

namespace CSweet.UnitTests;

public sealed class PluginProtectedAuditTests
{
    [Fact]
    public async Task ProviderContentIsOmittedBeforeItEntersImmutableAuditEvidence()
    {
        await using var f=await F.Create();var begin=await f.Begin();await f.Complete(begin);await f.Service.ImportAsync(begin.SetupId,default);
        var account=(await f.Service.GetAsync(f.Admin,f.Id,default)).Accounts.Single();
        await f.Service.SaveGrantAsync(f.Admin,f.Id,account.Id,new(f.Business,"SelectedResources",[account.Resources[0].Id],["read"],false,null,f.Digest),default);
        var grant=await f.Inner.Db.PluginAccountBusinessGrants.SingleAsync();
        await f.Service.BindBusinessResourceAsync(f.Manager,f.Business,f.Id,new(f.Inner.Requester.Id,"account",account.Resources[0].Id,account.Authorizations.Single().Id,grant.Revision,f.Digest),default);
        var work=new AgentWorkItem{Id=Guid.NewGuid(),OrganizationId=f.Business.ToString("D"),AgentInstallationId=f.Inner.Requester.Id,Name="provider-private-title",
            LastError="provider-private-error",IdempotencyKey="provider-private-key",CorrelationId="provider-private-correlation",ProtectedPayload=[1,2,3]};
        f.Inner.Db.AgentWorkItems.Add(work);await f.Inner.Db.SaveChangesAsync();
        var pending=await f.Inner.Db.AuditOutbox.SingleAsync(x=>x.SourceEntityId==work.Id);
        Assert.DoesNotContain("provider-private",pending.RequestJson);
        var request=JsonSerializer.Deserialize<AuditEventWriteRequest>(pending.RequestJson)!;
        Assert.True(PluginAuditRetentionGuard.ContentFree(request.Payload!.Value));
        var sources=await f.Inner.Db.PluginAccountDataCopies.Where(c=>c.RecordId==work.Id).ToArrayAsync();
        Assert.True(await PluginAuditRetentionGuard.VerifyAsync(f.Inner.Db,new EphemeralDataProtectionProvider(),f.Business,sources,default));
        // A legacy content-bearing immutable copy cannot be relabelled or silently deleted.
        f.Inner.Db.AuditEvents.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Business,EntityType=nameof(AgentWorkItem),EntityId=work.Id,
            EventType="agent.work.pending",Summary="provider-private-title",PayloadPreview="{\"text\":\"private\"}"});
        await f.Inner.Db.SaveChangesAsync();
        Assert.False(await PluginAuditRetentionGuard.VerifyAsync(f.Inner.Db,new EphemeralDataProtectionProvider(),f.Business,sources,default));
        Assert.Contains(await f.Inner.Db.AuditEvents.ToArrayAsync(),a=>a.Summary=="provider-private-title");
    }
    [Theory]
    [InlineData("{\"source\":{\"Title\":\"private\"},\"evidence\":{\"contentPolicy\":\"memory-content-omitted-v1\"},\"historical\":false,\"historyNotice\":null}")]
    [InlineData("{\"source\":{},\"evidence\":{\"contentPolicy\":\"memory-content-omitted-v1\",\"text\":\"private\"},\"historical\":false,\"historyNotice\":null}")]
    [InlineData("{\"source\":{},\"source\":{},\"evidence\":{\"contentPolicy\":\"memory-content-omitted-v1\"},\"historical\":false,\"historyNotice\":null}")]
    public void ContentFreeLabelCannotHideUnreviewedFieldsOrDuplicateKeys(string json)=>Assert.False(PluginAuditRetentionGuard.ContentFree(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task CapturedMessageIsPreservedAndReviewIsOfferedOnlyToItsOriginalAuthorizedHuman()
    {
        await using var f=await F.Create();var begin=await f.Begin();await f.Complete(begin);await f.Service.ImportAsync(begin.SetupId,default);
        var account=(await f.Service.GetAsync(f.Admin,f.Id,default)).Accounts.Single();
        // A trusted recorded source, not a caller-submitted deletion target.
        var human=new OrganizationUser{Id=Guid.NewGuid(),OrganizationId=f.Business,ApplicationUserId=f.Admin,EmployeeType=EmployeeType.Human};
        var agent=new OrganizationUser{Id=Guid.NewGuid(),OrganizationId=f.Business,AgentInstallationId=f.Inner.Requester.Id,EmployeeType=EmployeeType.Agent};
        var conversation=new Conversation{Id=Guid.NewGuid(),OrganizationId=f.Business,InitiatedByOrganizationUserId=human.Id,AgentOrganizationUserId=agent.Id};
        var message=new ConversationMessage{Id=Guid.NewGuid(),ConversationId=conversation.Id,Role=ConversationRole.Assistant,SenderOrganizationUserId=agent.Id,Content="retained-provider-context"};
        f.Inner.Db.CoreOrganizationUsers.AddRange(human,agent);f.Inner.Db.CoreConversations.Add(conversation);f.Inner.Db.CoreConversationMessages.Add(message);
        f.Inner.Db.PluginAccountDataCopies.Add(new(){Id=Guid.NewGuid(),AccountGroupId=account.Id,OrganizationId=f.Business,ConsumerInstallationId=f.Inner.Requester.Id,Store=nameof(ConversationMessage),RecordId=message.Id,CreatedAt=DateTimeOffset.UtcNow});
        f.Inner.Db.MemoryCaptureOutbox.Add(new(){Id=Guid.NewGuid(),ConversationMessageId=message.Id,EpisodeCapturedAt=DateTimeOffset.UtcNow});await f.Inner.Db.SaveChangesAsync();
        await f.Service.SaveOperatorAsync(f.Admin,f.Id,new(f.Manager,account.Id,null,f.Digest),default);
        var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);
        await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);await f.Service.ProcessPendingAsync(default);
        Assert.Equal("retained-provider-context",message.Content);Assert.Null((await f.Inner.Db.PluginAccountDeletions.SingleAsync()).LocalCompletedAt);
        Assert.Single((await f.Service.GetAsync(f.Admin,f.Id,default)).CleanupReviews,r=>r.EpisodeId==message.Id);
        human.ApplicationUserId=f.Manager;await f.Inner.Db.SaveChangesAsync();
        Assert.Empty((await f.Service.GetAsync(f.Admin,f.Id,default)).CleanupReviews);
        var operatorView=await f.Service.GetAsync(f.Manager,f.Id,default);
        Assert.False(operatorView.CanAdminister);Assert.Single(operatorView.CleanupReviews,r=>r.EpisodeId==message.Id);
    }
    [Fact]
    public async Task ModelStreamEvidenceUsesExistingContentOmissionValidatorAndRejectsTextChunks()
    {
        await using var f=await F.Create();var model=new AgentRunLog{Id=Guid.NewGuid(),OrganizationId=f.Business,AgentInstallationId=f.Inner.Requester.Id,OmitMemoryAuditContent=true};
        var copy=new PluginAccountDataCopy{Id=Guid.NewGuid(),AccountGroupId=Guid.NewGuid(),OrganizationId=f.Business,ConsumerInstallationId=f.Inner.Requester.Id,Store=nameof(AgentRunLog),RecordId=model.Id};
        f.Inner.Db.PluginAccountDataCopies.Add(copy);f.Inner.Db.AgentRunLogs.Add(model);await f.Inner.Db.SaveChangesAsync();
        void Chunk(string body)=>f.Inner.Db.QueueAudit(new AuditEventWriteRequest("model.response.chunk","Model",OrganizationId:f.Business,
            EntityType:nameof(AgentRunLog),EntityId:model.Id,Payload:Encoding.UTF8.GetBytes(body),UseAmbientOrganization:false));
        Chunk("{\"sequence\":1,\"contentPolicy\":\"memory-content-omitted-v1\",\"role\":\"assistant\",\"finishReason\":\"stop\"}");await f.Inner.Db.SaveChangesAsync();
        Assert.True(await PluginAuditRetentionGuard.VerifyAsync(f.Inner.Db,new EphemeralDataProtectionProvider(),f.Business,[copy],default));
        Chunk("{\"sequence\":2,\"contentPolicy\":\"memory-content-omitted-v1\",\"text\":\"private\"}");await f.Inner.Db.SaveChangesAsync();
        Assert.False(await PluginAuditRetentionGuard.VerifyAsync(f.Inner.Db,new EphemeralDataProtectionProvider(),f.Business,[copy],default));
    }
}
