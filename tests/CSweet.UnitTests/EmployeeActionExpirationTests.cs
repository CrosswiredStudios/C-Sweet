using System.Text.Json;
using System.Reflection;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class EmployeeActionExpirationTests
{
    [Fact]
    public async Task FiringEmployee_CancelsOutstandingActionsAndLinkedOwnerHires_PreservesHistoryAndOtherEmployees()
    {
        await using var db = CreateDb();
        var (employee, owner, installation) = await SeedAsync(db);
        var leadership = new LeadershipAssignment { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            OrganizationUserId = employee.Id, PositionKey = LeadershipPositionKeys.ChiefOfStaff };
        var ownerLeadership = new LeadershipAssignment { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            OrganizationUserId = owner.Id, PositionKey = LeadershipPositionKeys.ChiefExecutiveOfficer };
        db.AddRange(leadership, ownerLeadership);
        var plan = Plan(employee.OrganizationId, installation.Id);
        var completedPlan = Plan(employee.OrganizationId, installation.Id);
        completedPlan.Status = ProposalStatus.Approved;
        completedPlan.FulfilledHeadcount = 1;
        var otherPlan = Plan(employee.OrganizationId, Guid.NewGuid());
        var workflow = new StaffingActionProposal { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            WorkforcePlanId = plan.Id, RequestingInstallationId = Guid.Empty, Status = ProposalStatus.Pending };
        var operation = new AgentHireOperation { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            WorkflowId = workflow.Id, InitiatedByOrganizationUserId = owner.Id, Status = AgentHireOperationStatus.Building };
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            AgentOrganizationUserId = employee.Id, IsDeletionProtected = true };
        var action = new SuggestedUserAction { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            OriginatingInstallationId = installation.Id, ConversationId = chat.Id,
            WorkflowType = SuggestedUserActionWorkflows.BrowseHiringMarketplace };
        var linkedAction = new SuggestedUserAction { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            OriginatingInstallationId = Guid.NewGuid(), ConversationId = chat.Id,
            ParametersJson = JsonSerializer.Serialize(new { recommendationId = plan.Id }),
            WorkflowType = SuggestedUserActionWorkflows.BrowseHiringMarketplace };
        var completedAction = new SuggestedUserAction { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            OriginatingInstallationId = installation.Id, ConversationId = chat.Id, Status = SuggestedUserActionStatuses.Completed };
        var question = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            ConversationId = chat.Id, RequestingInstallationId = installation.Id };
        var proposal = new ActionProposal { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            AgentInstallationId = installation.Id };
        var resource = new ResourceChangeRequestRecord { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            RequesterOrganizationUserId = employee.Id, RequesterInstallationId = installation.Id };
        var replenishment = new StaffingReplenishmentRequestRecord { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            RequesterOrganizationUserId = employee.Id, RequesterInstallationId = installation.Id };
        var repository = new RepositoryProvisioningRequest { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            RequestedByOrganizationUserId = employee.Id, RequestedByAgentInstallationId = installation.Id,
            Status = RepositoryProvisioningStatus.AwaitingApproval };
        var sourceApproval = new SourceControlApproval { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            RequestedByOrganizationUserId = employee.Id, RequestedByAgentInstallationId = installation.Id, ProvisioningRequestId = repository.Id };
        var connector = new ConnectorExecution { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            RequesterInstallationId = installation.Id, Status = "AwaitingApproval" };
        var access = new ArtifactAccessRequest { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            ArtifactId = Guid.NewGuid(), RequestingInstallationId = installation.Id, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = installation.Id };
        await new PersonalTodoService(db, TimeProvider.System).EnsureBoardAsync(employee.OrganizationId, employee.Id);
        var board = await db.WorkBoards.SingleAsync(x => x.OwnerOrganizationUserId == employee.Id);
        var auditCard = new WorkTask { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId, BoardId = board.Id,
            AssignedEmployeeId = employee.Id, CreatedByOrganizationUserId = employee.Id, AssignedAgentInstallationId = installation.Id,
            CreationIdempotencyKey = "chief-work:hire:test", IsExecutable = false, Status = WorkTaskStatus.Running };
        db.AddRange(plan, completedPlan, otherPlan, workflow, operation, chat, action, linkedAction, completedAction,
            question, proposal, resource, replenishment, repository, sourceApproval, connector, access, auditCard);
        await db.SaveChangesAsync();
        db.ApplicationRealtimeOutbox.RemoveRange(db.ApplicationRealtimeOutbox);
        await db.SaveChangesAsync();

        var result = await new OrganizationUserService(db, new TestAuditEventWriter()).DeleteAsync(employee.Id);

        Assert.True(result.Succeeded);
        Assert.False(employee.IsActive);
        Assert.Equal(employee.ArchivedAt, leadership.EndsAt);
        Assert.Null(ownerLeadership.EndsAt);
        Assert.Null(employee.AgentInstallationId);
        Assert.False(installation.IsEnabled);
        var cleanup = await db.ResourceCleanupRequests.SingleAsync(x => x.ResourceId == installation.Id);
        Assert.Equal(CSweet.Infrastructure.Setup.ResourceCleanupQueue.Runtime, cleanup.Kind);
        Assert.Equal(employee.OrganizationId, cleanup.BusinessId);
        Assert.Null(cleanup.CompletedAt);
        Assert.NotNull(chat.ArchivedAt);
        Assert.Equal(ProposalStatus.Cancelled, plan.Status);
        Assert.Equal(ProposalStatus.Cancelled, workflow.Status);
        Assert.Equal(AgentHireOperationStatus.Cancelled, operation.Status);
        Assert.Contains("removed", operation.Error);
        Assert.Equal(SuggestedUserActionStatuses.Cancelled, action.Status);
        Assert.Equal(SuggestedUserActionStatuses.Cancelled, linkedAction.Status);
        Assert.Equal(ExecutiveDecisionStatus.Cancelled, question.Status);
        Assert.Equal(ProposalStatus.Cancelled, proposal.Status);
        Assert.Equal(ResourceChangeRequestStatus.Cancelled, resource.Status);
        Assert.Equal(StaffingReplenishmentRequestStatus.Cancelled, replenishment.Status);
        Assert.Equal(ApprovalStatus.Cancelled, sourceApproval.Status);
        Assert.Equal(RepositoryProvisioningStatus.Cancelled, repository.Status);
        Assert.Equal("Cancelled", connector.Status);
        Assert.Equal(ArtifactAccessRequestStatus.Cancelled, access.Status);
        Assert.Equal(WorkTaskStatus.Cancelled, auditCard.Status);
        Assert.Equal(WorkBoardColumnCategory.Cancelled, (await db.WorkBoardColumns.SingleAsync(x => x.Id == auditCard.BoardColumnId)).Category);
        Assert.Equal(ProposalStatus.Approved, completedPlan.Status);
        Assert.Equal(1, completedPlan.FulfilledHeadcount);
        Assert.Equal(SuggestedUserActionStatuses.Completed, completedAction.Status);
        Assert.Equal(ProposalStatus.Pending, otherPlan.Status);
        var events = await db.ApplicationRealtimeOutbox.Select(x => x.EventType).ToListAsync();
        Assert.Contains(AppRealtimeEvents.HiringRecommendationsChanged, events);
        Assert.Contains(AppRealtimeEvents.ApprovalChanged, events);
        Assert.Contains(SuggestedUserActionEvents.Cancelled, events);
        Assert.Contains(AppRealtimeEvents.AgentHireOperationChanged, events);
        var eventCount = events.Count;
        Assert.True((await new OrganizationUserService(db, new TestAuditEventWriter()).DeleteAsync(employee.Id)).Succeeded);
        Assert.Equal(eventCount, await db.ApplicationRealtimeOutbox.CountAsync());

        var service = new HiringService(db, new OrganizationUserService(db, new TestAuditEventWriter()), new TestAuditEventWriter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmWorkflowAsync(employee.OrganizationId,
            workflow.Id, owner.ApplicationUserId!.Value, new ConfirmHiringWorkflowRequest("stale-card")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RetryAsync(operation.Id, owner.ApplicationUserId!.Value));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpsertRecommendationAsync(employee.OrganizationId,
            installation.Id, new UpsertHiringRecommendationRequest("Creative Director", "Late callback", null, [], null, "late")));
        Assert.Equal(2, await db.CoreOrganizationUsers.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_ReconcilesRecommendationsFromPreviouslyFiredEmployee_AndOldPreviewCannotBeUsed(bool clickFirst)
    {
        await using var db = CreateDb();
        var (employee, owner, installation) = await SeedAsync(db);
        var plan = Plan(employee.OrganizationId, installation.Id);
        db.WorkforcePlans.Add(plan);
        // Reproduce the old dismissal behavior, which left the request pending.
        employee.IsActive = false;
        employee.ArchivedAt = DateTimeOffset.UtcNow;
        employee.AgentInstallationId = null;
        installation.IsEnabled = false;
        await db.SaveChangesAsync();
        var service = new HiringService(db, new OrganizationUserService(db, new TestAuditEventWriter()), new TestAuditEventWriter());

        if (clickFirst)
            await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewMarketplaceHireAsync(employee.OrganizationId,
                owner.ApplicationUserId!.Value, new PreviewMarketplaceHireRequest("catalog:naomi", "Creative Director", "Naomi", owner.Id, "old-card")
                { RecommendationId = plan.Id }));

        Assert.Empty(await service.ListRecommendationsAsync(employee.OrganizationId));
        Assert.Equal(ProposalStatus.Cancelled, plan.Status);
        Assert.NotNull(plan.DecidedAt);
        var eventCount = await db.ApplicationRealtimeOutbox.CountAsync();
        Assert.Empty(await service.ListRecommendationsAsync(employee.OrganizationId));
        Assert.Equal(eventCount, await db.ApplicationRealtimeOutbox.CountAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewMarketplaceHireAsync(employee.OrganizationId,
            owner.ApplicationUserId!.Value, new PreviewMarketplaceHireRequest("catalog:naomi", "Creative Director", "Naomi", owner.Id, "old-card")
            { RecommendationId = plan.Id }));
        Assert.Empty(await db.StaffingActionProposals.ToListAsync());

        // A fresh employee lifecycle must not revive the departed employee's requests.
        installation.IsEnabled = true;
        db.CoreOrganizationUsers.Add(new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            DisplayName = "Fresh Evelyn", EmployeeType = EmployeeType.Agent, AgentInstallationId = installation.Id });
        await db.SaveChangesAsync();
        Assert.Empty(await service.ListRecommendationsAsync(employee.OrganizationId));
        Assert.Equal(ProposalStatus.Cancelled, plan.Status);
    }

    [Fact]
    public async Task DismissalDuringBackgroundHire_DoesNotOverwriteCancellationOrResumeHiring()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CSweetDbContext(options);
        var (employee, owner, installation) = await SeedAsync(db);
        var plan = Plan(employee.OrganizationId, installation.Id);
        var workflow = new StaffingActionProposal { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            WorkforcePlanId = plan.Id, RequestingInstallationId = installation.Id };
        var operation = new AgentHireOperation { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            WorkflowId = workflow.Id, InitiatedByOrganizationUserId = owner.Id, Status = AgentHireOperationStatus.Building,
            AgentDefinitionId = Guid.NewGuid() };
        db.AddRange(plan, workflow, operation);
        await db.SaveChangesAsync();
        var definitions = DispatchProxy.Create<IAgentDefinitionService, DismissingDefinitions>();
        ((DismissingDefinitions)(object)definitions).Dismiss = async () =>
        {
            await using var dismissal = new CSweetDbContext(options);
            Assert.True((await new OrganizationUserService(dismissal, new TestAuditEventWriter()).DeleteAsync(employee.Id)).Succeeded);
        };
        var service = new HiringService(db, new OrganizationUserService(db, new TestAuditEventWriter()), new TestAuditEventWriter(),
            agentDefinitions: definitions);

        Assert.True(await service.ProcessNextAsync("worker"));
        Assert.Equal(AgentHireOperationStatus.Cancelled, operation.Status);
        Assert.False(await service.ProcessNextAsync("worker-again"));
        Assert.Equal(2, await db.CoreOrganizationUsers.CountAsync());
        Assert.Equal(ProposalStatus.Cancelled, (await db.StaffingActionProposals.AsNoTracking().SingleAsync()).Status);
    }

    public class DismissingDefinitions : DispatchProxy
    {
        public Func<Task> Dismiss { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IAgentDefinitionService.GetAsync) ? GetAsync() : throw new NotSupportedException();
        private async Task<AgentDefinitionResponse?> GetAsync() { await Dismiss(); return null; }
    }

    [Fact]
    public async Task FailedDismissal_DoesNotExpireEmployeeRequests()
    {
        await using var db = CreateDb();
        var (employee, _, installation) = await SeedAsync(db);
        var plan = Plan(employee.OrganizationId, installation.Id);
        db.WorkforcePlans.Add(plan);
        db.OrganizationTeams.Add(new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = employee.OrganizationId,
            LeadOrganizationUserId = employee.Id, Name = "Active team" });
        await db.SaveChangesAsync();

        var result = await new OrganizationUserService(db, new TestAuditEventWriter()).DeleteAsync(employee.Id);

        Assert.False(result.Succeeded);
        Assert.Equal("active_team_lead", result.ErrorCode);
        Assert.True(employee.IsActive);
        Assert.Equal(ProposalStatus.Pending, plan.Status);
    }

    private static WorkforcePlan Plan(Guid organizationId, Guid installationId) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, RequestingInstallationId = installationId,
        Title = "Creative Director", Status = ProposalStatus.Pending, IdempotencyKey = Guid.NewGuid().ToString()
    };

    private static async Task<(OrganizationUser Employee, OrganizationUser Owner, AgentInstallation Installation)> SeedAsync(CSweetDbContext db)
    {
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Example" };
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id, DisplayName = "Owner",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, ApplicationUserId = Guid.NewGuid() };
        var installation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
            BusinessId = organization.Id.ToString("D"), IsEnabled = true };
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id, DisplayName = "Evelyn",
            EmployeeType = EmployeeType.Agent, AgentInstallationId = installation.Id, ReportsToOrganizationUserId = owner.Id };
        db.AddRange(organization, owner, installation, employee);
        await db.SaveChangesAsync();
        return (employee, owner, installation);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
