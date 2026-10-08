using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task ErasureImpactIncludesHistoryAndAuthorizedAudiencesWithoutMutatingOrDisclosingPayloads()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var episode = await SeedLegacyEpisode(fixture, change: x => x with { LegalHold = false });
        var shared = EmployeeMemoryNamespaces.Organization(fixture.OrganizationId.ToString("D"), "csweet").Partition;
        var copy = episode with { Id = Guid.NewGuid(), Partition = shared, IdempotencyKey = "shared-copy" };
        await fixture.Store.AppendEpisodeAsync(copy);
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "Summary", "private-memory", 1, 100, false,
            MemoryTrustTier.UnconfirmedUser, DateTimeOffset.UtcNow) { SourceEpisodeIds = [episode.Id] };
        await fixture.Store.WriteBlockAsync(block);
        await using var db = fixture.Context();
        // This inventory case starts with no saved extraction output. Malformed retained
        // envelopes are covered separately by capture-retention review tests.
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.AcceptedExtractionJson, (string?)null)
            .SetProperty(x => x.ExtractionAcceptedAt, (DateTimeOffset?)null));
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_blocks WHERE id={block.Id}");
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var auditsBefore = await db.AuditOutbox.CountAsync();
        var jobBefore = JsonSerializer.Serialize(await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync());
        var before = await ((IMemoryErasureStore)fixture.Store).PreviewEpisodeErasureAsync(fixture.Partition, episode.Id);
        var impact = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        Assert.Equal(3, impact.RecordCount); Assert.Equal(2, impact.Audiences.Count); Assert.Null(impact.BlockedReason);
        Assert.Equal(1, Assert.Single(impact.Audiences, x => x.Scope == "Organization").Records.Single(x => x.Kind == "Episode").Count);
        Assert.Equal(1, Assert.Single(impact.Audiences, x => x.Scope == "Relationship").Records.Single(x => x.Kind == "Block").Count);
        var json = JsonSerializer.Serialize(impact);
        Assert.DoesNotContain("private-memory", json); Assert.DoesNotContain(episode.Content, json);
        Assert.DoesNotContain(fixture.HumanId.ToString(), json); Assert.DoesNotContain(block.Id.ToString(), json);
        Assert.DoesNotContain(before.EvidenceToken, json);
        Assert.Equal(before.EvidenceToken, (await ((IMemoryErasureStore)fixture.Store).PreviewEpisodeErasureAsync(fixture.Partition, episode.Id)).EvidenceToken);
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        Assert.Equal(auditsBefore, await db.AuditOutbox.CountAsync());
        Assert.Equal(jobBefore, JsonSerializer.Serialize(await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync()));
    }

    [MemoryPostgresTheory]
    [InlineData("held", "memory_legal_hold_prevents_deletion")]
    [InlineData("unlinked", "memory_erasure_lineage_review_required")]
    public async Task ErasureImpactReportsRetentionAndLineageBlockers(string scenario, string blocker)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var episode = await SeedLegacyEpisode(fixture, change: x => x with { LegalHold = scenario == "held" });
        if (scenario == "unlinked")
            await fixture.Store.WriteBlockAsync(new(Guid.NewGuid(), fixture.Partition, "Legacy", "unknown origin", 1, 100, false,
                MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow));
        await using var db = fixture.Context();
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        Assert.Equal(blocker, impact.BlockedReason);
        Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErasureImpactRejectsAnotherHumansCurrentOrHistoricalAudience(bool historicalOnly)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var episode = await SeedLegacyEpisode(fixture, change: x => x with { LegalHold = false });
        var privatePartition = EmployeeMemoryNamespaces.UserRelationship(fixture.OrganizationId.ToString("D"),
            fixture.EmployeeId.ToString("D"), Guid.NewGuid().ToString("D"), "csweet").Partition;
        var copy = episode with { Id = Guid.NewGuid(), Partition = privatePartition, IdempotencyKey = "private-copy" };
        await fixture.Store.AppendEpisodeAsync(copy);
        await using var db = fixture.Context();
        if (historicalOnly) await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_episodes WHERE id={copy.Id}");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor));
    }

    [MemoryPostgresFact]
    public async Task ErasureImpactRechecksEveryTransferredEmployeesAuthority()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context();
        var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await transfers.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, actor, TransferRequest(target, claim));
        var preview = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor,
            new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        preview = await transfers.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor);
        await transfers.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, actor,
            new(Guid.NewGuid(), preview.ReviewToken, "apply"));
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var impact = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, claim.EpisodeId, actor);
        Assert.Contains(impact.Audiences, x => x.EmployeeId == target && x.Records.Any(y => y.Kind == "Transfer"));
        await db.CoreOrganizationUsers.Where(x => x.Id == target).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, claim.EpisodeId, actor));
    }

    [MemoryPostgresTheory]
    [InlineData("employee")]
    [InlineData("installation")]
    public async Task ErasureImpactRejectsContendedDiscoveredAuthorityWithoutReverseLockWait(string authority)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, target, claim) = await SeedTransferAsync(fixture);
        var source = (await fixture.Store.ExportAsync(fixture.Partition)).Episodes.Single(x => x.Id == claim.EpisodeId);
        await fixture.Store.AppendEpisodeAsync(source with { Id = Guid.NewGuid(), Partition = TransferTarget(fixture, target), IdempotencyKey = "target-copy" });
        await using var writer = fixture.Context(); await using var transaction = await writer.Database.BeginTransactionAsync();
        var installation = await writer.CoreOrganizationUsers.Where(x => x.Id == target).Select(x => x.AgentInstallationId).SingleAsync();
        if (authority == "employee")
            await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreOrganizationUsers\" WHERE \"Id\"={target} FOR UPDATE");
        else
            await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AgentInstallations\" WHERE \"Id\"={installation!.Value} FOR UPDATE");
        await using var db = fixture.Context();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, claim.EpisodeId, actor, deadline.Token));
        await transaction.RollbackAsync();
        Assert.NotNull(await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, claim.EpisodeId, actor));
    }

    [MemoryPostgresFact]
    public async Task ErasureImpactRequiresTopLevelHumanAndRechecksRevocation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor));
        var root = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Human, IsActive = true };
        db.CoreOrganizationUsers.Add(root); await db.SaveChangesAsync();
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true)
            .SetProperty(x => x.ReportsToOrganizationUserId, root.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor));
    }
}
