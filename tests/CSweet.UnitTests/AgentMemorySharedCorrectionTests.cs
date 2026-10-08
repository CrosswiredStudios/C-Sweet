using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed record SharedCorrectionFixture(DurabilityFixture Fixture, Guid User, MemoryEpisode Source, Guid Audience,
        Guid Target, MemoryPartition Partition, Guid Copy);

    private static async Task<SharedCorrectionFixture> SeedSharedCorrectionAsync(DurabilityFixture fixture, string kind)
    {
        var (user, source, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        var target = await AddSharedRecipientAsync(fixture, kind, audience);
        await using var db = fixture.Context(); var transfers = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var applied = await ApplySharedAsync(transfers, fixture, fixture.EmployeeId, user,
            new(Guid.NewGuid(), target, kind, [new("Episode", source.Id)], "Alice handoff", SourceAudienceId: audience));
        var partition = EmployeeMemoryNamespaces.Employee(fixture.OrganizationId.ToString("D"), target.ToString("D"), "csweet").Partition;
        return new(fixture, user, source, audience, target, partition, applied.AppliedEpisodeId!.Value);
    }

    private static async Task<MemoryEpisode> AssertSharedCorrectionEpisodeAsync(SharedCorrectionFixture shared, Guid episodeId,
        Guid operation, Guid reviewedRecord, string referenceType)
    {
        var store = (PostgreSqlMemoryStore)shared.Fixture.Store;
        var episode = (await store.GetEpisodeAsync(shared.Partition, episodeId))!;
        Assert.StartsWith("sha256-v3:", episode.SourceFingerprint);
        Assert.Equal(operation, episode.CorrectionEvidence!.ReviewOperationId);
        Assert.Contains(episode.CorrectionEvidence.Sources, x => x.EpisodeId == shared.Copy);
        Assert.Equal(shared.Source.Partition, Assert.Single(episode.CorrectionEvidence.RequiredSharedPartitions!));
        Assert.Equal(new MemorySource("user", operation.ToString("D"), shared.Fixture.HumanId.ToString("D")), episode.Source);
        Assert.Contains(episode.OperationalReferences!, x => x.Type == referenceType && x.Id == reviewedRecord.ToString("D"));
        Assert.True(MemoryProvenance.IsCurrent(episode, shared.Partition, episode.Id, DateTimeOffset.UtcNow));
        return episode;
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task HumanClaimCorrectionOfSharedEvidenceCarriesSealedAncestryAndRequiresCurrentAudience(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var shared = await SeedSharedCorrectionAsync(fixture, kind);
        var now = DateTimeOffset.UtcNow;
        var entity = new MemoryEntity(Guid.NewGuid(), shared.Partition, "person", "Alice", [], null, false, now, now)
            { SourceEpisodeIds = [shared.Copy], Sensitivity = MemorySensitivity.Personal };
        await fixture.Store.UpsertEntityAsync(entity);
        var claim = new MemoryClaim(Guid.NewGuid(), shared.Partition, shared.Copy, entity.Id, "prefers", null, "concise replies",
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal, 1, 1, now, null, now) { SourceEpisodeIds = [shared.Copy] };
        await fixture.Store.WriteClaimAsync(claim);
        await using var db = fixture.Context();
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User);
        Assert.True(preview.CanCorrect);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", "detailed replies");
        var result = await review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, request);
        Assert.False(result.WasReplay);
        var corrected = (await fixture.Store.GetClaimAsync(result.ResultClaimId))!;
        Assert.Equal("detailed replies", corrected.Value);
        Assert.Equal(claim.Id, corrected.SupersedesClaimId);
        Assert.Contains(shared.Copy, corrected.SourceEpisodeIds);
        var episode = await AssertSharedCorrectionEpisodeAsync(shared, corrected.EpisodeId, request.OperationId, claim.Id, "memory-claim");
        var receipt = await db.MemoryReviewReceipts.AsNoTracking().SingleAsync(x => x.OperationId == request.OperationId);
        Assert.Equal(("Claim", "correct", fixture.HumanId), (receipt.RecordKind, receipt.Action, receipt.ActorOrganizationUserId));
        Assert.True(await db.AuditOutbox.AsNoTracking().AnyAsync(x => x.Id == receipt.Id && x.SourceEntityId == claim.Id));
        // The real writer produces evidence the trusted retention coordinator accepts.
        var hold = await review.GetHoldAsync(fixture.OrganizationId, shared.Target, episode.Id, shared.User);
        Assert.True(hold.IsTransferred);
        var inspector = fixture.Service(db, new UsageProviderFactory());
        Assert.NotNull(await inspector.GetItemAsync(fixture.OrganizationId, shared.Target, corrected.Id, applicationUserId: shared.User));
        Assert.True((await review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, request)).WasReplay);
        // Correcting the correction again keeps the same flattened ancestry instead of nesting it.
        var second = await review.GetClaimAsync(fixture.OrganizationId, shared.Target, corrected.Id, shared.User);
        Assert.True(second.CanCorrect);
        var secondRequest = new ReviewMemoryClaimRequest(Guid.NewGuid(), second.Revision, second.EvidenceToken, "correct", "bulleted replies");
        var secondResult = await review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, corrected.Id, shared.User, secondRequest);
        var recorrected = (await fixture.Store.GetClaimAsync(secondResult.ResultClaimId))!;
        var secondEpisode = await AssertSharedCorrectionEpisodeAsync(shared, recorrected.EpisodeId, secondRequest.OperationId, corrected.Id, "memory-claim");
        Assert.Equal(shared.Copy, Assert.Single(secondEpisode.CorrectionEvidence!.Sources).EpisodeId);
        Assert.Contains(episode.Id, recorrected.SourceEpisodeIds);

        await RevokeOperatorAudienceAsync(fixture, db, shared.Audience, kind == "Team" ? "actor-ended" : "actor-role");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.GetHoldAsync(fixture.OrganizationId, shared.Target, episode.Id, shared.User));
        Assert.Null(await inspector.GetItemAsync(fixture.OrganizationId, shared.Target, corrected.Id, applicationUserId: shared.User));
        Assert.Equal(2, await db.MemoryReviewReceipts.AsNoTracking().CountAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task HumanEntityCorrectionBindsSharedReplacementAuthorityIntoPreviewAndReplay(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var shared = await SeedSharedCorrectionAsync(fixture, kind);
        var now = DateTimeOffset.UtcNow;
        var subject = new MemoryEntity(Guid.NewGuid(), shared.Partition, "person", "Alice", [], null, false, now, now)
            { SourceEpisodeIds = [shared.Copy], Sensitivity = MemorySensitivity.Personal };
        var wrong = subject with { Id = Guid.NewGuid(), CanonicalName = "Bob Builder", Type = "team" };
        var right = subject with { Id = Guid.NewGuid(), CanonicalName = "Bob Studio", Type = "team" };
        foreach (var entity in new[] { subject, wrong, right }) await fixture.Store.UpsertEntityAsync(entity);
        var claim = new MemoryClaim(Guid.NewGuid(), shared.Partition, shared.Copy, subject.Id, "works_with", wrong.Id, null,
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal, 1, 1, now, null, now) { SourceEpisodeIds = [shared.Copy] };
        await fixture.Store.WriteClaimAsync(claim);
        await using var db = fixture.Context();
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User);
        var target = Assert.Single(await review.FindClaimCorrectionTargetsAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, "Bob Studio"));
        Assert.Equal(right.Id, target.EntityId);
        // A token computed without the replacement's audience authority is stale.
        var unbound = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct",
            ReplacementEntity: new(right.Id, new string('0', 64)));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, unbound));
        var request = unbound with { OperationId = Guid.NewGuid(), ReplacementEntity = new(right.Id, target.EvidenceToken) };
        var result = await review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, request);
        var corrected = (await fixture.Store.GetClaimAsync(result.ResultClaimId))!;
        Assert.Equal(right.Id, corrected.ObjectEntityId);
        var episode = await AssertSharedCorrectionEpisodeAsync(shared, corrected.EpisodeId, request.OperationId, claim.Id, "memory-claim");
        Assert.Contains(episode.OperationalReferences!, x => x.Type == "memory-entity" && x.Id == right.Id.ToString("D"));
        Assert.True((await review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, request)).WasReplay);
        await RevokeOperatorAudienceAsync(fixture, db, shared.Audience, kind == "Team" ? "actor-ended" : "actor-role");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, request));
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    public async Task HumanProcedureAndCoreCorrectionsOfSharedEvidenceCarrySealedAncestry(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var shared = await SeedSharedCorrectionAsync(fixture, kind);
        var now = DateTimeOffset.UtcNow;
        var procedure = new ProceduralMemory(Guid.NewGuid(), shared.Partition, shared.Copy, "Escalation", "Ask Alice first", null, 1,
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, now, null, now) { SourceEpisodeIds = [shared.Copy] };
        await fixture.Store.WriteProcedureAsync(procedure);
        var block = new MemoryBlock(Guid.NewGuid(), shared.Partition, "Preferences", "Alice prefers concise replies", 1, 100, true, MemoryTrustTier.AgentInference, now)
            { SourceEpisodeIds = [shared.Copy], Sensitivity = MemorySensitivity.Personal, Confirmation = MemoryConfirmationState.Pending };
        await fixture.Store.WriteBlockAsync(block);
        await using var db = fixture.Context();
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);

        var procedurePreview = await review.GetProcedureAsync(fixture.OrganizationId, shared.Target, procedure.Id, shared.User);
        Assert.True(procedurePreview.CanCorrect);
        var procedureRequest = new ReviewMemoryProcedureRequest(Guid.NewGuid(), procedurePreview.Revision, procedurePreview.EvidenceToken,
            "correct", new("Escalation", "Ask Alice, then the team lead", "Production incidents"));
        var procedureResult = await review.ReviewProcedureAsync(fixture.OrganizationId, shared.Target, procedure.Id, shared.User, procedureRequest);
        var correctedProcedure = (await fixture.Store.ExportAsync(shared.Partition)).Procedures.Single(x => x.Id == procedureResult.ResultProcedureId);
        Assert.Equal(2, correctedProcedure.Version);
        await AssertSharedCorrectionEpisodeAsync(shared, correctedProcedure.EpisodeId, procedureRequest.OperationId, procedure.Id, "memory-procedure");

        var corePreview = await review.GetCoreAsync(fixture.OrganizationId, shared.Target, block.Id, shared.User);
        Assert.True(corePreview.CanCorrect);
        var coreRequest = new ReviewMemoryCoreRequest(Guid.NewGuid(), corePreview.Revision, corePreview.EvidenceToken,
            "correct", new("Alice prefers detailed replies", true));
        await review.ReviewCoreAsync(fixture.OrganizationId, shared.Target, block.Id, shared.User, coreRequest);
        var correctedBlock = (await fixture.Store.ExportAsync(shared.Partition)).Blocks.Single(x => x.Id == block.Id);
        Assert.Equal("Alice prefers detailed replies", correctedBlock.Content);
        var added = Assert.Single(correctedBlock.SourceEpisodeIds, x => x != shared.Copy);
        await AssertSharedCorrectionEpisodeAsync(shared, added, coreRequest.OperationId, block.Id, "memory-block");
        Assert.Equal(2, await db.MemoryReviewReceipts.AsNoTracking().CountAsync(x => x.Action == "correct"));

        await RevokeOperatorAudienceAsync(fixture, db, shared.Audience, kind == "Team" ? "actor-ended" : "actor-role");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            review.ReviewProcedureAsync(fixture.OrganizationId, shared.Target, procedure.Id, shared.User, procedureRequest));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            review.ReviewCoreAsync(fixture.OrganizationId, shared.Target, block.Id, shared.User, coreRequest));
    }

    [MemoryPostgresFact]
    public async Task SharedCorrectionRollsBackAncestryEpisodeWhenTheReviewTransactionFails()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var shared = await SeedSharedCorrectionAsync(fixture, "Team");
        var now = DateTimeOffset.UtcNow;
        var entity = new MemoryEntity(Guid.NewGuid(), shared.Partition, "person", "Alice", [], null, false, now, now)
            { SourceEpisodeIds = [shared.Copy], Sensitivity = MemorySensitivity.Personal };
        await fixture.Store.UpsertEntityAsync(entity);
        var claim = new MemoryClaim(Guid.NewGuid(), shared.Partition, shared.Copy, entity.Id, "prefers", null, "concise replies",
            MemoryTrustTier.AgentInference, MemoryConfirmationState.Pending, MemorySensitivity.Personal, 1, 1, now, null, now) { SourceEpisodeIds = [shared.Copy] };
        await fixture.Store.WriteClaimAsync(claim);
        await using var db = fixture.Context();
        var review = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await review.GetClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User);
        var request = new ReviewMemoryClaimRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "correct", "detailed replies");
        var episodes = await CountEpisodesAsync(db, shared.Partition);
        // Fail the shared transaction after the ancestry episode and replacement claim are staged.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION csweet_test_fail_receipt() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER csweet_test_fail_receipt BEFORE INSERT ON "MemoryReviewReceipts" FOR EACH ROW EXECUTE FUNCTION csweet_test_fail_receipt();
            """);
        await Assert.ThrowsAnyAsync<Exception>(() => review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, request));
        Assert.Equal(episodes, await CountEpisodesAsync(db, shared.Partition));
        Assert.Empty(await db.MemoryReviewReceipts.AsNoTracking().ToListAsync());
        Assert.Equal(MemoryConfirmationState.Pending, (await fixture.Store.GetClaimAsync(claim.Id))!.Confirmation);
        Assert.Null((await fixture.Store.GetClaimAsync(claim.Id))!.ValidTo);
        await db.Database.ExecuteSqlRawAsync("""DROP TRIGGER csweet_test_fail_receipt ON "MemoryReviewReceipts"; DROP FUNCTION csweet_test_fail_receipt();""");
        var result = await review.ReviewClaimAsync(fixture.OrganizationId, shared.Target, claim.Id, shared.User, request);
        Assert.False(result.WasReplay);
        Assert.Equal(episodes + 1, await CountEpisodesAsync(db, shared.Partition));
    }

    private static async Task<int> CountEpisodesAsync(Infrastructure.Persistence.CSweetDbContext db, MemoryPartition partition)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync();
        command.CommandText = "SELECT count(*) FROM csweet_memory_episodes WHERE partition_key=@partition";
        var parameter = command.CreateParameter(); parameter.ParameterName = "partition"; parameter.Value = partition.StorageKey;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
