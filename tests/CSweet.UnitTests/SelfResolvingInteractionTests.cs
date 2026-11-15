using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class SelfResolvingInteractionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task ThirdPartyReadOnlyReviewerContributesThroughPublicBrokerAndOwnerAppliesOnce()
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        f.Board.TeamId = Guid.NewGuid();
        await Grant(f, f.Specialist.AgentInstallationId!.Value, WorkBoardActions.Read, GrantScopeKind.Team, f.Board.TeamId.Value);
        await Grant(f, f.Specialist.AgentInstallationId.Value, ArtifactActions.Read, GrantScopeKind.Artifact, f.Source.DocumentId);
        await Grant(f, f.Manager.AgentInstallationId!.Value, WorkItemActions.RevisePlanning, GrantScopeKind.Team, f.Board.TeamId.Value);
        await Grant(f, f.Manager.AgentInstallationId.Value, WorkBoardActions.Read, GrantScopeKind.Team, f.Board.TeamId.Value);
        await Grant(f, f.Manager.AgentInstallationId.Value, ArtifactActions.Read, GrantScopeKind.Artifact, f.Source.DocumentId);
        var service = new WorkIssueService(f.Db, new ScopedActionAuthorizationService(f.Db), TimeProvider.System);
        var broker = new WorkIssueCapabilityHandler(f.Db, service);
        var input = new Wire.ReconcileWorkIssuesRequest(f.Board.Id, f.Source, [new("presentation", "Own the approved presentation outcome", "Review the exact approved constraint", ["juice"])], "external-contribution");
        var read = await Invoke<Wire.WorkIssueDirectory>(f, broker, f.Specialist.AgentInstallationId.Value, Wire.WorkIssueCapabilities.Read, new Wire.ReadWorkIssuesRequest(f.Board.Id));
        Assert.False(read.CanReconcile); Assert.Equal(f.Manager.Id, read.AccountableEmployeeId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReconcileAsync(f.Org, f.Specialist.Id, input, default));
        var proposal = await Invoke<Wire.WorkIssueCommissionResponse>(f, broker, f.Specialist.AgentInstallationId.Value, Wire.WorkIssueCapabilities.Propose, input);
        var replay = await Invoke<Wire.WorkIssueCommissionResponse>(f, broker, f.Specialist.AgentInstallationId.Value, Wire.WorkIssueCapabilities.Propose, input);
        Assert.Equal(proposal.Id, replay.Id); Assert.Empty(await f.Db.WorkIssueCases.ToArrayAsync());
        Assert.Contains(await f.Db.AgentPlatformEventOutbox.ToArrayAsync(), e => e.TargetInstallationId == f.Manager.AgentInstallationId);
        var decision = new Wire.DecideWorkIssueProposalRequest(proposal.Id, proposal.Revision, true, "Reviewed against approved wording; retain this concrete responsibility", "owner-judgment");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DecideProposalAsync(f.Org, f.Specialist.Id, decision, default));
        var applied = await Invoke<Wire.WorkIssueCommissionResponse>(f, broker, f.Manager.AgentInstallationId.Value, Wire.WorkIssueCapabilities.DecideProposal, decision);
        var appliedAgain = await Invoke<Wire.WorkIssueCommissionResponse>(f, broker, f.Manager.AgentInstallationId.Value, Wire.WorkIssueCapabilities.DecideProposal, decision);
        Assert.Equal("Accepted", applied.Status); Assert.Equal(applied.Id, appliedAgain.Id);
        Assert.Single(await f.Db.WorkIssueCases.ToArrayAsync()); Assert.Single(await f.Db.WorkIssueCommissions.ToArrayAsync());
        Assert.DoesNotContain(await f.Db.ScopedActionGrants.ToArrayAsync(), g => g.SubjectId == f.Specialist.AgentInstallationId && g.Action == WorkItemActions.RevisePlanning);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("owner")]
    [InlineData("revision")]
    public async Task ChangedBasisCannotApplyContributionAndHistoricalSourceCanBeDeclined(string change)
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        var input = new Wire.ReconcileWorkIssuesRequest(f.Board.Id, f.Source, [new("rules", "Retain the full rules", "Evidence", [])], "contribution");
        var proposal = await f.Service.ProposeAsync(f.Org, f.Specialist.Id, f.Specialist.AgentInstallationId!.Value, input, default);
        if (change == "source") await f.ChangeSourceAsync();
        if (change == "owner") { f.Board.ManagerOrganizationUserId = f.Specialist.Id; await f.Db.SaveChangesAsync(); }
        if (change == "revision") { (await f.Db.WorkIssueCommissions.SingleAsync()).Revision++; await f.Db.SaveChangesAsync(); }
        var accept = new Wire.DecideWorkIssueProposalRequest(proposal.Id, proposal.Revision, true, "Apply", "accept");
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.DecideProposalAsync(f.Org, f.Manager.Id, accept, default));
        Assert.Empty(await f.Db.WorkIssueCases.ToArrayAsync());
        if (change == "source") {
            var rejected = await f.Service.DecideProposalAsync(f.Org, f.Manager.Id, accept with { Accept = false, Reason = "Source superseded; retain the proposed finding for current-source review", IdempotencyKey = "decline" }, default);
            Assert.Equal("Rejected", rejected.Status);
        }
    }

    [Fact]
    public async Task AcceptedContributionRetainsPrivateTrackingOnlyForItsOriginalParticipant()
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        var session = Guid.NewGuid();
        f.Db.AgentCoordinationSessions.Add(new() { Id=session, OrganizationId=f.Org, WorkstreamId=f.Board.WorkstreamId,
            InitiatorOrganizationUserId=f.Specialist.Id, InitiatorInstallationId=f.Specialist.AgentInstallationId!.Value,
            TargetOrganizationUserId=Guid.NewGuid(), TargetInstallationId=Guid.NewGuid() });
        await f.Db.SaveChangesAsync();
        var proposed=await f.Service.ProposeAsync(f.Org,f.Specialist.Id,f.Specialist.AgentInstallationId.Value,new(f.Board.Id,f.Source,
            [new("scope-owner","Meaningful source owner","Contributor judgment",[]) {Links=[new("Coordination",session)]}],"private-origin"),default);
        var decided=await f.Service.DecideProposalAsync(f.Org,f.Manager.Id,new(proposed.Id,proposed.Revision,true,"Owner reviewed approved wording","decide-private"),default);
        Assert.Null(decided.Failure);Assert.Equal("Owner reviewed approved wording",decided.DecisionReason);
        var owner=Assert.Single((await f.Service.ReadAsync(f.Org,f.Manager.Id,new(f.Board.Id),default)).Commissions);
        Assert.Null(owner.OriginSessionIds);Assert.DoesNotContain(owner.ProposedChanges!.SelectMany(c=>c.Links),l=>l.Id==session);
        var contributor=Assert.Single((await f.Service.ReadAsync(f.Org,f.Specialist.Id,new(f.Board.Id),default)).Commissions);
        Assert.Equal(session,Assert.Single(contributor.OriginSessionIds!));
    }

    [Fact]
    public async Task ConcurrentContributorsUseAFreshOwnerJudgmentAndKeepOneCanonicalObligation()
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        var original = new Wire.ReconcileWorkIssuesRequest(f.Board.Id,f.Source,
            [new("shared-obligation","Own the approved outcome","Contributor evidence",[])],"first-contributor");
        var first=await f.Service.ProposeAsync(f.Org,f.Specialist.Id,f.Specialist.AgentInstallationId!.Value,original,default);
        var second=await f.Service.ProposeAsync(f.Org,f.Specialist.Id,f.Specialist.AgentInstallationId.Value,original with {IdempotencyKey="second-contributor"},default);
        await f.Service.DecideProposalAsync(f.Org,f.Manager.Id,new(first.Id,first.Revision,true,"Reviewed the approved source","accept-first"),default);
        var stale=new Wire.DecideWorkIssueProposalRequest(second.Id,second.Revision,true,"Original contribution snapshot","stale-owner-command");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(()=>f.Service.DecideProposalAsync(f.Org,f.Manager.Id,stale,default));
        var current=Assert.Single((await f.Service.ReadAsync(f.Org,f.Manager.Id,new(f.Board.Id),default)).Issues);
        var reviewed=stale with {Reason="Reviewed the current canonical obligation and approved source together",IdempotencyKey="current-owner-command",
            ReviewedIssues=[new(current.ObligationKey,current.Id,current.Revision)]};
        await f.Service.DecideProposalAsync(f.Org,f.Manager.Id,reviewed,default);
        Assert.Single(await f.Db.WorkIssueCases.ToArrayAsync());
        Assert.Equal(2,await f.Db.WorkIssueCommissions.CountAsync(c=>c.Status=="Accepted"));
    }

    private static async Task Grant(WorkIssueLifecycleTests.Fixture f, Guid installation, string action, GrantScopeKind scope, Guid id)
    {
        f.Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = installation, Action = action, ScopeKind = scope, ScopeId = id }); await f.Db.SaveChangesAsync();
    }
    private static async Task<T> Invoke<T>(WorkIssueLifecycleTests.Fixture f, WorkIssueCapabilityHandler broker, Guid installation, string capability, object input)
    {
        // This participant uses only public wire contracts and the authenticated broker, with no SDK agent inheritance or private state keys.
        var session = new AgentSession("external-session", "third-party.agent", installation.ToString(), f.Org.ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            new(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { capability }, 1));
        var request = new RequestCapability { RequestId = Guid.NewGuid().ToString(), Capability = capability, Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(input, Json)) };
        var results = new List<CapabilityResult>(); await foreach (var result in broker.HandleAsync(session, request, default)) results.Add(result);
        var response = Assert.Single(results); Assert.True(response.Succeeded, response.Error);
        return JsonSerializer.Deserialize<T>(response.Payload.Span, Json)!;
    }
}
