using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class ProjectApprovalGovernanceTests
{
    private static ProjectApprovalGovernance.Decision Command(ActionProposal proposal, string kind = "Approve") =>
        new(proposal.Id, kind, "Reviewed against the accepted outcome.", new string('b', 64), "plan", "review:" + proposal.Id.ToString("N") + ":" + kind);

    [Fact]
    public async Task BrokerAcceptsNullDiscoveryIdAndRequiresReviewedCapabilityGrant()
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, producer, proposal, _) = await ProjectApprovalReviewTests.Seed(db);
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId, DisplayName = "Naomi",
            AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = owner.Id };
        producer.ReportsToOrganizationUserId = manager.Id; db.Add(manager); await db.SaveChangesAsync();
        var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter());
        var request = new RequestCapability { RequestId = "read", Capability = ProjectApprovalGovernance.ReadCapability,
            Payload = JsonPayload.FromUtf8("{\"proposalId\":null}") };
        foreach (var granted in new[] { false, true })
        {
            var session = new AgentSession("session", "director", manager.AgentInstallationId.Value.ToString(), owner.OrganizationId.ToString(), "runtime", "tick",
                new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), granted ? new HashSet<string> { request.Capability } : new HashSet<string>(), 1));
            var responses = new List<CapabilityResult>();
            await foreach (var response in handler.HandleAsync(session, request, default)) responses.Add(response);
            var result = Assert.Single(responses);
            Assert.Equal(granted, result.Succeeded);
            if (granted)
            {
                using var body = JsonDocument.Parse(result.Payload.ToStringUtf8());
                Assert.Equal(proposal.Id, Assert.Single(body.RootElement.EnumerateArray()).GetProperty("proposalId").GetGuid());
            }
        }
    }

    [Fact]
    public async Task ActualExecutorCreatesTheApprovedProjectForAnAgentManager()
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, producer, proposal, _) = await ProjectApprovalReviewTests.Seed(db);
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId, DisplayName = "Naomi",
            AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = owner.Id };
        producer.ReportsToOrganizationUserId = manager.Id; db.Add(manager);
        db.WorkstreamProfileDefinitions.Add(new() { Id = Guid.NewGuid(), Key = "video-game", Version = 1, DefinitionDigest = "profile-digest",
            MetadataSchemaJson = "{\"type\":\"object\"}", DefinitionJson = "{\"lifecycle\":{\"stages\":[{\"key\":\"Concept\"}]}}" });
        using var binding = JsonDocument.Parse(proposal.PayloadJson);
        var fields = binding.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
        fields["profileDefinitionDigest"] = JsonSerializer.SerializeToElement("profile-digest");
        proposal.PayloadJson = JsonSerializer.Serialize(fields); await db.SaveChangesAsync();
        var service = new ProjectApprovalGovernance(db);
        var executor = new WorkstreamManagedActionExecutor(db, TimeProvider.System);
        await service.DecideAsync(manager, Command(proposal), [executor]);
        await service.DecideAsync(manager, Command(proposal), [executor]);
        var project = Assert.Single(db.Workstreams);
        Assert.Equal(proposal.Id, project.SourceProposalId);
        Assert.Equal(producer.Id, project.AccountableManagerOrganizationUserId);
        Assert.Equal(WorkstreamStatus.Approved, project.Status);
        Assert.Equal(project.Id, Assert.Single(await service.ReadAsync(producer, proposal.Id)).Decision!.ProjectId);
    }

    [Fact]
    public async Task AgentManagerApprovalExecutesOnceAndOwnerCannotBypassManager()
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, producer, proposal, _) = await ProjectApprovalReviewTests.Seed(db);
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId, DisplayName = "Naomi",
            AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = owner.Id };
        producer.ReportsToOrganizationUserId = manager.Id; db.Add(manager); await db.SaveChangesAsync();
        var service = new ProjectApprovalGovernance(db); var executor = new Executor();
        Assert.False(await service.CanDecideAsync(proposal, owner));
        Assert.True(await service.CanDecideAsync(proposal, manager));
        Assert.Empty(await service.ReadAsync(owner, null));
        Assert.Equal("Unlimited", Assert.Single(await service.ReadAsync(manager, null)).Spending.Mode);
        await service.DecideAsync(manager, Command(proposal), [executor]);
        await service.DecideAsync(manager, Command(proposal), [executor]);
        Assert.Equal(1, executor.Calls); Assert.Equal(ProposalStatus.Approved, proposal.Status);
        var receipt = Assert.Single(await service.ReadAsync(producer, proposal.Id)).Decision;
        Assert.Equal(executor.ProjectId, receipt!.ProjectId);
        Assert.Equal(manager.Id, receipt.ActorId);
        Assert.Contains(db.AgentPlatformEventOutbox, x => x.TargetInstallationId == producer.AgentInstallationId && x.EventType == ProjectApprovalGovernance.DecidedEvent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(manager, Command(proposal) with { Comment = "Different command" }, [executor]));
    }

    [Fact]
    public async Task EscalationReassignsExactProposalAndPreservesRationaleAndReplay()
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, producer, proposal, _) = await ProjectApprovalReviewTests.Seed(db);
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId, DisplayName = "Naomi",
            AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = owner.Id };
        producer.ReportsToOrganizationUserId = manager.Id; db.Add(manager); await db.SaveChangesAsync();
        var service = new ProjectApprovalGovernance(db); var command = Command(proposal, "Escalate");
        await service.DecideAsync(manager, command, []);
        await service.DecideAsync(manager, command, []);
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.False(await service.CanDecideAsync(proposal, manager)); Assert.True(await service.CanDecideAsync(proposal, owner));
        Assert.Equal(manager.Id, Assert.Single(await service.ReadAsync(owner, null)).Escalation!.EscalatedBy);
        var card = await new ProjectApprovalReader(db).ReadAsync(owner.OrganizationId, owner.Id, proposal.Id);
        Assert.Contains(command.Comment, card!.ProjectCreation!.Rationale);
        await service.DecideAsync(owner, Command(proposal), [new Executor()]);
        Assert.Equal(ProposalStatus.Approved, proposal.Status);
    }

    [Theory]
    [InlineData("RequestRevision", ProposalStatus.Cancelled)]
    [InlineData("Reject", ProposalStatus.Rejected)]
    [InlineData("Withdraw", ProposalStatus.Cancelled)]
    public async Task NonApprovalPreservesFeedbackWithoutExecution(string kind, ProposalStatus status)
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, producer, proposal, _) = await ProjectApprovalReviewTests.Seed(db);
        var service = new ProjectApprovalGovernance(db); var executor = new Executor();
        await service.DecideAsync(kind == "Withdraw" ? producer : owner, Command(proposal, kind), [executor]);
        Assert.Equal(status, proposal.Status); Assert.Equal(0, executor.Calls);
        Assert.Equal(kind, Assert.Single(await service.ReadAsync(producer, proposal.Id)).Decision!.Decision);
    }

    [Fact]
    public async Task StaleBindingAndExecutorFailureRemainPending()
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, _, proposal, _) = await ProjectApprovalReviewTests.Seed(db);
        var service = new ProjectApprovalGovernance(db); var executor = new Executor { Fails = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(owner, Command(proposal) with { PayloadHash = "stale" }, [executor]));
        Assert.Equal(0, executor.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(owner, Command(proposal), [executor]));
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.DoesNotContain(db.PluginOperationalStates, x => x.Kind == ProjectApprovalReader.ReceiptKind);
    }

    [Fact]
    public async Task LimitedBudgetRequiresAmountAndCurrencyBeforeAgentApproval()
    {
        await using var db = ProjectApprovalReviewTests.Database();
        var (owner, producer, proposal, _) = await ProjectApprovalReviewTests.Seed(db);
        var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId, DisplayName = "Naomi",
            AgentInstallationId = Guid.NewGuid(), EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = owner.Id };
        producer.ReportsToOrganizationUserId = manager.Id; db.Add(manager);
        db.AgentInstallationConfigurations.Add(new() { AgentInstallationId = manager.AgentInstallationId.Value,
            SettingsJson = JsonSerializer.Serialize(new { maximumProjectBudget = 100, projectBudgetCurrency = "USD" }) });
        await db.SaveChangesAsync();
        var service = new ProjectApprovalGovernance(db); var executor = new Executor();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(manager, Command(proposal), [executor]));
        Assert.Equal(0, executor.Calls); Assert.Equal(ProposalStatus.Pending, proposal.Status);
        await service.DecideAsync(manager, Command(proposal, "Escalate"), []);
        Assert.True(await service.CanDecideAsync(proposal, owner));
    }

    private sealed class Executor : IManagedActionExecutor
    {
        public int Calls; public bool Fails; public Guid ProjectId = Guid.NewGuid();
        public bool CanExecute(string type) => type == ProjectApprovalReader.ActionType;
        public Task<ManagedActionExecutionResult> ExecuteAsync(ActionProposal proposal, OrganizationUser actor, CancellationToken ct = default)
        { Calls++; if (Fails) throw new InvalidOperationException("Execution failed."); return Task.FromResult(new ManagedActionExecutionResult(ProjectId, 1, "Created")); }
    }
}
