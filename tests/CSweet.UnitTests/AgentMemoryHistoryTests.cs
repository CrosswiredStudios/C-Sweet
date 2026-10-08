using CSweet.Contracts.Memory;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task MemoryHistoryRequiresTopLevelAuthorityForSharedOrganizationAndBoundsPayloads()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var partition = EmployeeMemoryNamespaces.Organization(fixture.OrganizationId.ToString("D"), "csweet").Partition;
        var now = DateTimeOffset.UtcNow;
        var episode = new MemoryEpisode(Guid.NewGuid(), partition, MemoryScope.Tenant, "Shared policy", "text/plain", new("user", "policy"),
            "checksum", now, now, Sensitivity: MemorySensitivity.Internal);
        await fixture.Store.AppendEpisodeAsync(episode);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        Assert.Equal("Organization", (await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", episode.Id, actor)).Scope);
        var oversized = episode with { Id = Guid.NewGuid(), Partition = fixture.Partition, Scope = MemoryScope.User, Content = new string('x', 1_048_576) };
        await fixture.Store.AppendEpisodeAsync(oversized);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", oversized.Id, actor));
        var ancestor = new CSweet.Domain.Core.OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            EmployeeType = CSweet.Domain.Core.EmployeeType.Human };
        db.CoreOrganizationUsers.Add(ancestor); await db.SaveChangesAsync();
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReportsToOrganizationUserId, ancestor.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", episode.Id, actor));
    }

    [MemoryPostgresFact]
    public async Task MemoryHistoryPagesFrozenClaimsAndAttributesOriginalAndCorrectedRecords()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DisplayName, "Reviewer"));
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        var result = await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", "detailed replies"));
        var first = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Claim", claim.Id, actor, limit: 1);
        Assert.Equal("concise replies", Assert.Single(first.Items).Content); Assert.Empty(first.Items[0].Reviews); Assert.NotNull(first.NextAfterRevision);
        var next = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Claim", claim.Id, actor, first.NextAfterRevision!.Value, 1);
        Assert.Equal("concise replies", Assert.Single(next.Items).Content); Assert.NotNull(next.Items[0].ValidTo);
        var review = Assert.Single(next.Items[0].Reviews);
        Assert.Equal(result.ReceiptId, review.ReceiptId); Assert.Equal("Reviewer", review.ReviewerName); Assert.Equal(result.ResultClaimId, review.RelatedRecord!.Id);
        var replacement = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Claim", result.ResultClaimId, actor);
        Assert.Equal("detailed replies", Assert.Single(replacement.Items).Content);
        Assert.Equal(claim.Id, Assert.Single(replacement.Items[0].Reviews).RelatedRecord!.Id);
        Assert.Contains(replacement.Items[0].Sources, x => x.Id == claim.EpisodeId);
        var source = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", claim.EpisodeId, actor);
        Assert.Contains("Alice", Assert.Single(source.Items).Content);
        var empty = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Claim", claim.Id, actor, next.Items[0].Revision);
        Assert.Empty(empty.Items); Assert.Null(empty.NextAfterRevision);
    }

    [MemoryPostgresFact]
    public async Task MemoryHistoryShowsDeletedSnapshotsButScopePurgeRemovesHistory()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var procedure = await SeedReviewProcedure(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_procedures WHERE id={procedure.Id}");
        var history = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Procedure", procedure.Id, actor);
        Assert.Equal(2, history.Items.Count); Assert.Equal("Delete", history.Items[^1].Operation);
        Assert.Equal(procedure.Procedure, history.Items[^1].Content); Assert.Equal(procedure.Applicability, history.Items[^1].Applicability);
        await fixture.Store.DeleteScopeAsync(fixture.Partition);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Procedure", procedure.Id, actor));
    }

    [MemoryPostgresFact]
    public async Task MemoryHistoryReauthorizesEveryPageAndCannotResolveOtherRelationshipOrInstallationScopes()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        var foreign = claim with { Id = Guid.NewGuid(), Partition = claim.Partition with { UserId = Guid.NewGuid().ToString("D") } };
        await fixture.Store.WriteClaimAsync(foreign);
        var privateClaim = claim with { Id = Guid.NewGuid(), Partition = claim.Partition with { CustomNamespace = "installation-private" } };
        await fixture.Store.WriteClaimAsync(privateClaim);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Claim", foreign.Id, actor));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Claim", privateClaim.Id, actor));
        var first = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Claim", claim.Id, actor);
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Claim", claim.Id, actor, first.Items[^1].Revision));
    }

    [MemoryPostgresFact]
    public async Task MemoryHistoryProjectsAllDisplayedKindsAndOmitsEmbeddingVectors()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var claim = await SeedReviewClaim(fixture);
        var now = DateTimeOffset.UtcNow;
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "Core notes", "Reviewed core", 1, 100, true, MemoryTrustTier.ConfirmedUser, now)
            { SourceEpisodeIds = [claim.EpisodeId], Sensitivity = MemorySensitivity.Personal };
        var edge = new MemoryEdge(Guid.NewGuid(), fixture.Partition, claim.EpisodeId, claim.SubjectEntityId, "knows", claim.SubjectEntityId,
            MemoryTrustTier.AgentInference, 1, now, null, true, now);
        var embedding = new MemoryEmbedding(Guid.NewGuid(), fixture.Partition, claim.EpisodeId, MemoryLayer.Episodic, [0.123456f, 0.765432f], "test-model", now);
        await fixture.Store.WriteBlockAsync(block); await fixture.Store.WriteEdgeAsync(edge); await fixture.Store.WriteEmbeddingAsync(embedding);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        Assert.Equal("Alice", Assert.Single((await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Entity", claim.SubjectEntityId, actor)).Items).Title);
        Assert.Equal("Reviewed core", Assert.Single((await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Core", block.Id, actor)).Items).Content);
        Assert.Equal(2, Assert.Single((await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Relationship", edge.Id, actor)).Items).RelatedRecords.Count);
        var vector = Assert.Single((await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Embedding", embedding.Id, actor)).Items);
        Assert.Contains("Dimensions: 2", vector.Content); Assert.DoesNotContain("0.123456", vector.Content); Assert.Equal(claim.EpisodeId, Assert.Single(vector.Sources).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Claim", claim.Id, actor, limit: 21));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Transfer", claim.Id, actor));
    }
}
