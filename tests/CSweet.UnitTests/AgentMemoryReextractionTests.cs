using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static AgentMemoryReextractionService Reextraction(DurabilityFixture fixture, CSweetDbContext db) =>
        new(db, fixture.Store, fixture.Service(db, new UsageProviderFactory()), TimeProvider.System);

    private static ReviewMemoryReextractionRequest ReextractionRequest(MemoryReextractionPreview preview) =>
        new(Guid.NewGuid(), preview.InputGeneration, preview.EvidenceToken, AgentMemoryReextractionService.PreservationPolicy);

    private static async Task<(Guid User, Guid Job)> SeedReextractionAsync(DurabilityFixture fixture, bool legacyOutput = true)
    {
        var result = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync(ReviewedEpisodeReconciliation.InstallGuards);
        if (legacyOutput)
        {
            await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Processing)
                .SetProperty(j => j.LeaseToken, Guid.NewGuid()).SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5)));
            await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x
                .SetProperty(j => j.AcceptedExtractionJson, "{\"SchemaVersion\":1,\"private\":\"legacy private output\"}")
                .SetProperty(j => j.ExtractionAcceptedAt, DateTimeOffset.UtcNow));
            await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed)
                .SetProperty(j => j.LeaseToken, (Guid?)null).SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.LastError, "memory_enrichment_unverifiable_output"));
        }
        await db.Database.ExecuteSqlRawAsync(EpisodeReextractionGuards.Install);
        return result;
    }

    [MemoryPostgresFact]
    public async Task ReextractionArchivesUnverifiableOutputAndReplayNeverResetsTheReplacement()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture);
        await using var db = fixture.Context(); var service = Reextraction(fixture, db);
        var original = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => EpisodeRecovery(fixture, db)
            .RetryAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, new(Guid.NewGuid(), 0)));
        var page = await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user);
        Assert.Equal(id, Assert.Single(page.Items).JobId);
        Assert.DoesNotContain("legacy private output", JsonSerializer.Serialize(page));
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        Assert.True(preview.CanQueue); Assert.True(preview.HasPreviousAcceptedExtraction);
        Assert.DoesNotContain("legacy private output", JsonSerializer.Serialize(preview));
        var request = ReextractionRequest(preview);
        var queued = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, request);
        Assert.Equal(1, queued.InputGeneration); Assert.Equal("Pending", queued.Status); Assert.False(queued.Replayed);
        var archived = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.NotNull(archived.SupersededAt);
        Assert.Equal(MemoryEpisodeReextractionEvidence.JobHash(original), MemoryEpisodeReextractionEvidence.JobHash(archived));
        Assert.True(await MemoryEpisodeReextractionEvidence.VerifyArchivedAsync(db, archived, default));
        Assert.True((await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, request)).Replayed);
        var provider = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(1, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(1, provider.Calls);
        var completed = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(x => x.Id == queued.JobId);
        Assert.Equal(MemoryCaptureStatus.Completed, completed.Status);
        Assert.Equal("Healthy", (await fixture.Service(db, provider).GetSummaryAsync(fixture.OrganizationId, fixture.EmployeeId))!.Health);
        using var accepted = JsonDocument.Parse(completed.AcceptedExtractionJson!);
        Assert.Equal(6, accepted.RootElement.GetProperty("SchemaVersion").GetInt32());
        var replay = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, request);
        Assert.Equal(queued.JobId, replay.JobId); Assert.Equal("Completed", replay.Status); Assert.True(replay.Replayed);
        Assert.Equal(2, await db.MemoryEpisodeEnrichmentJobs.CountAsync());
        Assert.Single(await db.MemoryEpisodeReextractionReceipts.ToListAsync());
        Assert.Single(await db.AuditOutbox.Where(x => x.SourceEntityType == nameof(MemoryEpisodeEnrichmentJob)).ToListAsync());
        Assert.Equal(MemoryConfirmationState.Pending, Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims).Confirmation);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewAsync(fixture.OrganizationId,
            fixture.EmployeeId, id, user, request with { PreservationPolicy = "preserve-existing-v1", EvidenceToken = new string('a',64) }));
        Assert.Equal(0, await fixture.Service(db, provider).ProcessPendingAsync());
    }

    [MemoryPostgresFact]
    public async Task ReextractionOfCompletedJobPreservesReviewedRecordsAndSupportsAnotherGeneration()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, claim) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context(); var ingestion = IngestionRecovery(fixture, db);
        var initial = await ingestion.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var first = await ingestion.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), initial.Revision, initial.EvidenceToken, initial.RequiredReconciliationPolicy));
        var provider = new ReconciliationProvider();
        Assert.Equal(1, await fixture.Service(db, provider).ProcessPendingAsync());
        await db.Database.ExecuteSqlRawAsync(EpisodeReextractionGuards.Install);
        var before = await fixture.Store.ExportAsync(episode.Partition); var service = Reextraction(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, first.JobId, user);
        Assert.True(preview.CanQueue); Assert.True(preview.ExistingRecords >= 7);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId,
            first.JobId, user, ReextractionRequest(preview) with { PreservationPolicy = "overwrite" }));
        var request = ReextractionRequest(preview);
        var second = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, first.JobId, user, request);
        Assert.Equal(1, await fixture.Service(db, provider).ProcessPendingAsync());
        var after = await fixture.Store.ExportAsync(episode.Partition);
        Assert.Equal(JsonSerializer.Serialize(before.Claims), JsonSerializer.Serialize(after.Claims));
        Assert.Equal(JsonSerializer.Serialize(before.Entities), JsonSerializer.Serialize(after.Entities));
        Assert.Equal(JsonSerializer.Serialize(before.Procedures), JsonSerializer.Serialize(after.Procedures));
        Assert.Equal(JsonSerializer.Serialize(before.Edges), JsonSerializer.Serialize(after.Edges));
        Assert.Equal(JsonSerializer.Serialize(before.Blocks), JsonSerializer.Serialize(after.Blocks));
        Assert.Equal(JsonSerializer.Serialize(before.Embeddings), JsonSerializer.Serialize(after.Embeddings));
        Assert.Equal(MemoryConfirmationState.Confirmed, Assert.Single(after.Claims, x => x.Id == claim).Confirmation);
        var next = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, second.JobId, user);
        var third = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, second.JobId, user, ReextractionRequest(next));
        Assert.Equal(2, third.InputGeneration);
        var replay = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, first.JobId, user, request);
        Assert.Equal(second.JobId, replay.JobId); Assert.Equal("Superseded", replay.Status);
        Assert.Equal(1, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(3, provider.Calls);
    }

    [MemoryPostgresTheory]
    [InlineData("grant")]
    [InlineData("manager")]
    [InlineData("source")]
    [InlineData("restored-grant")]
    public async Task ReextractionRejectsStaleSourceAndCurrentOrRestoredAuthority(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture);
        await using var db = fixture.Context(); var service = Reextraction(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        if (change.Contains("grant"))
        {
            await db.AgentInstallationGrants.ExecuteUpdateAsync(x => x.SetProperty(j => j.RequiredCapabilitiesJson, "[]"));
            if (change == "restored-grant") await db.AgentInstallationGrants.ExecuteUpdateAsync(x =>
                x.SetProperty(j => j.RequiredCapabilitiesJson, "[\"platform.memory.write.v1\"]"));
        }
        else if (change == "manager") await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId)
            .ExecuteUpdateAsync(x => x.SetProperty(j => j.ReportsToOrganizationUserId, (Guid?)null));
        else await db.Database.ExecuteSqlRawAsync("UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['content'],'\"changed\"')");
        var failure = await Record.ExceptionAsync(() => service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, ReextractionRequest(preview)));
        Assert.True(failure is UnauthorizedAccessException or InvalidOperationException or DbUpdateConcurrencyException, failure?.ToString());
        Assert.Single(await db.MemoryEpisodeEnrichmentJobs.ToListAsync()); Assert.Empty(await db.MemoryEpisodeReextractionReceipts.ToListAsync());
        Assert.Null((await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).SupersededAt);
    }

    [MemoryPostgresFact]
    public async Task ReextractionAuditFailureRollsBackSupersessionAndQueue()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture);
        await using var db = fixture.Context(); var service = Reextraction(fixture, db);
        var original = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_reextraction_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER fail_reextraction_audit BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW EXECUTE FUNCTION fail_reextraction_audit();
            """);
        var request = ReextractionRequest(preview);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, request));
        var retained = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(retained));
        Assert.Empty(await db.MemoryEpisodeReextractionReceipts.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_reextraction_audit ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_reextraction_audit();");
        Assert.False((await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, request)).Replayed);
    }

    [MemoryPostgresFact]
    public async Task ReextractionDatabaseRequiresAtomicReceiptAndFreezesThePredecessor()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture);
        await using var db = fixture.Context(); var service = Reextraction(fixture, db);
        await Assert.ThrowsAsync<PostgresException>(() => db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x =>
            x.SetProperty(j => j.SupersededAt, DateTimeOffset.UtcNow)));
        Assert.Null((await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).SupersededAt);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        var queued = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, ReextractionRequest(preview));
        await Assert.ThrowsAsync<PostgresException>(() => db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == id)
            .ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Pending)));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"MemoryEpisodeReextractionReceipts\" WHERE \"Id\"={queued.ReceiptId}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MemoryEpisodeReextractionReceipts\" SET \"RequestHash\"={new string('b',64)} WHERE \"Id\"={queued.ReceiptId}"));
        await db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == queued.JobId).ExecuteUpdateAsync(x =>
            x.SetProperty(j => j.Status, MemoryCaptureStatus.Processing).SetProperty(j => j.LeaseToken, Guid.NewGuid())
             .SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5)));
        await Assert.ThrowsAsync<PostgresException>(() => db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == queued.JobId)
            .ExecuteUpdateAsync(x => x.SetProperty(j => j.AcceptedExtractionJson, "{\"SchemaVersion\":4}")));
        var downgrade = db.GetService<IMigrationsSqlGenerator>().Generate(new ReviewedEpisodeReextraction().DownOperations).First().CommandText;
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(downgrade));
        Assert.Equal(2, await db.MemoryEpisodeEnrichmentJobs.CountAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team", "actor-ended")]
    [InlineData("Role", "actor-role")]
    [InlineData("InstallationEmployee", "installation")]
    [InlineData("InstallationRelationship", "relationship")]
    public async Task ReextractionSupportsCurrentAudiencesAndRevocationAlsoDeniesReceiptReplay(string kind, string revoke)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context(); var ingestion = IngestionRecovery(fixture, db);
        var input = await ingestion.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var old = await ingestion.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), input.Revision, input.EvidenceToken));
        Assert.Equal(1, await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).ProcessPendingAsync());
        await db.Database.ExecuteSqlRawAsync(ReviewedEpisodeReconciliation.InstallGuards);
        await db.Database.ExecuteSqlRawAsync(EpisodeReextractionGuards.Install);
        var service = Reextraction(fixture, db);
        Assert.Equal(old.JobId, Assert.Single((await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user)).Items).JobId);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, old.JobId, user);
        Assert.True(preview.CanQueue); Assert.Equal(episode.Content, preview.Content);
        var request = ReextractionRequest(preview);
        await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, old.JobId, user, request);
        Assert.Equal(1, await fixture.Service(db, new ReconciliationProvider()).ProcessPendingAsync());
        Assert.All((await fixture.Store.ExportAsync(episode.Partition)).Claims, x => Assert.Equal(episode.Partition, x.Partition));
        await RevokeOperatorAudienceAsync(fixture, db, audience, revoke);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewAsync(fixture.OrganizationId,
            fixture.EmployeeId, old.JobId, user, request));
        Assert.Single(await db.MemoryEpisodeReextractionReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ReextractionForgettingHonorsHoldDeletesAllGenerationsAndRetainsContentFreeReceipts()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture);
        await using var db = fixture.Context(); var service = Reextraction(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        var request = ReextractionRequest(preview);
        var queued = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, request);
        var episode = queued.EpisodeId; var erase = ErasureService(fixture, db);
        var hold = await erase.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode, user);
        await erase.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode, user,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true));
        // A hold changes retention only; it must not force another provider call or lose this lineage.
        Assert.Equal(1, await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).ProcessPendingAsync());
        var blocked = await erase.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode, user);
        Assert.Equal("memory_legal_hold_prevents_deletion", blocked.ApplyBlockedReason); Assert.Null(blocked.EvidenceToken);
        hold = await erase.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode, user);
        await erase.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode, user,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, false));
        var impact = await erase.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode, user);
        Assert.Null(impact.ApplyBlockedReason);
        db.ChangeTracker.Clear();
        var erased = await erase.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode, user,
            new(Guid.NewGuid(), Assert.IsType<string>(impact.EvidenceToken)));
        Assert.Equal("completed", erased.Status); Assert.Equal(2, erased.ClearedJobs);
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync()); Assert.Empty(await db.MemoryEpisodeExtractionReceipts.ToListAsync());
        Assert.Single(await db.MemoryEpisodeReextractionReceipts.ToListAsync());
        Assert.DoesNotContain("Alice", JsonSerializer.Serialize(await db.MemoryEpisodeReextractionReceipts.ToListAsync()));
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, episode));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, request));
        Assert.Empty((await IngestionRecovery(fixture, db).ListAsync(fixture.OrganizationId, fixture.EmployeeId, user)).Items);
    }

    [MemoryPostgresFact]
    public async Task ReextractionMigrationPreservesPopulatedOriginalJobsAndCanRoundTripBeforeReview()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture);
        await using var db = fixture.Context(); var original = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        var generator = db.GetService<IMigrationsSqlGenerator>(); var migration = new ReviewedEpisodeReextraction();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()));
        Assert.False(db.Database.HasPendingModelChanges());
        var service = Reextraction(fixture, db); var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, ReextractionRequest(preview));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(generator.Generate(migration.DownOperations).First().CommandText));
    }

    [MemoryPostgresTheory]
    [InlineData("provider")]
    [InlineData("hash")]
    [InlineData("extractor")]
    [InlineData("missing-lists")]
    public async Task ReextractionRecoversMalformedAcceptedEvidenceWithoutTreatingItAsReusable(string defect)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture, legacyOutput: false);
        await using var db = fixture.Context(); var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        var source = JsonNode.Parse(job.SourceJson)!;
        var output = new JsonObject { ["SchemaVersion"] = 4, ["Episode"] = source["Episode"]!.DeepClone(),
            ["GenericSourceHash"] = job.SourceHash, ["ExtractorVersion"] = "test",
            ["Enrichment"] = new JsonObject { ["Entities"] = new JsonArray(), ["Claims"] = new JsonArray(), ["Edges"] = new JsonArray(), ["Procedures"] = new JsonArray() },
            ["Provider"] = new JsonObject { ["Id"] = fixture.ProviderId.ToString(), ["Model"] = "test-model", ["ConfigurationHash"] = new string('a',64) } };
        if (defect == "provider") output["Provider"] = null;
        if (defect == "hash") output["Provider"]!["ConfigurationHash"] = new string('x',64);
        if (defect == "extractor") output["ExtractorVersion"] = "";
        if (defect == "missing-lists") output["Enrichment"] = new JsonObject();
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Processing)
            .SetProperty(j => j.LeaseToken, Guid.NewGuid()).SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5)));
        var body = output.ToJsonString();
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.AcceptedExtractionJson, body)
            .SetProperty(j => j.ExtractionAcceptedAt, DateTimeOffset.UtcNow));
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed)
            .SetProperty(j => j.LeaseToken, (Guid?)null).SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => EpisodeRecovery(fixture, db).RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, id, user, new(Guid.NewGuid(), 0)));
        var service = Reextraction(fixture, db); var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, ReextractionRequest(preview));
        var provider = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(1, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(1, provider.Calls);
        Assert.NotNull((await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(x => x.Id == id)).AcceptedExtractionJson);
    }

    [MemoryPostgresFact]
    public async Task ReextractionConcurrentReviewsCannotForkAndLosingRequestRequiresFreshReview()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture);
        await using var first = fixture.Context(); await using var second = fixture.Context();
        var preview = await Reextraction(fixture, first).PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        async Task<Exception?> Attempt(CSweetDbContext context) => await Record.ExceptionAsync(() =>
            Reextraction(fixture, context).ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, ReextractionRequest(preview)));
        var results = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.Single(results, x => x is null); Assert.Single(results, x => x is DbUpdateConcurrencyException);
        Assert.Equal(2, await first.MemoryEpisodeEnrichmentJobs.CountAsync()); Assert.Single(await first.MemoryEpisodeReextractionReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ReextractionBoundsGenerationsAndOldReplayAlwaysReportsItsOwnChild()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, original) = await SeedReextractionAsync(fixture);
        await using var db = fixture.Context(); var service = Reextraction(fixture, db); var id = original;
        ReviewMemoryReextractionRequest? firstRequest = null; Guid firstChild = Guid.Empty;
        for (var generation = 1; generation <= 8; generation++)
        {
            var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
            Assert.True(preview.CanQueue); var request = ReextractionRequest(preview);
            var result = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, request);
            Assert.Equal(generation, result.InputGeneration);
            if (generation == 1) { firstRequest = request; firstChild = result.JobId; }
            id = result.JobId;
            await db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed));
        }
        var last = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        Assert.False(last.CanQueue); Assert.Equal("memory_reextraction_generation_limit", last.BlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, ReextractionRequest(last)));
        var replay = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, original, user, firstRequest!);
        Assert.Equal(firstChild, replay.JobId); Assert.Equal("Superseded", replay.Status);
        Assert.Equal(9, await db.MemoryEpisodeEnrichmentJobs.CountAsync()); Assert.Equal(8, await db.MemoryEpisodeReextractionReceipts.CountAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("history")]
    [InlineData("seal")]
    public async Task ReextractionRefusesMissingHistoryOrTamperedArchivedEvidence(string defect)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture);
        await using var db = fixture.Context(); var service = Reextraction(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        var result = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, ReextractionRequest(preview));
        if (defect == "history") await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_revisions WHERE record_id={result.EpisodeId}");
        else
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER csweet_episode_reextraction_receipt_guard ON \"MemoryEpisodeReextractionReceipts\"");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"MemoryEpisodeReextractionReceipts\" SET \"PreviousJobHash\"={new string('b',64)} WHERE \"Id\"={result.ReceiptId}");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, result.JobId, user));
        Assert.Equal(2, await db.MemoryEpisodeEnrichmentJobs.CountAsync());
    }

    [MemoryPostgresFact]
    public async Task ReextractionOfAppliedTransferKeepsItsVerifiedOriginAndRetainedClaims()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, target, episode) = await SeedVerifiedErasureTransferAsync(fixture);
        await using var db = fixture.Context();
        Assert.Equal(1, await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).ProcessPendingAsync());
        await db.Database.ExecuteSqlRawAsync(EpisodeMemoryEnrichmentRecovery.InstallTriggers);
        await db.Database.ExecuteSqlRawAsync(ReviewedEpisodeReconciliation.InstallGuards);
        await db.Database.ExecuteSqlRawAsync(EpisodeReextractionGuards.Install);
        var original = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(); var service = Reextraction(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, target, original.Id, user);
        Assert.Equal("knowledge-transfer", preview.Source); Assert.True(preview.CanQueue);
        var retained = await fixture.Store.ExportAsync(TransferTarget(fixture, target));
        var result = await service.ReviewAsync(fixture.OrganizationId, target, original.Id, user, ReextractionRequest(preview));
        Assert.Equal(episode, result.EpisodeId);
        Assert.Equal(1, await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).ProcessPendingAsync());
        var after = await fixture.Store.ExportAsync(TransferTarget(fixture, target));
        Assert.NotEmpty(retained.Claims);
        foreach (var claim in retained.Claims) Assert.Equal(JsonSerializer.Serialize(claim), JsonSerializer.Serialize(Assert.Single(after.Claims, x => x.Id == claim.Id)));
        Assert.NotNull((await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(after.Episodes.Single(x => x.Id == episode).Partition, episode))!.TransferEvidence);
    }

    [MemoryPostgresFact]
    public async Task ReextractionAcceptedReplacementRetriesWithoutAnotherProviderCall()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, id) = await SeedReextractionAsync(fixture);
        await using var db = fixture.Context(); var service = Reextraction(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user);
        var replacement = await service.ReviewAsync(fixture.OrganizationId, fixture.EmployeeId, id, user, ReextractionRequest(preview));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_reextraction_claim() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER fail_reextraction_claim BEFORE INSERT ON csweet_memory_claims FOR EACH ROW EXECUTE FUNCTION fail_reextraction_claim();
            """);
        var provider = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(0, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(1, provider.Calls);
        var accepted = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(x => x.Id == replacement.JobId);
        Assert.NotNull(accepted.AcceptedExtractionJson);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_reextraction_claim ON csweet_memory_claims; DROP FUNCTION fail_reextraction_claim();");
        await db.MemoryEpisodeEnrichmentJobs.Where(x => x.Id == replacement.JobId).ExecuteUpdateAsync(x =>
            x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed).SetProperty(j => j.Attempts, 10));
        await EpisodeRecovery(fixture, db).RetryAsync(fixture.OrganizationId, fixture.EmployeeId, replacement.JobId, user, new(Guid.NewGuid(), 0));
        var noDispatch = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("Accepted output must be reused"));
        Assert.Equal(1, await fixture.Service(db, noDispatch).ProcessPendingAsync()); Assert.Equal(0, noDispatch.Calls);
        var completed = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync(x => x.Id == replacement.JobId);
        Assert.Equal(accepted.AcceptedExtractionJson, completed.AcceptedExtractionJson);
        Assert.Equal(accepted.ExtractionAcceptedAt, completed.ExtractionAcceptedAt);
        Assert.Equal(MemoryCaptureStatus.Completed, completed.Status);
    }
}
