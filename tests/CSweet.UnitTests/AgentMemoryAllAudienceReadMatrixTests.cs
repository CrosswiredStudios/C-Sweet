using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static readonly string[] RemainingReadAudiences = ["Employee", "Relationship", "Organization", "Team", "Role",
        "InstallationEmployee", "InstallationRelationship"];
    public static IEnumerable<object[]> AllAudienceMemoryReadData() => RemainingReadAudiences
        .SelectMany(kind => ScopedReadRoutes.Select(route => new object[] { kind, route }));

    private static async Task BindAudienceChatWorkAsync(DurabilityFixture fixture, AgentWorkItem work)
    {
        await using var db = fixture.Context();
        var conversation = await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).Select(x => x.ConversationId).SingleAsync();
        var question = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation,
            SenderOrganizationUserId = fixture.HumanId, Role = ConversationRole.User, Content = "What is my name?", CreatedAt = DateTimeOffset.UtcNow };
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = conversation,
            UserMessageId = question.Id, TargetAgentOrganizationUserId = fixture.EmployeeId, Attempt = 1, Status = ChatTurnStatus.RecallingMemory,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.CoreConversationMessages.Add(question); db.ChatTurns.Add(turn); await db.SaveChangesAsync();
        await db.AgentWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceType, "chat-turn")
            .SetProperty(x => x.SourceId, turn.Id.ToString("D")));
        // Do not also capture the original chat message; this fixture already has its own source episode.
    }

    private static async Task<(MemoryEpisode Episode, Guid Audience)> SeedReadAudienceAsync(DurabilityFixture fixture, string kind)
    {
        if (kind is "Organization" or "Team" or "Role" or "InstallationEmployee" or "InstallationRelationship")
        {
            var shared = await SeedOperatorAudienceAsync(fixture, kind, sensitivity:
                kind is "Organization" or "Team" or "Role" ? MemorySensitivity.Internal : MemorySensitivity.Personal);
            return (shared.Episode, shared.Audience);
        }
        var space = kind switch
        {
            "Employee" => EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), fixture.EmployeeId.ToString("D"), "csweet"),
            "Relationship" => EmployeeMemoryNamespaces.UserRelationship(fixture.OrganizationId.ToString("D"), fixture.EmployeeId.ToString("D"), fixture.HumanId.ToString("D"), "csweet"),
            _ => throw new ArgumentException(nameof(kind))
        };
        var source = await SeedJoblessProposalAsync(fixture, x => x with { Partition = space.Partition, Scope = space.Scope });
        return (source.Episode, Guid.Empty);
    }

    private static async Task SetReadAudienceRevokedAsync(DurabilityFixture fixture, string kind, Guid audience, bool revoked)
    {
        await using var db = fixture.Context();
        if (kind == "Team") await db.TeamMemberships.Where(x => x.OrganizationUserId == fixture.EmployeeId && x.TeamId == audience)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndedAt, revoked ? DateTimeOffset.UtcNow : (DateTimeOffset?)null));
        else if (kind == "Role") await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RoleId, revoked ? null : (Guid?)audience));
        else if (kind is "Relationship" or "InstallationRelationship") await db.CoreConversations
            .Where(x => x.AgentOrganizationUserId == fixture.EmployeeId && x.InitiatedByOrganizationUserId == fixture.HumanId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ArchivedAt, revoked ? DateTimeOffset.UtcNow : (DateTimeOffset?)null));
        else await db.AgentInstallations.Where(x => x.Id == fixture.InstallationId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.IsEnabled, !revoked));
    }

    [MemoryPostgresTheory]
    [MemberData(nameof(AllAudienceMemoryReadData))]
    public async Task AllAudienceMemoryReadsReturnActualRecordsAndRecheckRevocation(string kind, string route)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedReadAudienceAsync(fixture, kind); var records = await SeedScopedRecordsAsync(fixture, source.Episode);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindAudienceChatWorkAsync(fixture, work);
        await using var db = fixture.Context(); var handler = ReadHandler(fixture, db);
        var request = ScopedRouteRequest(source.Episode, records, route);
        AssertScopedRouteRecords(await handler.HandleAsync(session, request, default), source.Episode, records, route);
        Assert.DoesNotContain("Alice", Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync()).EvidenceJson);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await SetReadAudienceRevokedAsync(fixture, kind, source.Audience, true);
        var denied = await handler.HandleAsync(session, request, default);
        Assert.False(denied.Succeeded); Assert.Equal("memory_policy_denied", denied.FailureCode);
        Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8()); Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        await SetReadAudienceRevokedAsync(fixture, kind, source.Audience, false);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [MemberData(nameof(AllAudienceMemoryReadData))]
    public async Task AllAudienceMemorySuppressionWithholdsEveryReadRouteAndOldDispatch(string kind, string route)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedReadAudienceAsync(fixture, kind); var records = await SeedScopedRecordsAsync(fixture, source.Episode);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindAudienceChatWorkAsync(fixture, work);
        await using var db = fixture.Context(); var handler = ReadHandler(fixture, db);
        var request = ScopedRouteRequest(source.Episode, records, route);
        AssertScopedRouteRecords(await handler.HandleAsync(session, request, default), source.Episode, records, route);
        await ((IMemorySuppressionStore)fixture.Store).SuppressEpisodeAsync(source.Episode.Partition, source.Episode.Id);
        var withheld = await handler.HandleAsync(session, request, default);
        Assert.DoesNotContain("Alice", withheld.Payload.ToStringUtf8()); Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
    }

    [MemoryPostgresTheory]
    [InlineData("Employee")] [InlineData("Relationship")] [InlineData("Organization")]
    [InlineData("Team")] [InlineData("Role")] [InlineData("InstallationEmployee")] [InlineData("InstallationRelationship")]
    public async Task AllAudienceMemoryForeignRecordsCannotBeReadByRawIdOrAnyRoute(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedReadAudienceAsync(fixture, kind);
        var foreign = source.Episode with { Id = Guid.NewGuid(), IdempotencyKey = null,
            Partition = source.Episode.Partition with { TenantId = Guid.NewGuid().ToString("D") } };
        await fixture.Store.AppendEpisodeAsync(foreign); var records = await SeedScopedRecordsAsync(fixture, foreign);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindAudienceChatWorkAsync(fixture, work);
        await using var db = fixture.Context(); var handler = ReadHandler(fixture, db);
        foreach (var route in ScopedReadRoutes)
        {
            var denied = await handler.HandleAsync(session, ScopedRouteRequest(foreign, records, route), default);
            Assert.False(denied.Succeeded); Assert.Equal("memory_policy_denied", denied.FailureCode); Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8());
        }
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Employee")] [InlineData("Relationship")] [InlineData("Organization")]
    [InlineData("Team")] [InlineData("Role")] [InlineData("InstallationEmployee")] [InlineData("InstallationRelationship")]
    public async Task AllAudienceMemoryUnobservedRevocationAndRestorationCannotReviveReadEvidence(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var source = await SeedReadAudienceAsync(fixture, kind); var records = await SeedScopedRecordsAsync(fixture, source.Episode);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture); await BindAudienceChatWorkAsync(fixture, work);
        await using var db = fixture.Context();
        AssertScopedRouteRecords(await ReadHandler(fixture, db).HandleAsync(session,
            ScopedRouteRequest(source.Episode, records, "episode"), default), source.Episode, records, "episode");
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        // No intervening read/dispatch observes the revoked state. Restoring identical
        // values must not revive the earlier receipt after two committed transitions.
        await SetReadAudienceRevokedAsync(fixture, kind, source.Audience, true);
        await SetReadAudienceRevokedAsync(fixture, kind, source.Audience, false);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }
}
