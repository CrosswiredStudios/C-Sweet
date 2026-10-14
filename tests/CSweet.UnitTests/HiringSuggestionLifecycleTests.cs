using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class UserActionServiceTests
{
    [Fact]
    public async Task LateFulfilledSuggestion_DoesNotSupersedeTheNextRoleAndReusesCompletedCard()
    {
        await using var db = CreateDb();
        var f = await HiringFixtureAsync(db);
        var filled = await AddRecommendationAsync(db, f.Organization, f.Installation);
        var next = await AddRecommendationAsync(db, f.Organization, f.Installation);
        await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(filled, f.Hire, "hire"));
        var nextCard = await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.Message, next, "next"));

        // A fulfillment handler selected this role before the hire, but only attaches its card afterwards.
        var lateCard = await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.LaterMessage, filled, "late"));
        var replay = await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.LaterMessage, filled, "retry"));

        Assert.Equal("Completed", lateCard.Status);
        Assert.Equal(f.Hire, lateCard.ResultOrganizationUserId);
        Assert.Equal((await db.WorkforcePlans.FindAsync(filled))!.DecidedAt, lateCard.CompletedAt);
        Assert.Equal(lateCard.Id, replay.Id);
        Assert.Equal("Pending", (await db.SuggestedUserActions.FindAsync(nextCard.Id))!.Status);
        Assert.Equal(2, await db.SuggestedUserActions.CountAsync());
        Assert.Single(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Completed).ToListAsync());
        Assert.Empty(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Superseded).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fulfillment_CompletesExistingAndSupersededCardsWithParticipantEvents(bool superseded)
    {
        await using var db = CreateDb();
        var f = await HiringFixtureAsync(db);
        var target = await AddRecommendationAsync(db, f.Organization, f.Installation);
        var card = await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.Message, target, "first"));
        if (superseded)
        {
            var replacement = await AddRecommendationAsync(db, f.Organization, f.Installation);
            await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.LaterMessage, replacement, "replacement"));
            Assert.Equal("Superseded", (await db.SuggestedUserActions.FindAsync(card.Id))!.Status);
        }
        await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Hire, "hire"));
        await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Hire, "hire"));

        var completed = (await db.SuggestedUserActions.FindAsync(card.Id))!;
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(f.Hire, completed.ResultOrganizationUserId);
        Assert.Null(completed.SupersededAt);
        Assert.Null(completed.SupersededByActionId);
        Assert.Null(completed.SupersededByRole);
        var evt = Assert.Single(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Completed).ToListAsync());
        Assert.Equal(f.Conversation, evt.ChatId);
        Assert.Contains(f.Owner, JsonSerializer.Deserialize<Guid[]>(evt.RecipientOrganizationUserIdsJson)!);
        Assert.DoesNotContain(f.Hire, JsonSerializer.Deserialize<Guid[]>(evt.RecipientOrganizationUserIdsJson)!);
        using var payload = JsonDocument.Parse(evt.DataJson);
        Assert.Equal(card.Id, payload.RootElement.GetProperty("actionId").GetGuid());
        Assert.Equal(completed.ConversationMessageId, payload.RootElement.GetProperty("messageId").GetGuid());
        Assert.Equal(f.Hire, payload.RootElement.GetProperty("resultOrganizationUserId").GetGuid());
        var hub = new CommunicationHubService(db, new TestAuditEventWriter(), new ChatTurnService(db));
        var projected = (await hub.ListMessagesAsync(f.Organization, f.Conversation, f.Owner))!
            .SelectMany(x => x.Actions ?? []).Single(x => x.Id == card.Id);
        Assert.Equal("Completed", projected.Status);
        Assert.Equal("Rohan", projected.ResultOrganizationUserDisplayName);
    }

    [Fact]
    public async Task MultiSeatSuggestion_CompletesOnlyAfterFinalHire()
    {
        await using var db = CreateDb();
        var f = await HiringFixtureAsync(db);
        var target = await AddRecommendationAsync(db, f.Organization, f.Installation);
        var plan = (await db.WorkforcePlans.FindAsync(target))!;
        plan.Headcount = 2;
        await db.SaveChangesAsync();
        await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Hire, "first"));
        var card = await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.Message, target, "partial"));
        Assert.Equal("Pending", card.Status);
        Assert.Empty(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Completed).ToListAsync());
        await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Owner, "second"));
        Assert.Equal("Completed", (await db.SuggestedUserActions.FindAsync(card.Id))!.Status);
        Assert.Single(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Completed).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnBoundSuggestion_StillMaterializesWhenHireCompletesBeforeTurn(bool hiredBeforeSuggestion)
    {
        await using var db = CreateDb();
        var f = await HiringFixtureAsync(db);
        var target = await AddRecommendationAsync(db, f.Organization, f.Installation);
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            ConversationId = f.Conversation, TargetAgentOrganizationUserId = f.Agent,
            UserMessageId = f.Message, Status = ChatTurnStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.ChatTurns.Add(turn);
        await db.SaveChangesAsync();
        if (hiredBeforeSuggestion)
            await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Hire, "hire"));
        var card = await Actions(db).SuggestAsync(f.Organization, f.Installation,
            Request(f.Message, target, "turn") with { MessageId = null, ChatTurnId = turn.Id });
        if (!hiredBeforeSuggestion)
            await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Hire, "hire"));
        await new ChatTurnService(db).CompleteAsync(turn.Id, f.LaterMessage, false);
        var completed = (await db.SuggestedUserActions.FindAsync(card.Id))!;
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(f.Hire, completed.ResultOrganizationUserId);
        Assert.NotNull(completed.ConversationMessageId);
        Assert.Null(completed.ChatTurnId);
    }

    [Fact]
    public async Task WithdrawnSuggestion_IsNonActionableAndCannotSupersedeOtherRoles()
    {
        await using var db = CreateDb();
        var f = await HiringFixtureAsync(db);
        var withdrawn = await AddRecommendationAsync(db, f.Organization, f.Installation);
        await Hiring(db).WithdrawRecommendationAsync(f.Organization, f.Installation, new(withdrawn, "Withdraw", "withdraw"));
        var next = await AddRecommendationAsync(db, f.Organization, f.Installation);
        var card = await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.Message, next, "next"));
        var late = await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.LaterMessage, withdrawn, "late"));
        var retry = await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.LaterMessage, withdrawn, "retry"));
        Assert.Equal("Cancelled", late.Status);
        Assert.Equal(late.Id, retry.Id);
        Assert.Equal(2, await db.SuggestedUserActions.CountAsync());
        Assert.Equal("Pending", (await db.SuggestedUserActions.FindAsync(card.Id))!.Status);
    }

    [Fact]
    public async Task Suggestion_RejectsMissingAndCrossOrganizationRecommendation()
    {
        await using var db = CreateDb();
        var f = await HiringFixtureAsync(db);
        var inaccessible = await AddRecommendationAsync(db, Guid.NewGuid(), f.Installation);
        foreach (var id in new[] { inaccessible, Guid.NewGuid() })
            await Assert.ThrowsAsync<ArgumentException>(() => Actions(db).SuggestAsync(f.Organization, f.Installation,
                Request(f.Message, id, id.ToString("N"))));
        Assert.Empty(await db.SuggestedUserActions.ToListAsync());
    }

    private static UserActionService Actions(CSweetDbContext db) => new(db,
        new IUserActionWorkflowResolver[] { new HiringMarketplaceUserActionWorkflowResolver() });
    private static HiringService Hiring(CSweetDbContext db) => new(db,
        new OrganizationUserService(db, new TestAuditEventWriter()), new TestAuditEventWriter());
    private static SuggestUserActionRequest Request(Guid message, Guid target, string key) => new(
        message, null, SuggestedUserActionWorkflows.BrowseHiringMarketplace, "Browse candidates", "Own quality",
        JsonSerializer.SerializeToElement(new { role = "game-quality-assurance", recommendationId = target }), key);

    private sealed record HiringFixture(Guid Organization, Guid Installation, Guid Agent, Guid Owner,
        Guid Hire, Guid Conversation, Guid Message, Guid LaterMessage);

    private static async Task<HiringFixture> HiringFixtureAsync(CSweetDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var org = new Organization { Id = Guid.NewGuid(), Name = "Example", CreatedAt = now, UpdatedAt = now };
        var installation = Guid.NewGuid();
        if (db.Database.IsRelational())
            db.AgentInstallations.Add(new AgentInstallation {
                Id = installation, InstallationKey = installation, BusinessId = org.Id.ToString("D"),
                PackageVersion = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "chief", Version = "1.0.0", ManifestJson = "{}",
                    PackageSource = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/chief" } } });
        var chief = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, AgentInstallationId = installation,
            DisplayName = "Chief", EmployeeType = EmployeeType.Agent, PermissionLevel = OrganizationPermissionLevel.Manager, CreatedAt = now };
        var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Owner",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, CreatedAt = now };
        var hire = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Rohan",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Contributor, CreatedAt = now };
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = org.Id, AgentOrganizationUserId = chief.Id,
            InitiatedByOrganizationUserId = owner.Id, Kind = ConversationKind.DirectHumanAgent, CreatedAt = now, UpdatedAt = now };
        foreach (var user in new[] { chief, owner }) chat.Participants.Add(new ConversationParticipant {
            Id = Guid.NewGuid(), OrganizationUserId = user.Id, Role = ConversationParticipantRole.Member, JoinedAt = now });
        var message = Message(chat.Id, chief.Id, "Hiring suggestions", now, 1);
        var later = Message(chat.Id, chief.Id, "Next suggestion", now, 2);
        if (db.Database.IsRelational()) message.Sequence = later.Sequence = 0;
        db.AddRange(org, chief, owner, hire, chat, message, later);
        await db.SaveChangesAsync();
        return new(org.Id, installation, chief.Id, owner.Id, hire.Id, chat.Id, message.Id, later.Id);
    }
}
