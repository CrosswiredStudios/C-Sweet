using CSweet.Contracts.Memory;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(MemoryClaim Claim, MemoryEntity Target, MemoryEpisode Source)> SeedScopedEntityReviewAsync(
        DurabilityFixture fixture, ScopedAudienceFixture scoped)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var subject = new MemoryEntity(Guid.NewGuid(), scoped.Episode.Partition, "person", "Alice", [], null, false, now, now)
            { SourceEpisodeIds = [scoped.Episode.Id], Sensitivity = MemorySensitivity.Personal };
        var previous = subject with { Id = Guid.NewGuid(), CanonicalName = "Bob" };
        await fixture.Store.UpsertEntityAsync(subject); await fixture.Store.UpsertEntityAsync(previous);
        var source = new MemoryEpisode(Guid.NewGuid(), scoped.Episode.Partition, scoped.Episode.Scope,
            "Carol is the new manager", "text/plain", new("user", "new-manager", fixture.HumanId.ToString("D")),
            "checksum", now, now, ExpiresAt: now.AddDays(1), LegalHold: true, Sensitivity: MemorySensitivity.Confidential);
        await fixture.Store.AppendEpisodeAsync(source);
        var target = subject with { Id = Guid.NewGuid(), CanonicalName = "Carol", SourceEpisodeIds = [source.Id] };
        await fixture.Store.UpsertEntityAsync(target);
        var claim = new MemoryClaim(Guid.NewGuid(), scoped.Episode.Partition, scoped.Episode.Id, subject.Id,
            "reports to", previous.Id, null, MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending,
            MemorySensitivity.Personal, 1, 1, now, now.AddDays(2), now) { SourceEpisodeIds = [scoped.Episode.Id] };
        await fixture.Store.WriteClaimAsync(claim);
        return (claim, target, source);
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceEntityCorrectionPreservesScopeContributorsAndCurrentReplayAuthority(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        var (claim, target, source) = await SeedScopedEntityReviewAsync(fixture, scoped);
        await using var db = fixture.Context();
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User);
        var choice = Assert.Single(await review.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User, "car"));
        Assert.True(preview.IsEntityValued); Assert.Equal(target.Id, choice.EntityId); Assert.Equal("Confidential", choice.Sensitivity);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken,
            "correct", ReplacementEntity: new(target.Id, choice.EvidenceToken));
        var result = await review.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User, request);
        var corrected = (await fixture.Store.GetClaimAsync(result.ResultClaimId))!;
        var episode = (await ((PostgreSqlMemoryStore)fixture.Store).GetEpisodeAsync(scoped.Episode.Partition, corrected.EpisodeId))!;
        Assert.Equal(scoped.Episode.Scope, episode.Scope); Assert.Equal(target.Id, corrected.ObjectEntityId);
        Assert.Null(corrected.Value); Assert.Equal(MemorySensitivity.Confidential, corrected.Sensitivity);
        Assert.Contains(scoped.Episode.Id, corrected.SourceEpisodeIds); Assert.Contains(source.Id, corrected.SourceEpisodeIds);
        Assert.Equal(source.ExpiresAt, episode.ExpiresAt); Assert.True(episode.LegalHold);
        Assert.NotNull(episode.CorrectionEvidence); Assert.True(MemorySourceIntegrity.IsVerified(episode));
        Assert.Equal(2, episode.CorrectionEvidence.Sources.Count);
        Assert.Contains(await fixture.Store.SearchAsync(new(scoped.Episode.Partition, scoped.Episode.Scope, "Carol",
            Layers: new HashSet<MemoryLayer> { MemoryLayer.Episodic })), x => x.Id == episode.Id);
        Assert.True((await review.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User, request)).WasReplay);
        Assert.Single(await db.MemoryReviewReceipts.ToArrayAsync());
        if (kind == "Case") await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.HumanId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, DateTimeOffset.UtcNow));
        else await db.ConversationParticipants.Where(x => x.OrganizationUserId == fixture.HumanId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReviewClaimAsync(fixture.OrganizationId,
            fixture.EmployeeId, claim.Id, scoped.User, request));
        Assert.Single(await db.MemoryReviewReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Case")]
    [InlineData("Conversation")]
    public async Task ScopedAudienceEntityChoiceRejectsStaleForeignAndChangedThenRestoredAuthority(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, kind);
        var (claim, target, _) = await SeedScopedEntityReviewAsync(fixture, scoped);
        await using var db = fixture.Context();
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User);
        var choice = Assert.Single(await review.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User, "car"));
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken,
            "correct", ReplacementEntity: new(target.Id, choice.EvidenceToken));
        await fixture.Store.UpsertEntityAsync(target with { Aliases = ["new alias"] });
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => review.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User, request));
        var foreign = target with { Id = Guid.NewGuid(), Partition = kind == "Case"
            ? target.Partition with { CustomNamespace = "case:" + Guid.NewGuid().ToString("D") }
            : target.Partition with { ConversationId = Guid.NewGuid().ToString("D") } };
        await fixture.Store.UpsertEntityAsync(foreign);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => review.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId,
            claim.Id, scoped.User, request with { ReplacementEntity = new(foreign.Id, choice.EvidenceToken) }));
        choice = Assert.Single(await review.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User, "car"));
        if (kind == "Case")
        {
            await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, DateTimeOffset.UtcNow));
            await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, (DateTimeOffset?)null));
        }
        else
        {
            await db.ConversationParticipants.Where(x => x.OrganizationUserId == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt, DateTimeOffset.UtcNow));
            await db.ConversationParticipants.Where(x => x.OrganizationUserId == fixture.HumanId).ExecuteUpdateAsync(x => x.SetProperty(p => p.LeftAt, (DateTimeOffset?)null));
        }
        preview = await review.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User);
        request = request with { ExpectedRevision = preview.Revision, EvidenceToken = preview.EvidenceToken,
            ReplacementEntity = new(target.Id, choice.EvidenceToken) };
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => review.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User, request));
        Assert.Empty(await db.MemoryReviewReceipts.ToArrayAsync());
        var fresh = Assert.Single(await review.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User, "car"));
        Assert.NotEqual(choice.EvidenceToken, fresh.EvidenceToken);
        await review.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, scoped.User,
            request with { ReplacementEntity = new(target.Id, fresh.EvidenceToken) });
        Assert.Single(await db.MemoryReviewReceipts.ToArrayAsync());
    }
}
