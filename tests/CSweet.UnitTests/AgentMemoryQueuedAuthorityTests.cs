using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    public static IEnumerable<object[]> QueuedAuthorityData() =>
        new[] { "sender", "employee-manager", "installation", "grant", "conversation", "sender-membership",
            "employee-membership", "membership-recreated", "sender-role", "role-definition", "team-definition", "participant" }
        .SelectMany(change => new[] { false, true }.Select(memory => new object[] { change, memory }));

    private sealed record QueuedAuthorityFixture(Guid Conversation, Guid Team, Guid Role);

    private static async Task<QueuedAuthorityFixture> SeedQueuedAuthorityAsync(DurabilityFixture fixture)
    {
        await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var conversation = await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).Select(x => x.ConversationId).SingleAsync();
        var role = Guid.NewGuid(); var team = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        db.CoreRoles.Add(new Role { Id = role, OrganizationId = fixture.OrganizationId, Name = "Designer" });
        db.OrganizationTeams.Add(new OrganizationTeam { Id = team, OrganizationId = fixture.OrganizationId,
            Name = "Design", NormalizedName = "DESIGN", TeamKey = "design", LeadOrganizationUserId = fixture.HumanId });
        db.TeamMemberships.AddRange(new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            TeamId = team, OrganizationUserId = fixture.EmployeeId, ExclusiveAgentEmployeeId = fixture.EmployeeId, JoinedAt = now },
            new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                TeamId = team, OrganizationUserId = fixture.HumanId, JoinedAt = now });
        db.ConversationParticipants.AddRange(new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversation,
            OrganizationUserId = fixture.EmployeeId, JoinedAt = now },
            new ConversationParticipant { Id = Guid.NewGuid(), ConversationId = conversation, OrganizationUserId = fixture.HumanId, JoinedAt = now });
        await db.SaveChangesAsync();
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId || x.Id == fixture.HumanId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RoleId, role));
        return new(conversation, team, role);
    }

    private static async Task RestoreQueuedAuthorityWithoutObservationAsync(DurabilityFixture fixture, QueuedAuthorityFixture audience, string change)
    {
        await using var db = fixture.Context();
        switch (change)
        {
            case "sender":
                await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
                await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true)); break;
            case "employee-manager":
                await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReportsToOrganizationUserId, (Guid?)null));
                await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReportsToOrganizationUserId, fixture.HumanId)); break;
            case "installation":
                await db.AgentInstallations.Where(x => x.Id == fixture.InstallationId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
                await db.AgentInstallations.Where(x => x.Id == fixture.InstallationId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, true)); break;
            case "grant":
                var required = await db.AgentInstallationGrants.Select(x => x.RequiredCapabilitiesJson).SingleAsync();
                await db.AgentInstallationGrants.ExecuteUpdateAsync(s => s.SetProperty(x => x.RequiredCapabilitiesJson, "[]"));
                await db.AgentInstallationGrants.ExecuteUpdateAsync(s => s.SetProperty(x => x.RequiredCapabilitiesJson, required)); break;
            case "conversation":
                await db.CoreConversations.Where(x => x.Id == audience.Conversation).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
                await db.CoreConversations.Where(x => x.Id == audience.Conversation).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTimeOffset?)null)); break;
            case "sender-membership": case "employee-membership":
                var person = change == "sender-membership" ? fixture.HumanId : fixture.EmployeeId;
                await db.TeamMemberships.Where(x => x.OrganizationUserId == person).ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, DateTimeOffset.UtcNow));
                await db.TeamMemberships.Where(x => x.OrganizationUserId == person).ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, (DateTimeOffset?)null)); break;
            case "membership-recreated":
                var membership = await db.TeamMemberships.AsNoTracking().SingleAsync(x => x.OrganizationUserId == fixture.HumanId);
                await db.TeamMemberships.Where(x => x.Id == membership.Id).ExecuteDeleteAsync();
                db.TeamMemberships.Add(membership); await db.SaveChangesAsync(); break;
            case "sender-role":
                await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RoleId, (Guid?)null));
                await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RoleId, audience.Role)); break;
            case "role-definition":
                var authority = await db.CoreRoles.Where(x => x.Id == audience.Role).Select(x => x.AuthorityLevel).SingleAsync();
                await db.CoreRoles.Where(x => x.Id == audience.Role).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthorityLevel, (AuthorityLevel)((int)authority + 1)));
                await db.CoreRoles.Where(x => x.Id == audience.Role).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthorityLevel, authority)); break;
            case "team-definition":
                await db.OrganizationTeams.Where(x => x.Id == audience.Team).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, DateTimeOffset.UtcNow));
                await db.OrganizationTeams.Where(x => x.Id == audience.Team).ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTimeOffset?)null)); break;
            case "participant":
                await db.ConversationParticipants.Where(x => x.ConversationId == audience.Conversation && x.OrganizationUserId == fixture.HumanId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeftAt, DateTimeOffset.UtcNow));
                await db.ConversationParticipants.Where(x => x.ConversationId == audience.Conversation && x.OrganizationUserId == fixture.HumanId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeftAt, (DateTimeOffset?)null)); break;
            default: throw new ArgumentException(nameof(change));
        }
    }

    [MemoryPostgresTheory]
    [MemberData(nameof(QueuedAuthorityData))]
    public async Task QueuedAuthorityRestorationBeforeClaimRejectsFrozenWork(string change, bool memory)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var audience = await SeedQueuedAuthorityAsync(fixture); var turn = await SeedRecallTurnAsync(fixture);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn, memory);
        await new MemoryRecallDispatchEvidence(db).AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default);
        await RestoreQueuedAuthorityWithoutObservationAsync(fixture, audience, change);
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        await using var current = fixture.Context();
        var rejected = await current.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id);
        Assert.Equal(AgentWorkStatus.DeadLetter, rejected.Status); Assert.Equal(0, rejected.AttemptCount);
        Assert.Empty(await current.AgentWorkAttempts.Where(x => x.AgentWorkItemId == work.Id).ToArrayAsync());
        Assert.Empty(await current.AgentMemoryReadReceipts.ToArrayAsync());
        Assert.Null((await current.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [MemberData(nameof(QueuedAuthorityData))]
    public async Task QueuedAuthorityRestorationAfterDeliveryResetsBeforeDispatch(string change, bool memory)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var audience = await SeedQueuedAuthorityAsync(fixture); var turn = await SeedRecallTurnAsync(fixture);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn, memory);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        var delivered = Assert.Single(await db.AgentMemoryReadReceipts.AsNoTracking().ToArrayAsync());
        await RestoreQueuedAuthorityWithoutObservationAsync(fixture, audience, change);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        await using var current = fixture.Context();
        Assert.NotNull((await current.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        Assert.Equal(MemoryRuntimeResetRequiredException.RetainedEvidence, (await current.AgentRuntimeInstances.SingleAsync()).MemoryResetReasonCode);
        Assert.Equal(delivered.EvidenceJson, (await current.AgentMemoryReadReceipts.AsNoTracking().SingleAsync()).EvidenceJson);
        Assert.DoesNotContain("Alice", Assert.Single(await current.AgentRuntimeEvents.ToArrayAsync()).Reason);
    }

    [MemoryPostgresTheory]
    [MemberData(nameof(QueuedAuthorityData))]
    public async Task QueuedAuthorityRestorationOnReconnectUsesFreshTaskEvidenceWithoutReset(string change, bool memory)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var audience = await SeedQueuedAuthorityAsync(fixture); var turn = await SeedRecallTurnAsync(fixture);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn, memory);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        var historical = await db.AgentMemoryReadReceipts.AsNoTracking().SingleAsync();
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteDeleteAsync();
        await db.ChatTurns.Where(x => x.Id == turn.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ChatTurnStatus.Completed));
        await RestoreQueuedAuthorityWithoutObservationAsync(fixture, audience, change);
        var next = await SeedRecallTurnAsync(fixture);
        // This fresh attempt uses restored current authority without inheriting or
        // retroactively certifying the completed callback's historical context.
        await using var reconnected = fixture.Context();
        var (nextInbox, nextWork) = await QueueRecallAsync(fixture, reconnected, next, includeMemory: false);
        var delivered = Assert.IsType<ClaimedAgentWork>(await nextInbox.ClaimAsync(DeliverySession(session), default));
        Assert.Equal(nextWork.Id, delivered.WorkId);
        await new PlatformMemoryReadEvidence(reconnected).AuthorizeDispatchAsync(session, nextWork.Id, default, delivered.Attempt);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() =>
            new PlatformMemoryReadEvidence(reconnected).AuthorizeDispatchAsync(session, work.Id, default, 1));
        var currentConsumer = await reconnected.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == nextWork.Id);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() =>
            new MemoryRecallDispatchEvidence(reconnected).AuthorizeRetainedDeliveryAsync(historical, currentConsumer, default));
        await using var current = fixture.Context();
        var active = await current.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == nextWork.Id);
        Assert.Equal(AgentWorkStatus.Leased, active.Status); Assert.Equal(1, active.AttemptCount);
        Assert.Single(await current.AgentWorkAttempts.Where(x => x.AgentWorkItemId == nextWork.Id).ToArrayAsync());
        Assert.Equal(2, await current.AgentMemoryReadReceipts.CountAsync());
        Assert.Equal(historical.EvidenceJson, (await current.AgentMemoryReadReceipts.SingleAsync(x => x.WorkId == work.Id)).EvidenceJson);
        Assert.Null((await current.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task QueuedAuthorityOrdinaryProgressAndRolledBackRevocationKeepDispatchValid()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var audience = await SeedQueuedAuthorityAsync(fixture); var turn = await SeedRecallTurnAsync(fixture);
        var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context(); await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        await db.AgentInstallations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ConfigurationSyncLastAttemptAt, DateTimeOffset.UtcNow));
        await db.OrganizationTeams.ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, "Ordinary team progress"));
        await db.TeamMemberships.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceType, "updated label"));
        await db.ConversationParticipants.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReadMessageSequence, 50L));
        await db.CoreConversations.Where(x => x.Id == audience.Conversation).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "Updated title"));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.AgentInstallations.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
            await transaction.RollbackAsync();
        }
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }
}
