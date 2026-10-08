using CSweet.Contracts.Memory;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task CoreCorrectionRejectsSourceAndRevisionOverflowWithoutChangingTheBlock()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var block = await SeedReviewCore(fixture);
        var raw = await fixture.Store.ExportAsync(fixture.Partition); var template = raw.Episodes[0];
        var sources = block.SourceEpisodeIds.ToList();
        while (sources.Count < MemoryProvenance.MaximumSourceEpisodes)
        {
            var episode = template with { Id = Guid.NewGuid(), Source = new("user", Guid.NewGuid().ToString("D")) };
            await fixture.Store.AppendEpisodeAsync(episode); sources.Add(episode.Id);
        }
        await fixture.Store.WriteBlockAsync(block with { SourceEpisodeIds = sources });
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor);
        Assert.True(preview.CanConfirm); Assert.False(preview.CanCorrect);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", new("Overflow", false))));
        Assert.Equal(128, (await fixture.Store.ExportAsync(fixture.Partition)).Episodes.Count);
        var saturated = block with { Name = "Saturated", Id = Guid.NewGuid(), Revision = int.MaxValue };
        await fixture.Store.WriteBlockAsync(saturated);
        var limit = await service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, saturated.Id, actor);
        Assert.False(limit.CanConfirm); Assert.False(limit.CanReject); Assert.False(limit.CanCorrect);
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    private static async Task<MemoryBlock> SeedReviewCore(DurabilityFixture fixture, bool held = false)
    {
        var procedure = await SeedReviewProcedure(fixture, held);
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "Deployment policy", "Check approval before deployment", 3, 500, true,
            MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow.AddMinutes(-1)) { Confirmation = MemoryConfirmationState.Pending,
            Sensitivity = MemorySensitivity.Internal, SourceEpisodeIds = procedure.SourceEpisodeIds.Prepend(procedure.EpisodeId).ToArray() };
        await fixture.Store.WriteBlockAsync(block); return block;
    }

    [MemoryPostgresFact]
    public async Task CoreReviewChangesRecallAndReplayCannotUndoLaterRejection()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var block = await SeedReviewCore(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor);
        Assert.True(preview.CanConfirm); Assert.Equal("Confidential", preview.Sensitivity);
        var request = new ReviewMemoryCoreRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm");
        var result = await service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor, request);
        Assert.Contains(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "deployment")), x => x.Id == block.Id);
        Assert.Equal(MemoryTrustTier.AgentInference, Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Blocks).Trust);
        preview = await service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor);
        await service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor, new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "reject"));
        Assert.DoesNotContain(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "deployment")), x => x.Id == block.Id);
        Assert.True((await service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor, request)).WasReplay);
        Assert.Equal(MemoryConfirmationState.Rejected, Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Blocks).Confirmation);
        var history = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Core", block.Id, actor);
        Assert.Equal(3, history.Items.Count); Assert.Contains("Rejected", history.Items[^1].State!);
        Assert.Equal(result.ReceiptId, Assert.Single(history.Items[1].Reviews).ReceiptId);
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor, request));
    }

    [MemoryPostgresFact]
    public async Task CoreCorrectionPreservesIdentitySourcesHoldAndHistory()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var block = await SeedReviewCore(fixture, held: true);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor);
        var request = new ReviewMemoryCoreRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", new("Corrected deployment policy", false));
        var result = await service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor, request);
        var raw = await fixture.Store.ExportAsync(fixture.Partition); var updated = Assert.Single(raw.Blocks);
        Assert.Equal(block.Id, result.BlockId); Assert.Equal(block.Name, updated.Name); Assert.Equal(block.Revision + 1, updated.Revision);
        Assert.Equal(block.MaximumTokens, updated.MaximumTokens); Assert.False(updated.IsPinned); Assert.Equal("Corrected deployment policy", updated.Content);
        Assert.Equal(MemoryConfirmationState.Confirmed, updated.Confirmation); Assert.Equal(MemoryTrustTier.ConfirmedUser, updated.Trust);
        Assert.All(block.SourceEpisodeIds, id => Assert.Contains(id, updated.SourceEpisodeIds));
        var source = Assert.Single(raw.Episodes, x => !block.SourceEpisodeIds.Contains(x.Id));
        Assert.True(source.LegalHold); Assert.Equal(MemorySensitivity.Confidential, source.Sensitivity);
        Assert.Equal(raw.Episodes.Where(x => x.Id != source.Id).Min(x => x.ExpiresAt), source.ExpiresAt);
        Assert.Equal(fixture.HumanId.ToString("D"), source.Source.Author); Assert.True(MemorySourceIntegrity.IsVerified(source));
        Assert.True((await service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor, request)).WasReplay);
        Assert.Equal(3, (await fixture.Store.ExportAsync(fixture.Partition)).Episodes.Count);
        var history = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Core", block.Id, actor);
        Assert.Equal(block.Content, history.Items[0].Content); Assert.Equal(updated.Content, history.Items[1].Content);
        Assert.Single(history.Items[1].Reviews);
    }

    [MemoryPostgresFact]
    public async Task CoreReviewRejectsStaleOrForeignEvidenceAndCanWithholdUnverifiedLegacyContent()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var block = await SeedReviewCore(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{content}}','\"altered\"') WHERE id={block.SourceEpisodeIds[0]}");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "confirm")));
        preview = await service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor);
        Assert.False(preview.CanConfirm); Assert.False(preview.CanCorrect); Assert.True(preview.CanReject);
        var legacy = block with { Id = Guid.NewGuid(), Name = "Legacy", SourceEpisodeIds = [] };
        await fixture.Store.WriteBlockAsync(legacy);
        var legacyPreview = await service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, legacy.Id, actor);
        Assert.False(legacyPreview.CanConfirm); Assert.True(legacyPreview.CanReject);
        await service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, legacy.Id, actor,
            new(Guid.NewGuid(), legacyPreview.Revision, legacyPreview.EvidenceToken, "reject"));
        var foreign = block with { Id = Guid.NewGuid(), Partition = block.Partition with { UserId = Guid.NewGuid().ToString("D") } };
        await fixture.Store.WriteBlockAsync(foreign);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, foreign.Id, actor));
    }

    [MemoryPostgresFact]
    public async Task CoreAuditFailureRollsBackCorrectionAndConcurrentDecisionsHaveOneWinner()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var block = await SeedReviewCore(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor);
        var request = new ReviewMemoryCoreRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", new("Changed", false));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_core_review() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER fail_core_review BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryBlock') EXECUTE FUNCTION fail_core_review();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId, block.Id, actor, request));
        var raw = await fixture.Store.ExportAsync(fixture.Partition);
        Assert.Equal(2, raw.Episodes.Count); Assert.Equal(block.Content, Assert.Single(raw.Blocks).Content);
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_core_review ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_core_review();");
        async Task<bool> Attempt()
        {
            await using var context = fixture.Context();
            try { await new AgentMemoryReviewService(context, fixture.Store, TimeProvider.System).ReviewCoreAsync(fixture.OrganizationId, fixture.EmployeeId,
                block.Id, actor, request with { OperationId = Guid.NewGuid() }); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(), Attempt()), x => x); Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
        Assert.Equal(3, (await fixture.Store.ExportAsync(fixture.Partition)).Episodes.Count);
    }
}
