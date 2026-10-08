using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(MemoryClaim Claim, MemoryEntity Target, MemoryEpisode Source)> SeedEntityReview(DurabilityFixture fixture)
    {
        var original = await SeedReviewClaim(fixture); var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var oldTarget = new MemoryEntity(Guid.NewGuid(), fixture.Partition, "person", "Bob", [], null, false, now, now)
            { SourceEpisodeIds = [original.EpisodeId], Sensitivity = MemorySensitivity.Personal };
        await fixture.Store.UpsertEntityAsync(oldTarget);
        var source = new MemoryEpisode(Guid.NewGuid(), fixture.Partition, MemoryScope.User, "Carol is the new manager", "text/plain",
            new("user", "new-manager"), "checksum", now, now, ExpiresAt: now.AddDays(1), LegalHold: true, Sensitivity: MemorySensitivity.Confidential);
        await fixture.Store.AppendEpisodeAsync(source);
        var target = oldTarget with { Id = Guid.NewGuid(), CanonicalName = "Carol", SourceEpisodeIds = [source.Id] };
        await fixture.Store.UpsertEntityAsync(target);
        var claim = original with { Id = Guid.NewGuid(), Predicate = "reports to", ObjectEntityId = oldTarget.Id, Value = null, ValidTo = now.AddDays(2) };
        await fixture.Store.WriteClaimAsync(claim);
        return (claim, target, source);
    }

    [Fact]
    public void AddingEntityCorrectionPreservesHistoricalLiteralRequestHashInput()
    {
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), 123, new string('a', 64), "correct", "literal");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(JsonSerializer.Serialize(new { request.OperationId, request.ExpectedRevision, request.EvidenceToken, request.Action, request.ReplacementValue }, options),
            JsonSerializer.Serialize(request, options));
    }

    [MemoryPostgresFact]
    public async Task EntityClaimCorrectionCannotDiscardSourcesWhenTheCombinedEvidenceExceedsTheBound()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var (claim, target, source) = await SeedEntityReview(fixture);
        var ids = new List<Guid> { source.Id };
        for (var i = 1; i < MemoryProvenance.MaximumSourceEpisodes; i++)
        {
            var extra = source with { Id = Guid.NewGuid(), Source = new("user", $"source-{i}") };
            await fixture.Store.AppendEpisodeAsync(extra); ids.Add(extra.Id);
        }
        await fixture.Store.UpsertEntityAsync(target with { SourceEpisodeIds = ids });
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        Assert.Empty(await service.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, "car"));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        Assert.Equal(claim.ObjectEntityId, (await fixture.Store.GetClaimAsync(claim.Id))!.ObjectEntityId);
    }

    [MemoryPostgresFact]
    public async Task EntityClaimCorrectionAuditFailureRollsBackAndConcurrentCorrectionsHaveOneWinner()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var (claim, target, _) = await SeedEntityReview(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        var choice = Assert.Single(await service.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, "car"));
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", ReplacementEntity: new(target.Id, choice.EvidenceToken));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_entity_review() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER fail_entity_review BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryClaim') EXECUTE FUNCTION fail_entity_review();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request));
        var raw = await fixture.Store.ExportAsync(fixture.Partition);
        Assert.Equal(2, raw.Episodes.Count); Assert.Equal(2, raw.Claims.Count); Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        Assert.Equal(claim.ValidTo, (await fixture.Store.GetClaimAsync(claim.Id))!.ValidTo);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_entity_review ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_entity_review();");
        async Task<bool> Attempt()
        {
            await using var context = fixture.Context();
            try { await new AgentMemoryReviewService(context, fixture.Store, TimeProvider.System).ReviewClaimAsync(fixture.OrganizationId,
                fixture.EmployeeId, claim.Id, actor, request with { OperationId = Guid.NewGuid() }); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(), Attempt()), x => x);
        Assert.Equal(3, (await fixture.Store.ExportAsync(fixture.Partition)).Claims.Count);
        Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task EntityClaimCorrectionRetainsBothTargetsEvidenceAndReplayCannotUndoLaterRejection()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var (claim, target, source) = await SeedEntityReview(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        Assert.True(preview.IsEntityValued); Assert.True(preview.CanCorrect);
        var choice = Assert.Single(await service.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, "car"));
        Assert.Equal(target.Id, choice.EntityId); Assert.Equal("Confidential", choice.Sensitivity);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct",
            ReplacementEntity: new(choice.EntityId, choice.EvidenceToken));
        var result = await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request);
        var replacement = (await fixture.Store.GetClaimAsync(result.ResultClaimId))!;
        Assert.Equal(target.Id, replacement.ObjectEntityId); Assert.Null(replacement.Value); Assert.Equal(claim.Id, replacement.SupersedesClaimId);
        Assert.Contains(claim.EpisodeId, replacement.SourceEpisodeIds); Assert.Contains(source.Id, replacement.SourceEpisodeIds);
        Assert.Equal(MemorySensitivity.Confidential, replacement.Sensitivity); Assert.Equal(claim.ValidTo, replacement.ValidTo);
        Assert.Equal(MemoryTrustTier.ConfirmedUser, replacement.Trust);
        var correctionSource = (await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, replacement.EpisodeId))!;
        Assert.Equal(source.ExpiresAt, correctionSource.ExpiresAt); Assert.True(correctionSource.LegalHold);
        Assert.Contains("Carol", correctionSource.Content); Assert.True(MemorySourceIntegrity.IsVerified(correctionSource));
        Assert.Contains(correctionSource.OperationalReferences!, x => x.Type == "memory-entity" && x.Id == target.Id.ToString("D"));
        var current = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, replacement.Id, actor);
        await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, replacement.Id, actor, new(Guid.NewGuid(), current.Revision, current.EvidenceToken, "reject"));
        Assert.True((await service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request)).WasReplay);
        Assert.Equal(MemoryConfirmationState.Rejected, (await fixture.Store.GetClaimAsync(replacement.Id))!.Confirmation);
        Assert.Equal(2, await db.MemoryReviewReceipts.CountAsync());
    }

    [MemoryPostgresFact]
    public async Task EntityClaimCorrectionRejectsStaleTargetsForeignIdsAndValueTypeChanges()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var (claim, target, _) = await SeedEntityReview(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        var choice = Assert.Single(await service.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, "car"));
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", ReplacementEntity: new(target.Id, choice.EvidenceToken));
        await fixture.Store.UpsertEntityAsync(target with { Aliases = ["new alias"] });
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request));
        var foreign = target with { Id = Guid.NewGuid(), Partition = target.Partition with { UserId = Guid.NewGuid().ToString("D") } };
        await fixture.Store.UpsertEntityAsync(foreign);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor,
            request with { ReplacementEntity = new(foreign.Id, choice.EvidenceToken) }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor,
            request with { ReplacementEntity = null, ReplacementValue = "Carol" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor,
            request with { ReplacementValue = "ambiguous" }));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task EntityClaimCorrectionRechecksTargetSourcePolicyAndCurrentAuthority()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var (claim, target, source) = await SeedEntityReview(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor);
        var choice = Assert.Single(await service.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, "car"));
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", ReplacementEntity: new(target.Id, choice.EvidenceToken));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{content}}','\"changed\"') WHERE id={source.Id}");
        Assert.Empty(await service.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, "car"));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewClaimAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, request));
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, fixture.EmployeeId, claim.Id, actor, "car"));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }
}
