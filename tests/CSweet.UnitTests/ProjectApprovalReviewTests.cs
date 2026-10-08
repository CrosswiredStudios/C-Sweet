using System.Text.Json;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class ProjectApprovalReviewTests
{
    [Fact]
    public async Task ReadsBoundPlanWithNamesAndUnknownBudgetWithoutInventingBoardOrCost()
    {
        await using var db = Database();
        var (owner, agent, proposal, _) = await Seed(db);
        var item = await new ProjectApprovalReader(db).ReadAsync(owner.OrganizationId, owner.Id, proposal.Id);
        Assert.True(item!.CanDecide);
        Assert.Equal("Create project: Prism Break", item.Title);
        Assert.Equal("Naomi", item.RequestedBy);
        Assert.Equal("Naomi", item.ProjectCreation!.Lead);
        Assert.Null(item.ProjectCreation.Budget);
        Assert.Null(item.ProjectCreation.TargetDate);
        Assert.False(item.ProjectCreation.CreatesBoard);
        Assert.Equal(["Launch"], item.ProjectCreation.HumanDecisions);
        Assert.DoesNotContain(new string('a', 64), item.Summary);
        Assert.Equal(new string('b', 64), item.AgentAction!.PayloadHash);
    }

    [Fact]
    public async Task ReadsCurrentDecisionAndDoesNotGrantOtherManagerOrOtherOrganizationAccess()
    {
        await using var db = Database();
        var (owner, agent, proposal, _) = await Seed(db);
        var other = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId,
            DisplayName = "Unassigned manager", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Manager };
        db.Add(other); await db.SaveChangesAsync();
        var reader = new ProjectApprovalReader(db);
        Assert.Null(await reader.ReadAsync(owner.OrganizationId, other.Id, proposal.Id));
        Assert.Null(await reader.ReadAsync(Guid.NewGuid(), owner.Id, proposal.Id));
        proposal.Status = ProposalStatus.Cancelled; proposal.DecidedAt = DateTimeOffset.UtcNow;
        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId,
            AgentInstallationId = proposal.AgentInstallationId, Kind = ProjectApprovalReader.ReceiptKind,
            ExternalKey = proposal.Id.ToString("N"), PayloadJson = JsonSerializer.Serialize(new ProjectApprovalReader.Receipt(
                "RequestRevision", "Reduce the scope", owner.Id, owner.DisplayName, null), new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await db.SaveChangesAsync();
        var item = await reader.ReadAsync(owner.OrganizationId, owner.Id, proposal.Id);
        Assert.False(item!.CanDecide);
        Assert.Equal("Reduce the scope", item.DecisionComment);
        Assert.Equal(owner.DisplayName, item.ActualDecisionMaker);
    }

    [Fact]
    public async Task ApprovalToolRejectsForeignProposalAndDeduplicatesSameProposalInPrivateChat()
    {
        await using var db = Database();
        var (owner, agent, proposal, message) = await Seed(db);
        var resolver = new ApprovalUserActionWorkflowResolver(db);
        var parameters = JsonSerializer.SerializeToElement(new { approvalId = proposal.Id, url = "https://untrusted.example" });
        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve(owner.OrganizationId, Guid.NewGuid(), parameters));
        Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve(Guid.NewGuid(), agent.AgentInstallationId!.Value, parameters));
        var service = new UserActionService(db, [resolver]);
        var request = new SuggestUserActionRequest(message.Id, null, SuggestedUserActionWorkflows.ReviewApproval,
            "Review project", "Project ready for review", parameters, "first");
        var first = await service.SuggestAsync(owner.OrganizationId, agent.AgentInstallationId!.Value, request);
        var duplicate = await service.SuggestAsync(owner.OrganizationId, agent.AgentInstallationId.Value, request with { IdempotencyKey = "retry" });
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(proposal.Id, first.ApprovalId);
        Assert.Equal($"/organizations/{owner.OrganizationId:D}/approvals?approvalId={proposal.Id:D}", first.NavigationUri);
        Assert.Single(await db.SuggestedUserActions.ToListAsync());
        Assert.Equal(2, await db.CoreConversationMessages.CountAsync());
        var cancelled = await db.SuggestedUserActions.SingleAsync();
        cancelled.Status = SuggestedUserActionStatuses.Cancelled;
        await db.SaveChangesAsync();
        var replacement = await service.SuggestAsync(owner.OrganizationId, agent.AgentInstallationId.Value,
            request with { IdempotencyKey = "new-turn" });
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(2, await db.SuggestedUserActions.CountAsync());
    }

    [Fact]
    public async Task ApprovalToolCannotExposeProposalIntoPublicConversation()
    {
        await using var db = Database();
        var (owner, agent, proposal, message) = await Seed(db);
        message.Conversation!.IsPrivate = false; await db.SaveChangesAsync();
        var service = new UserActionService(db, [new ApprovalUserActionWorkflowResolver(db)]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SuggestAsync(owner.OrganizationId,
            agent.AgentInstallationId!.Value, new(message.Id, null, SuggestedUserActionWorkflows.ReviewApproval,
                "Review", null, JsonSerializer.SerializeToElement(new { approvalId = proposal.Id }), "public")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"channelId\":3,\"payloadHash\":false,\"idempotencyKey\":[]}")]
    public async Task UnreadablePlanHasHelpfulErrorAndNoReviewData(string payload)
    {
        await using var db = Database();
        var (owner, _, proposal, _) = await Seed(db);
        proposal.PayloadJson = payload; await db.SaveChangesAsync();
        var item = await new ProjectApprovalReader(db).ReadAsync(owner.OrganizationId, owner.Id, proposal.Id);
        Assert.NotNull(item!.ReviewError);
        Assert.Null(item.ProjectCreation);
    }

    internal static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    internal static async Task<(OrganizationUser Owner, OrganizationUser Agent, ActionProposal Proposal, ConversationMessage Message)> Seed(CSweetDbContext db)
    {
        var org = Guid.NewGuid();
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, ApplicationUserId = Guid.NewGuid(),
            DisplayName = "Matt", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
        var agent = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, AgentInstallationId = Guid.NewGuid(),
            DisplayName = "Naomi", EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = owner.Id };
        var plan = new W.WorkstreamPlanProposalV2Request("Prism Break", $"Deliver the accepted vision {new string('a', 64)}.",
            ["Playable prototype"], "Concept", agent.Id, null, [], [], null, null, null, null, "Test the concept", "plan",
            "video-game", 1, JsonSerializer.SerializeToElement(new { }), new(null, 14, ["game-engineer"], ["launch"], ["work-planning"], null), [], []);
        var proposal = new ActionProposal { Id = Guid.NewGuid(), OrganizationId = org, AgentInstallationId = agent.AgentInstallationId.Value,
            ActionType = ProjectApprovalReader.ActionType, Summary = "Create Workstream: Prism Break", IdempotencyKey = "plan",
            CreatedAt = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new { channelId = "platform-capability", actionType = "workstream-governance",
                payloadHash = new string('b', 64), idempotencyKey = "plan", alwaysRequiresApproval = true, payload = plan }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = org, IsPrivate = true, IsDeletionProtected = true,
            Participants = [new() { Id = Guid.NewGuid(), OrganizationUserId = owner.Id }, new() { Id = Guid.NewGuid(), OrganizationUserId = agent.Id }] };
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat.Id, Conversation = chat,
            SenderOrganizationUserId = agent.Id, Content = "Review project", Role = ConversationRole.Assistant };
        db.AddRange(new Organization { Id = org, Name = "Studio" }, owner, agent, proposal, chat, message);
        await db.SaveChangesAsync();
        return (owner, agent, proposal, message);
    }
}
