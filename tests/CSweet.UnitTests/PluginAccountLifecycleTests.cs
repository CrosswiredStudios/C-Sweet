using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;
using F=CSweet.UnitTests.PluginAccountFlowTests.Fixture;

namespace CSweet.UnitTests;

public sealed class PluginAccountLifecycleTests
{
    private static async Task<PluginManagedAccount> Connect(F f)
    { var begin=await f.Begin();await f.Complete(begin);await f.Service.ImportAsync(begin.SetupId,default);return (await f.Service.GetAsync(f.Admin,f.Id,default)).Accounts.Single(); }
    private static async Task Bind(F f,PluginManagedAccount account)
    {
        await f.Service.SaveGrantAsync(f.Admin,f.Id,account.Id,new(f.Business,"SelectedResources",[account.Resources[0].Id],["read"],false,null,f.Digest),default);
        var grant=await f.Inner.Db.PluginAccountBusinessGrants.SingleAsync();
        await f.Service.BindBusinessResourceAsync(f.Manager,f.Business,f.Id,new(f.Inner.Requester.Id,"account",account.Resources[0].Id,account.Authorizations.Single().Id,grant.Revision,f.Digest),default);
    }
    [Fact]
    public async Task AdministratorCanAssignOperatorBeforeFirstConnectionButNotBusinessAuthority()
    {
        await using var f=await F.Create();
        await f.Service.SaveOperatorAsync(f.Admin,f.Id,new(f.Manager,null,null,f.Digest),default);
        var begin=await f.Service.BeginAsync(f.Manager,f.Id,new(["base"],"v1",true,f.Digest),default);
        Assert.NotEqual(Guid.Empty,begin.SetupId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.SaveGrantAsync(f.Manager,f.Id,Guid.NewGuid(),new(f.Business,"WholeAccount",[],["read"],true,null,f.Digest),default));
        Assert.Empty(await f.Inner.Db.PluginAccountBusinessGrants.ToArrayAsync());
    }
    [Fact]
    public async Task AssignedOperatorCannotConnectAnotherAccountAndRevocationStopsPendingConsent()
    {
        await using var f=await F.Create();var account=await Connect(f);
        await f.Service.SaveOperatorAsync(f.Admin,f.Id,new(f.Manager,account.Id,null,f.Digest),default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.BeginAsync(f.Manager,f.Id,new(["base"],"v1",true,f.Digest),default));
        var begin=await f.Service.BeginAsync(f.Manager,f.Id,new(["base"],"v1",true,f.Digest,account.Authorizations.Single().Id),default);
        var op=await f.Inner.Db.PluginAccountOperators.SingleAsync();
        await f.Service.RevokeOperatorAsync(f.Admin,f.Id,op.Id,op.Revision,default);
        var state=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(begin.AuthorizationUrl).Query)["state"]!;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.CompleteAsync(f.Manager,"code",state!,null,default));
        Assert.Equal(PluginConnectionStatus.Connected,(await f.Inner.Db.PluginConnections.SingleAsync(x=>x.Id==account.Authorizations[0].Id)).Status);
    }
    [Fact]
    public async Task RegrantRequiresHistoricalRevisionAndCurrentPackage()
    {
        await using var f=await F.Create();await f.Service.SaveOperatorAsync(f.Admin,f.Id,new(f.Manager,null,null,f.Digest),default);
        var op=await f.Inner.Db.PluginAccountOperators.SingleAsync();await f.Service.RevokeOperatorAsync(f.Admin,f.Id,op.Id,op.Revision,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.SaveOperatorAsync(f.Admin,f.Id,new(f.Manager,null,null,f.Digest),default));
        await f.Service.SaveOperatorAsync(f.Admin,f.Id,new(f.Manager,null,op.Revision,f.Digest),default);
        Assert.Single((await f.Service.GetAsync(f.Admin,f.Id,default)).Administration!.Operators.Where(x=>!x.Revoked));
    }
    [Fact]
    public async Task MaintenanceIsSystemOwnedAndRemovesUnseenResourcesOnlyAfterCompleteScan()
    {
        await using var f=await F.Create();var account=await Connect(f);
        var connection=await f.Inner.Db.PluginConnections.SingleAsync(x=>x.Id==account.Authorizations[0].Id);
        connection.NextInventoryCheckAt=DateTimeOffset.UtcNow.AddMinutes(-1);await f.Inner.Db.SaveChangesAsync();
        f.Transport.OmitSecondResource=true;f.Transport.FailSecondPage=true;
        await f.Service.ProcessPendingAsync(default);
        Assert.All(await f.Inner.Db.PluginAccountResources.ToArrayAsync(),x=>Assert.True(x.IsAvailable));
        var maintenance=await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x=>x.IsMaintenance);Assert.Null(maintenance.ApplicationUserId);Assert.Equal("ImportFailed",maintenance.Status);
        f.Transport.FailSecondPage=false;connection=await f.Inner.Db.PluginConnections.SingleAsync(x=>x.Id==connection.Id);connection.NextInventoryCheckAt=DateTimeOffset.UtcNow.AddMinutes(-1);await f.Inner.Db.SaveChangesAsync();
        await f.Service.ProcessPendingAsync(default);
        Assert.False((await f.Inner.Db.PluginAccountResources.SingleAsync(x=>x.ExternalResourceId=="workspace-b")).IsAvailable);
        Assert.Single(await f.Inner.Db.PluginAccountSetupSessions.Where(x=>x.IsMaintenance).ToArrayAsync());
    }
    [Fact]
    public async Task MaintenanceIdentityChangeRequiresReauthorizationInsteadOfRetargeting()
    {
        await using var f=await F.Create();var account=await Connect(f);
        var connection=await f.Inner.Db.PluginConnections.SingleAsync(x=>x.Id==account.Authorizations[0].Id);
        connection.NextInventoryCheckAt=DateTimeOffset.UtcNow.AddMinutes(-1);await f.Inner.Db.SaveChangesAsync();f.Transport.Subject="a-different-principal";
        await f.Service.ProcessPendingAsync(default);
        Assert.Equal(PluginConnectionStatus.ReauthorizationRequired,(await f.Inner.Db.PluginConnections.AsNoTracking().SingleAsync(x=>x.Id==connection.Id)).Status);
        Assert.NotEqual("a-different-principal",(await f.Inner.Db.PluginAccountGroups.SingleAsync()).VerifiedSubject);
    }
    [Fact]
    public async Task DisconnectErasesScopedPrimaryCopiesButCannotSelfAttestExternalDeletion()
    {
        await using var f=await F.Create();var account=await Connect(f);await Bind(f,account);
        var state=new PluginOperationalState{Id=Guid.NewGuid(),OrganizationId=f.Business,AgentInstallationId=f.Inner.Requester.Id,Kind="agent-operating-state",ExternalKey="draft",PayloadJson="{\"comment\":\"private provider text\"}"};
        var unrelated=new PluginOperationalState{Id=Guid.NewGuid(),OrganizationId=Guid.NewGuid(),AgentInstallationId=Guid.NewGuid(),Kind="agent-operating-state",ExternalKey="unrelated",PayloadJson="{\"keep\":true}"};
        f.Inner.Db.PluginOperationalStates.AddRange(state,unrelated);await f.Inner.Db.SaveChangesAsync();
        Assert.Contains(await f.Inner.Db.PluginAccountDataCopies.ToArrayAsync(),x=>x.RecordId==state.Id && x.AccountGroupId==account.Id);
        var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);
        await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);
        await f.Service.ProcessPendingAsync(default);
        Assert.False(await f.Inner.Db.PluginOperationalStates.AnyAsync(x=>x.Id==state.Id));Assert.True(await f.Inner.Db.PluginOperationalStates.AnyAsync(x=>x.Id==unrelated.Id));
        var deletion=await f.Inner.Db.PluginAccountDeletions.SingleAsync();Assert.NotNull(deletion.LocalCompletedAt);Assert.Null(deletion.CompletedAt);Assert.Equal("WaitingForConfirmation",deletion.Status);
        foreach(var store in new[]{"external:backups","external:runtime","external:processors","external:retained-evidence"}) f.DeletionParticipants.Add(new ConfirmedParticipant(store));
        await f.Service.ProcessPendingAsync(default);Assert.NotNull(deletion.CompletedAt);Assert.Equal(PluginAccountState.Disconnected,(await f.Inner.Db.PluginAccountGroups.SingleAsync()).State);
    }
    [Fact]
    public async Task MissedDeletionDeadlineIsVisibleAndNeverRecordedAsSuccess()
    {
        await using var f=await F.Create();var account=await Connect(f);var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);
        await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);
        var deletion=await f.Inner.Db.PluginAccountDeletions.SingleAsync();deletion.Deadline=DateTimeOffset.UtcNow.AddHours(-1);deletion.TargetAt=deletion.Deadline;await f.Inner.Db.SaveChangesAsync();
        await f.Service.ProcessPendingAsync(default);Assert.Equal("DeadlineExceeded",deletion.Status);Assert.Null(deletion.CompletedAt);
    }
    [Fact]
    public async Task PauseAndResumeReviewCurrentImpactAndDoNotDisconnectSharedCredentials()
    {
        await using var f=await F.Create();var account=await Connect(f);
        var impact=await f.Service.GetInstallationImpactAsync(f.Admin,f.Id,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ControlInstallationAsync(f.Admin,f.Id,new("Pause","modified",1),default));
        await f.Service.ControlInstallationAsync(f.Admin,f.Id,new("Pause",impact.ReviewHash,1),default);
        var paused=await f.Service.GetAsync(f.Admin,f.Id,default);Assert.False(paused.CanConnect);Assert.False(paused.Administration!.Enabled);
        var plugins=AgentInstallationServiceTests.CreatePluginInstallerForAcceptance(f.Inner.Db);
        Assert.False(Assert.Single(await plugins.ListAsync()).RemovalPending);
        Assert.Equal(PluginConnectionStatus.Connected,(await f.Inner.Db.PluginConnections.SingleAsync(x=>x.Id==account.Authorizations[0].Id)).Status);
        var current=await f.Service.GetInstallationImpactAsync(f.Admin,f.Id,default);
        await f.Service.ControlInstallationAsync(f.Admin,f.Id,new("Resume",current.ReviewHash,paused.Administration.Revision),default);
        Assert.True((await f.Service.GetAsync(f.Admin,f.Id,default)).CanConnect);
    }
    [Fact]
    public async Task RemovalWaitsForCleanupAndRetainsContentFreeTombstone()
    {
        await using var f=await F.Create();await Connect(f);var impact=await f.Service.GetInstallationImpactAsync(f.Admin,f.Id,default);
        await f.Service.ControlInstallationAsync(f.Admin,f.Id,new("Remove",impact.ReviewHash,1),default);
        Assert.False(f.Inner.Connector.IsEnabled);Assert.Equal(PluginRevisionStatus.Active,f.Inner.Connector.RevisionStatus);
        var plugins=AgentInstallationServiceTests.CreatePluginInstallerForAcceptance(f.Inner.Db);
        Assert.True(Assert.Single(await plugins.ListAsync()).RemovalPending);
        Assert.True((await plugins.GetAsync(f.Id))!.RemovalPending);
        await f.Service.ProcessPendingAsync(default);Assert.Equal(PluginRevisionStatus.Active,f.Inner.Connector.RevisionStatus);
        Assert.True(Assert.Single(await plugins.ListAsync()).RemovalPending);
        f.DeletionParticipants.Add(new ConfirmedParticipant("external:backups"));await f.Service.ProcessPendingAsync(default);
        Assert.Equal(PluginRevisionStatus.Retired,f.Inner.Connector.RevisionStatus);
        Assert.Empty(await plugins.ListAsync());
        var retained=await plugins.GetAsync(f.Id);Assert.Equal("Retired",retained!.RevisionStatus);Assert.False(retained.RemovalPending);
    }
    [Fact]
    public async Task RemovingNeverConnectedPluginImmediatelyLeavesInstalledButPreservesReceiptAccess()
    {
        await using var f=await F.Create();
        f.Inner.Db.PluginConnections.RemoveRange(await f.Inner.Db.PluginConnections.ToArrayAsync());
        await f.Inner.Db.SaveChangesAsync();
        Assert.Empty(await f.Inner.Db.PluginAccountGroups.ToArrayAsync());
        var plugins=AgentInstallationServiceTests.CreatePluginInstallerForAcceptance(f.Inner.Db);
        Assert.Single(await plugins.ListAsync());
        var impact=await f.Service.GetInstallationImpactAsync(f.Admin,f.Id,default);
        Assert.Equal(0,impact.Accounts);
        await f.Service.ControlInstallationAsync(f.Admin,f.Id,new("Remove",impact.ReviewHash,1),default);
        Assert.Equal(PluginRevisionStatus.Retired,f.Inner.Connector.RevisionStatus);
        Assert.Empty(await plugins.ListAsync());
        Assert.Empty(await f.Inner.Db.PluginAccountDeletions.ToArrayAsync());
        Assert.Empty(await f.Inner.Db.PluginAccountRevocationJobs.ToArrayAsync());
        Assert.Equal("Retired",(await plugins.GetAsync(f.Id))!.RevisionStatus);
        var view=await f.Service.GetAsync(f.Admin,f.Id,default);
        Assert.True(view.Administration!.Retired);Assert.False(view.CanConnect);
    }
    [Fact]
    public async Task MissingCleanupServicesDoNotGateProductionConsentOrImport()
    {
        await using var f=await F.Create();f.Environment.EnvironmentName="Production";
        f.AllowDevelopmentPreview=false;
        var view=await f.Service.GetAsync(f.Admin,f.Id,default);
        Assert.True(view.CanConnect);Assert.NotEmpty(view.Administration!.MissingDeletionStores);
        var account=await Connect(f);Assert.NotEmpty(account.Resources);
        var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);
        await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);
        await f.Service.ProcessPendingAsync(default);
        Assert.Null((await f.Inner.Db.PluginAccountDeletions.SingleAsync()).CompletedAt);
    }
    [Fact]
    public async Task AuthorizedResponsePropagatesProvenanceToAnotherAgentWithoutGrantingProviderAccess()
    {
        await using var f=await F.Create();var account=await Connect(f);await Bind(f,account);
        var source=new PluginOperationalState{Id=Guid.NewGuid(),OrganizationId=f.Business,AgentInstallationId=f.Inner.Requester.Id,Kind="agent-operating-state",ExternalKey="report",PayloadJson="{\"private\":true}"};
        f.Inner.Db.PluginOperationalStates.Add(source);var recipient=new AgentInstallation{Id=Guid.NewGuid(),BusinessId=f.Business.ToString("D")};f.Inner.Db.AgentInstallations.Add(recipient);await f.Inner.Db.SaveChangesAsync();
        await new CSweet.Infrastructure.Setup.PluginAccountDataExposureService(f.Inner.Db).RecordResponseAsync(f.Business,recipient.Id,System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {source.Id}),default);
        var derived=new PluginOperationalState{Id=Guid.NewGuid(),OrganizationId=f.Business,AgentInstallationId=recipient.Id,Kind="agent-operating-state",ExternalKey="summary",PayloadJson="{\"derived\":true}"};
        f.Inner.Db.PluginOperationalStates.Add(derived);await f.Inner.Db.SaveChangesAsync();
        Assert.Contains(await f.Inner.Db.PluginAccountDataCopies.ToArrayAsync(),x=>x.RecordId==derived.Id && x.AccountGroupId==account.Id);
        Assert.False(await f.Inner.Db.AgentCapabilityBindings.AnyAsync(x=>x.RequesterInstallationId==recipient.Id));
    }
    [Fact]
    public async Task UnchangedMaintenanceDoesNotSilentlyInvalidateExistingAccountAuthority()
    {
        await using var f=await F.Create();var account=await Connect(f);await Bind(f,account);var plan=await f.Inner.Prepare("read");
        var connection=await f.Inner.Db.PluginConnections.SingleAsync(x=>x.Id==account.Authorizations[0].Id);connection.NextInventoryCheckAt=DateTimeOffset.UtcNow.AddMinutes(-1);await f.Inner.Db.SaveChangesAsync();
        await f.Service.ProcessPendingAsync(default);
        await f.Inner.Service.RevalidateAsync(f.Business,f.Inner.Requester.Id,plan.Id,plan.PlanHash,default);
    }
    [Fact]
    public async Task RuntimeDeletionRequiresRecordedScopeAndVerifiedWorkloadDestruction()
    {
        await using var f=await F.Create();var account=await Connect(f);await Bind(f,account);
        f.Inner.Db.PluginOperationalStates.Add(new(){Id=Guid.NewGuid(),OrganizationId=f.Business,AgentInstallationId=f.Inner.Requester.Id,Kind="agent-operating-state",ExternalKey="runtime-source",PayloadJson="{}"});
        var runtime=new AgentRuntimeInstance{Id=Guid.NewGuid(),AgentInstallationId=f.Inner.Requester.Id,QueuedAt=DateTimeOffset.UtcNow.AddHours(-1),IsolationProviderId="test-office",ProviderInstanceId="test-workload",BrokerTokenHash="old",LogExcerpt="private provider output"};
        f.Inner.Db.AgentRuntimeInstances.Add(runtime);await f.Inner.Db.SaveChangesAsync();
        var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);
        var runner=new DeletionRunner();var participant=new CSweet.Infrastructure.Setup.PluginRuntimeDataDeletionParticipant(f.Inner.Db,runner);
        Assert.False(await participant.DeleteAsync(account.Id,f.Business,Guid.NewGuid(),Guid.NewGuid(),default));Assert.Equal(0,runner.Destroys);
        Assert.False(await participant.DeleteAsync(account.Id,f.Business,f.Inner.Requester.Id,f.Inner.Requester.Id,default));Assert.Equal(1,runner.Destroys);Assert.Null(runtime.MemoryResetCompletedAt);
        runner.StillPresent=false;
        Assert.True(await participant.DeleteAsync(account.Id,f.Business,f.Inner.Requester.Id,f.Inner.Requester.Id,default));
        Assert.NotNull(runtime.MemoryResetCompletedAt);Assert.Null(runtime.LogExcerpt);Assert.Empty(runtime.BrokerTokenHash);
    }
    [Fact]
    public async Task UnknownPrimaryStoreBlocksPrimaryCompletionReceipt()
    {
        await using var f=await F.Create();var account=await Connect(f);
        f.Inner.Db.PluginAccountDataCopies.Add(new(){Id=Guid.NewGuid(),AccountGroupId=account.Id,OrganizationId=f.Business,
            ConsumerInstallationId=f.Inner.Requester.Id,Store="FutureUnimplementedStore",RecordId=Guid.NewGuid(),CreatedAt=DateTimeOffset.UtcNow});
        await f.Inner.Db.SaveChangesAsync();var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);
        await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);
        f.DeletionParticipants.Add(new ConfirmedParticipant("external:backups"));await f.Service.ProcessPendingAsync(default);
        var deletion=await f.Inner.Db.PluginAccountDeletions.SingleAsync();Assert.Null(deletion.LocalCompletedAt);Assert.Null(deletion.CompletedAt);
    }
    [Fact]
    public async Task MissingCredentialDoesNotAttestProviderRevocationOrPermitRemovalCompletion()
    {
        await using var f=await F.Create();var account=await Connect(f);var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);
        await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);
        var job=await f.Inner.Db.PluginAccountRevocationJobs.SingleAsync();f.Secrets.Values.Clear();job.Deadline=DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Inner.Db.SaveChangesAsync();f.DeletionParticipants.Add(new ConfirmedParticipant("external:backups"));await f.Service.ProcessPendingAsync(default);
        Assert.Equal("UnconfirmedDataDeletionPending",job.Status);Assert.Null((await f.Inner.Db.PluginAccountDeletions.SingleAsync()).CompletedAt);
        Assert.Equal(PluginAccountState.Disconnecting,(await f.Inner.Db.PluginAccountGroups.SingleAsync()).State);
    }
    [Fact]
    public async Task AgentAuthoredTasksAreErasedButProtectedWorkRequiresExistingErasureWorkflow()
    {
        await using var f=await F.Create();var account=await Connect(f);await Bind(f,account);
        var author=new OrganizationUser{Id=Guid.NewGuid(),OrganizationId=f.Business,AgentInstallationId=f.Inner.Requester.Id};
        f.Inner.Db.CoreOrganizationUsers.Add(author);
        var generated=new WorkTask{Id=Guid.NewGuid(),OrganizationId=f.Business,CreatedByOrganizationUserId=author.Id,Title="Provider-derived draft",Description="private comment"};
        var human=new WorkTask{Id=Guid.NewGuid(),OrganizationId=f.Business,AssignedAgentInstallationId=f.Inner.Requester.Id,Title="Keep human instructions",Description="Original human work"};
        var work=new AgentWorkItem{Id=Guid.NewGuid(),OrganizationId=f.Business.ToString("D"),AgentInstallationId=f.Inner.Requester.Id,ProtectedPayload=[1,2,3]};
        var progress=new AgentWorkProgress{Id=Guid.NewGuid(),AgentWorkItemId=work.Id,ProtectedValue=[4,5,6]};
        f.Inner.Db.CoreWorkTasks.AddRange(generated,human);f.Inner.Db.AgentWorkItems.Add(work);f.Inner.Db.AgentWorkProgress.Add(progress);
        await f.Inner.Db.SaveChangesAsync();
        Assert.Contains(await f.Inner.Db.PluginAccountDataCopies.ToArrayAsync(),x=>x.RecordId==progress.Id);
        var impact=await f.Service.GetDisconnectImpactAsync(f.Admin,f.Id,account.Id,default);
        await f.Service.DisconnectAsync(f.Admin,f.Id,account.Id,new(impact.AccountRevision,impact.ReviewHash),default);await f.Service.ProcessPendingAsync(default);
        Assert.NotNull(generated.ArchivedAt);Assert.False(generated.IsExecutable);Assert.DoesNotContain("private",generated.Description);
        Assert.Equal("Keep human instructions",human.Title);Assert.Null(human.ArchivedAt);Assert.Empty(progress.ProtectedValue);
        Assert.NotEmpty(work.ProtectedPayload);Assert.Null(work.MemoryErasedAt);Assert.Equal(AgentWorkStatus.Cancelled,work.Status);
        var deletion=await f.Inner.Db.PluginAccountDeletions.SingleAsync();Assert.Null(deletion.LocalCompletedAt);Assert.Equal("PrimaryReviewRequired",deletion.Status);
    }
    [Fact]
    public async Task ExpiredInventoryAndRecordedDraftsArePurgedWithoutInventingAnAccountDeletionReceipt()
    {
        await using var f=await F.Create();var account=await Connect(f);await Bind(f,account);var old=DateTimeOffset.UtcNow.AddDays(-31);
        var state=new PluginOperationalState{Id=Guid.NewGuid(),OrganizationId=f.Business,AgentInstallationId=f.Inner.Requester.Id,Kind="agent-operating-state",ExternalKey="old-report",PayloadJson="{\"private\":true}"};
        f.Inner.Db.PluginOperationalStates.Add(state);await f.Inner.Db.SaveChangesAsync();
        foreach(var resource in await f.Inner.Db.PluginAccountResources.ToArrayAsync()) resource.VerifiedAt=old;
        foreach(var connection in await f.Inner.Db.PluginConnections.ToArrayAsync()) connection.NextInventoryCheckAt=null;
        foreach(var copy in await f.Inner.Db.PluginAccountDataCopies.Where(x=>x.RecordId==state.Id).ToArrayAsync()) copy.CreatedAt=old;
        await f.Inner.Db.SaveChangesAsync();await f.Service.ProcessPendingAsync(default);
        Assert.False(await f.Inner.Db.PluginOperationalStates.AnyAsync(x=>x.Id==state.Id));
        Assert.All(await f.Inner.Db.PluginAccountResources.ToArrayAsync(),r=>{Assert.False(r.IsAvailable);Assert.Equal("Expired resource",r.DisplayName);Assert.DoesNotContain("workspace",r.ExternalResourceId);});
        Assert.Empty(await f.Inner.Db.PluginAccountDeletions.ToArrayAsync());
    }
    private sealed class DeletionRunner:IAgentWorkloadRunner
    {
        public bool StillPresent {get;set;}=true;public int Destroys {get;private set;}
        public Task<IsolationWorkloadHandle> CreateAndStartAsync(CSweet.Office.Contracts.Workloads.RuntimeWorkloadSpecification workload,CSweet.Office.Contracts.Workloads.AgentTrustLevel trustLevel,string? preferredProviderId=null,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle,CancellationToken ct=default)=>Task.FromResult<IsolationWorkloadStatus?>(StillPresent?new(handle,IsolationWorkloadState.Stopped,IsolationTerminationReason.Completed,0,null,null,null,null):null);
        public Task StopAsync(IsolationWorkloadHandle handle,TimeSpan gracePeriod,CancellationToken ct=default)=>Task.CompletedTask;
        public Task DestroyAsync(IsolationWorkloadHandle handle,CancellationToken ct=default){Destroys++;return Task.CompletedTask;}
        public Task<string> GetLogsAsync(IsolationWorkloadHandle handle,int max,CancellationToken ct=default)=>Task.FromResult("");
    }
    private sealed class ConfirmedParticipant(string store):IPluginAccountDataDeletionParticipant
    {
        public string Store=>store;
        public Task<bool> DeleteAsync(Guid accountId,Guid? organizationId,Guid consumerInstallationId,Guid recordId,CancellationToken ct)=>Task.FromResult(true);
    }
}
