using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("Team", true)]
    [InlineData("Role", true)]
    [InlineData("Team", false)]
    [InlineData("Role", false)]
    public async Task ScopedAudienceMixedContributorsRemainRequiredThroughEntityCorrectionNestedCopyAndDispatch(string kind, bool revokeHuman)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, teamSource, teamId) = await SeedOperatorAudienceAsync(fixture, "Team");
        var roleId = Guid.NewGuid(); var roleEpisodeId = Guid.NewGuid();
        var roleAudience = EmployeeMemoryNamespaces.Role(fixture.OrganizationId.ToString("D"), roleId.ToString("D"), "csweet");
        var roleSource = teamSource with { Id = roleEpisodeId, Partition = roleAudience.Partition, Scope = roleAudience.Scope,
            Source = teamSource.Source with { Id = roleEpisodeId.ToString("D") }, IdempotencyKey = "mixed-role:" + roleEpisodeId.ToString("D") };
        await using (var seed = fixture.Context())
        {
            seed.CoreRoles.Add(new Role { Id = roleId, OrganizationId = fixture.OrganizationId, Name = "Designer" });
            await seed.SaveChangesAsync();
            await seed.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId || x.Id == fixture.HumanId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.RoleId, roleId));
        }
        await fixture.Store.AppendEpisodeAsync(roleSource);
        var owner = await AddSharedRecipientAsync(fixture, "Role", roleId);
        var recipient = await AddSharedRecipientAsync(fixture, "Role", roleId);
        await using var db = fixture.Context();
        foreach (var person in new[] { owner, recipient })
            db.TeamMemberships.Add(new TeamMembership { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                TeamId = teamId, OrganizationUserId = person, ExclusiveAgentEmployeeId = person, JoinedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();
        var transfer = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var team = await ApplySharedAsync(transfer, fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), owner, "Team", [new("Episode", teamSource.Id)], "Alice team evidence", SourceAudienceId: teamId));
        var role = await ApplySharedAsync(transfer, fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), owner, "Role", [new("Episode", roleSource.Id)], "Alice role evidence", SourceAudienceId: roleId));
        var partition = EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), owner.ToString("D"), "csweet").Partition;
        var recipientPartition = EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), recipient.ToString("D"), "csweet").Partition;
        var now = DateTimeOffset.UtcNow;
        var subject = new MemoryEntity(Guid.NewGuid(), partition, "person", "Alice", [], null, false, now, now)
            { SourceEpisodeIds = [team.AppliedEpisodeId!.Value], Sensitivity = MemorySensitivity.Personal };
        var previous = subject with { Id = Guid.NewGuid(), CanonicalName = "Bob" };
        var target = subject with { Id = Guid.NewGuid(), CanonicalName = "Carol", SourceEpisodeIds = [role.AppliedEpisodeId!.Value] };
        await fixture.Store.UpsertEntityAsync(subject); await fixture.Store.UpsertEntityAsync(previous); await fixture.Store.UpsertEntityAsync(target);
        var claim = new MemoryClaim(Guid.NewGuid(), partition, team.AppliedEpisodeId.Value, subject.Id, "reports to", previous.Id, null,
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal, 1, 1, now, null, now)
            { SourceEpisodeIds = [team.AppliedEpisodeId.Value] };
        await fixture.Store.WriteClaimAsync(claim);
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetClaimAsync(fixture.OrganizationId, owner, claim.Id, user);
        var choice = Assert.Single(await review.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, owner, claim.Id, user, "car"));
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken,
            "correct", ReplacementEntity: new(target.Id, choice.EvidenceToken));
        var corrected = await review.ReviewClaimAsync(fixture.OrganizationId, owner, claim.Id, user, request);
        var correctedClaim = (await fixture.Store.GetClaimAsync(corrected.ResultClaimId))!;
        var store = (PostgreSqlMemoryStore)fixture.Store;
        var correction = (await store.GetEpisodeAsync(partition, correctedClaim.EpisodeId))!;
        Assert.Equal(2, MemorySharedAudiences.Required(correction)!.Count);
        var copyRequest = new PrepareMemoryTransferRequest(Guid.NewGuid(), recipient, "Employee", [new("Episode", correction.Id)], "Alice combined approved evidence");
        var nested = await ApplySharedAsync(transfer, fixture, owner, user, copyRequest);
        var copied = (await store.GetEpisodeAsync(recipientPartition, nested.AppliedEpisodeId!.Value))!;
        var required = MemorySharedAudiences.Required(copied)!;
        Assert.Equal(2, required.Count); Assert.Contains(teamSource.Partition, required); Assert.Contains(roleSource.Partition, required);
        Assert.NotEmpty((await review.ReadHistoryAsync(fixture.OrganizationId, recipient, "Episode", copied.Id, user)).Items);
        Assert.True((await review.GetHoldAsync(fixture.OrganizationId, recipient, copied.Id, user)).IsTransferred);
        var conversation = await db.CoreConversations.SingleAsync(x => x.AgentOrganizationUserId == recipient);
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id,
            SenderOrganizationUserId = fixture.HumanId, Content = "What does Alice need?", Role = ConversationRole.User, CreatedAt = now };
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, ConversationId = conversation.Id,
            UserMessageId = message.Id, TargetAgentOrganizationUserId = recipient, Attempt = 1,
            Status = ChatTurnStatus.RecallingMemory, CreatedAt = now, UpdatedAt = now };
        db.CoreConversationMessages.Add(message); db.ChatTurns.Add(turn); await db.SaveChangesAsync();
        var memory = fixture.Service(db, new UsageProviderFactory());
        var recall = await memory.PrepareTurnRecallAsync(turn.Id); Assert.Contains("Alice", recall.Context);
        var installation = (await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.Id == recipient)).AgentInstallationId!.Value;
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId.ToString("D"), AgentInstallationId = installation,
            SourceType = "chat-turn", SourceId = turn.Id.ToString("D"), PayloadHash = "mixed-sources",
            MemoryRecallReceiptJson = MemoryRecallDispatchEvidence.BindPayload(recall.ReceiptJson, "mixed-sources",
                fixture.OrganizationId.ToString("D"), installation, "chat-turn", turn.Id.ToString("D")) };
        var guard = new MemoryRecallDispatchEvidence(db); await guard.AuthorizeWorkAsync(work, recipient.ToString("D"), default);
        var revoked = revokeHuman ? fixture.HumanId : recipient;
        if (kind == "Team") await db.TeamMemberships.Where(x => x.OrganizationUserId == revoked && x.TeamId == teamId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndedAt, DateTimeOffset.UtcNow));
        else await db.CoreOrganizationUsers.Where(x => x.Id == revoked).ExecuteUpdateAsync(x => x.SetProperty(p => p.RoleId, (Guid?)null));
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => guard.AuthorizeWorkAsync(work, recipient.ToString("D"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetHoldAsync(fixture.OrganizationId, recipient, copied.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReadHistoryAsync(fixture.OrganizationId, recipient, "Episode", copied.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfer.PrepareAsync(fixture.OrganizationId, owner, user, copyRequest));
        Assert.Null(await memory.GetItemAsync(fixture.OrganizationId, recipient, copied.Id, applicationUserId: user));
        Assert.Null((await memory.PrepareTurnRecallAsync(turn.Id)).Context);
        if (revokeHuman) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReviewClaimAsync(fixture.OrganizationId, owner, claim.Id, user, request));
        else Assert.True((await review.ReviewClaimAsync(fixture.OrganizationId, owner, claim.Id, user, request)).WasReplay);
    }
}
