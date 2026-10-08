using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CSweet.Domain.Core;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<MemoryExtractionInputReceipt> SeedFailedDispatchAsync(DurabilityFixture fixture)
    {
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync(DurableExtractionInputEvidence.InstallTriggers);
        await fixture.Service(db, new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("private-provider-detail")))
            .CaptureMessageAsync(fixture.MessageId, enrich: true);
        return await db.MemoryExtractionInputReceipts.AsNoTracking().SingleAsync();
    }

    [MemoryPostgresFact]
    public async Task InputReceiptsDiscoverFailedPairedDispatchWithoutAcceptedOutputAndSurviveErasureReplay()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, assistant, source) = await SeedCaptureErasureAsync(fixture);
        var receipt = await SeedFailedDispatchAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var job = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.Id == receipt.JobId);
        Assert.Equal(MemoryCaptureStatus.Pending, job.Status); Assert.Null(job.AcceptedExtractionJson);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            Assert.Contains(receipt.JobId, plan.Jobs);
            Assert.Null(await cleanup.CheckRetentionAsync(plan, default));
            await cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default);
            await transaction.CommitAsync();
        }
        Assert.Equal(receipt.EvidenceJson, (await db.MemoryExtractionInputReceipts.AsNoTracking().SingleAsync()).EvidenceJson);
        // The provider's old input may now be absent: the completed fence and metadata
        // receipt still allow the host cleanup to replay without restoring that input.
        await using (var connection = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var delete = new NpgsqlCommand("DELETE FROM csweet_memory_episodes WHERE id=@id", connection);
            delete.Parameters.AddWithValue("id", assistant); await delete.ExecuteNonQueryAsync();
        }
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            Assert.Equal(new MemoryCaptureErasure.Result(0, 0), await cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default));
            await transaction.CommitAsync();
        }
    }

    [MemoryPostgresFact]
    public async Task InputReceiptsProtectIndependentHeldInputDuringProviderDispatch()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _, source) = await SeedCaptureErasureAsync(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new ScriptedProviderFactory(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        await using var worker = fixture.Context();
        await worker.Database.ExecuteSqlRawAsync(DurableExtractionInputEvidence.InstallTriggers);
        var processing = fixture.Service(worker, factory).CaptureMessageAsync(fixture.MessageId, enrich: true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
            var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
            var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
            await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor,
                new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true));
            await using var transaction = await db.Database.BeginTransactionAsync();
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default));
            Assert.Equal("memory_legal_hold_prevents_deletion", error.Message);
            Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
            Assert.Single(await db.MemoryEnrichmentProviderLeases.ToListAsync());
            Assert.Equal(MemoryCaptureStatus.Processing, (await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.ConversationMessageId == fixture.MessageId)).Status);
            await transaction.RollbackAsync();
        }
        finally { release.TrySetResult(); }
        await processing.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputReceiptsKeepProcessingWithoutCurrentLeaseEvidenceBlocked(bool previousAttempt)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, _, source) = await SeedCaptureErasureAsync(fixture);
        if (previousAttempt) await SeedFailedDispatchAsync(fixture);
        await using var db = fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MemoryCaptureStatus.Processing)
            .SetProperty(x => x.LeaseToken, Guid.NewGuid()).SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5)));
        await using var transaction = await db.Database.BeginTransactionAsync();
        var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
        var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
        Assert.Equal("memory_erasure_capture_retention_review_required", await cleanup.CheckRetentionAsync(plan, default));
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task InputReceiptCapacityFailsBeforeDispatchWithoutExcludingTheSource()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var original = await SeedFailedDispatchAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        for (var attempt = 1; attempt < 64; attempt++)
        {
            var lease = Guid.NewGuid();
            await db.MemoryCaptureOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MemoryCaptureStatus.Processing)
                .SetProperty(x => x.LeaseToken, lease).SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5)));
            var node = JsonNode.Parse(original.EvidenceJson)!.AsObject(); node["LeaseToken"] = lease;
            var json = node.ToJsonString();
            db.MemoryExtractionInputReceipts.Add(new() { Id = Guid.NewGuid(), OrganizationId = original.OrganizationId,
                JobId = original.JobId, LeaseToken = lease, EvidenceJson = json,
                ReceiptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MemoryCaptureStatus.Pending)
            .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync()); Assert.Equal(0, factory.Calls);
        var job = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Failed, job.Status); Assert.Equal("memory_enrichment_input_receipt_capacity", job.LastError);
        Assert.Empty(await db.MemoryCaptureExclusions.ToListAsync()); Assert.Empty(await db.MemoryEnrichmentProviderLeases.ToListAsync());
        Assert.Equal(64, await db.MemoryExtractionInputReceipts.CountAsync());
    }

    private sealed class InputReceiptQuiesce : IBusinessAgentInstallationCleanup
    {
        internal bool Called { get; private set; }
        public Task QuiesceAsync(Guid organizationId, CancellationToken cancellationToken = default)
        { Called = true; return Task.CompletedTask; }
    }

    [MemoryPostgresFact]
    public async Task InputReceiptsAreRemovedThroughOrganizationCascadeAfterQuiescing()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await SeedFailedDispatchAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var cleanup = new InputReceiptQuiesce();
        await new OrganizationDataPurgeService(db, cleanup, NullLogger<OrganizationDataPurgeService>.Instance).PurgeAsync(fixture.OrganizationId);
        Assert.True(cleanup.Called); Assert.Empty(await db.MemoryExtractionInputReceipts.ToListAsync());
        Assert.Empty(await db.MemoryCaptureOutbox.ToListAsync()); Assert.Empty(await db.CoreOrganizations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task InputReceiptsCommitFailurePreventsProviderDispatchAndReleasesTheJob()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_input_receipt() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER reject_input_receipt BEFORE INSERT ON "MemoryExtractionInputReceipts" FOR EACH ROW EXECUTE FUNCTION reject_input_receipt();
            """);
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(0, factory.Calls); Assert.Empty(await db.MemoryExtractionInputReceipts.ToListAsync());
        Assert.Empty(await db.MemoryEnrichmentProviderLeases.ToListAsync());
        var job = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Pending, job.Status); Assert.Null(job.LeaseToken); Assert.Equal("memory_enrichment_failed", job.LastError);
    }

    [MemoryPostgresFact]
    public async Task InputReceiptsAppendForNewLeasesAndRetainEarlierAttempts()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var receipt = await SeedFailedDispatchAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1))
            .SetProperty(x => x.RetryGeneration, 1));
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(1, await fixture.Service(db, factory).ProcessPendingAsync());
        var receipts = await db.MemoryExtractionInputReceipts.AsNoTracking().OrderBy(x => x.RetryGeneration).ToListAsync();
        Assert.Equal(2, receipts.Count); Assert.Equal(receipt.EvidenceJson, receipts[0].EvidenceJson);
        Assert.NotEqual(receipts[0].LeaseToken, receipts[1].LeaseToken); Assert.Equal(1, receipts[1].RetryGeneration);
        Assert.Equal(AgentMemoryService.InspectDispatchInputsForErasure(receipts[0]).Inputs[0],
            AgentMemoryService.InspectDispatchInputsForErasure(receipts[1]).Inputs[0]);
    }

    [MemoryPostgresFact]
    public async Task InputReceiptsProtectHeldContributorOmittedByALaterAttempt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, assistant, source) = await SeedCaptureErasureAsync(fixture);
        var first = await SeedFailedDispatchAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, assistant, actor);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, assistant, actor,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true));
        var primaryCreatedAt = await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).Select(x => x.CreatedAt).SingleAsync();
        // Move the old answer outside the pairing window without deleting its held
        // stored source. The next attempt must use the later answer instead.
        await db.CoreConversationMessages.Where(x => x.Id == assistant).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.CreatedAt, primaryCreatedAt.AddSeconds(-1)));
        var later = Guid.NewGuid();
        db.CoreConversationMessages.Add(new() { Id = later, ConversationId = source.ConversationId, Role = ConversationRole.Assistant,
            Content = "An independent replacement answer.", CreatedAt = DateTimeOffset.UtcNow,
            Sequence = await db.CoreConversationMessages.MaxAsync(x => x.Sequence) + 1 });
        await db.SaveChangesAsync();
        await db.MemoryCaptureOutbox.Where(x => x.Id == first.JobId).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).CaptureMessageAsync(fixture.MessageId, enrich: true);
        var job = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.Id == first.JobId);
        Assert.Equal(new[] { fixture.MessageId, later }, AgentMemoryService.InspectExtractionForErasure(job.AcceptedExtractionJson!,
            fixture.MessageId, source.ConversationId).Inputs.Select(x => x.Id));
        Assert.Equal(2, await db.MemoryExtractionInputReceipts.CountAsync());
        await using var transaction = await db.Database.BeginTransactionAsync();
        var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
        var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source with { MessageId = fixture.MessageId }], default);
        Assert.Equal("memory_legal_hold_prevents_deletion", await cleanup.CheckRetentionAsync(plan, default));
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("lease")]
    [InlineData("generation")]
    [InlineData("organization")]
    [InlineData("expired")]
    [InlineData("hash")]
    [InlineData("erased")]
    public async Task InputReceiptDatabaseGuardRejectsWritesWithoutCurrentAuthority(string changed)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var original = await SeedFailedDispatchAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var lease = Guid.NewGuid();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, MemoryCaptureStatus.Processing)
            .SetProperty(x => x.LeaseToken, lease).SetProperty(x => x.LeaseExpiresAt,
                changed == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddMinutes(5)));
        var generation = changed == "generation" ? 1 : 0;
        var writtenLease = changed == "lease" ? Guid.NewGuid() : lease;
        var organization = changed == "organization" ? Guid.NewGuid() : fixture.OrganizationId;
        var node = JsonNode.Parse(original.EvidenceJson)!.AsObject(); node["LeaseToken"] = writtenLease; node["RetryGeneration"] = generation;
        var json = node.ToJsonString(); var hash = changed == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (changed == "erased")
        {
            var conversation = await db.CoreConversationMessages.Select(x => x.ConversationId).SingleAsync();
            db.MemorySourceInvalidations.Add(new() { SourceMessageId = fixture.MessageId, PreviousConversationId = conversation,
                ReasonCode = MemoryCaptureErasure.FailureCode, InvalidatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemoryExtractionInputReceipts" ("Id","OrganizationId","JobId","LeaseToken","RetryGeneration","EvidenceJson","ReceiptHash","CreatedAt")
            VALUES ({Guid.NewGuid()},{organization},{original.JobId},{writtenLease},{generation},{json},{hash},{DateTimeOffset.UtcNow})
            """));
        Assert.Contains("memory_input_evidence_lease_lost", error.MessageText);
        Assert.Single(await db.MemoryExtractionInputReceipts.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("hash")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("job")]
    [InlineData("lease")]
    [InlineData("generation")]
    [InlineData("tenant")]
    [InlineData("role")]
    [InlineData("missing-role")]
    [InlineData("missing-generation")]
    public async Task InputReceiptsRejectAlteredOrAmbiguousEvidence(string corruption)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var receipt = await SeedFailedDispatchAsync(fixture);
        var node = JsonNode.Parse(receipt.EvidenceJson)!.AsObject();
        switch (corruption)
        {
            case "unknown-field": node["PrivateText"] = "private-memory"; break;
            case "duplicate-field": node["version"] = 1; break;
            case "job": node["JobId"] = Guid.NewGuid(); break;
            case "lease": node["LeaseToken"] = Guid.NewGuid(); break;
            case "generation": node["RetryGeneration"] = 1; break;
            case "tenant": node["Partition"]!["TenantId"] = Guid.NewGuid().ToString("D"); break;
            case "role": node["Sources"]!["Messages"]![0]!["Role"] = (int)ConversationRole.Assistant; break;
            case "missing-role": node["Sources"]!["Messages"]![0]!.AsObject().Remove("Role"); break;
            case "missing-generation": node.Remove("RetryGeneration"); break;
        }
        receipt.EvidenceJson = node.ToJsonString();
        receipt.ReceiptHash = corruption == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(receipt.EvidenceJson)));
        var error = Assert.Throws<InvalidOperationException>(() => AgentMemoryService.InspectDispatchInputsForErasure(receipt));
        Assert.Equal("memory_erasure_capture_lineage_review_required", error.Message);
    }

    [MemoryPostgresFact]
    public async Task InputReceiptMigrationPreservesLegacyJobsAndGuardsEvidenceAndDowngrade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var migration = new DurableExtractionInputEvidence();
        await db.Database.ExecuteSqlRawAsync(DurableExtractionInputEvidence.InstallTriggers);
        var original = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        await RunConversationMigrationAsync(db, migration.DownOperations);
        await RunConversationMigrationAsync(db, migration.UpOperations);
        Assert.Equal(original.Id, (await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(await db.MemoryExtractionInputReceipts.ToListAsync());
        Assert.Equal(1, await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).ProcessPendingAsync());
        var receipt = await db.MemoryExtractionInputReceipts.SingleAsync();
        receipt.ReceiptHash = new string('0', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        var update = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MemoryExtractionInputReceipts\" SET \"ReceiptHash\"={new string('0', 64)} WHERE \"Id\"={receipt.Id}"));
        Assert.Contains("memory_input_evidence_immutable", update.MessageText);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"MemoryExtractionInputReceipts\" WHERE \"Id\"={receipt.Id}"));
        Assert.Contains("memory_input_evidence_immutable", delete.MessageText);
        var downgrade = await Assert.ThrowsAsync<PostgresException>(() => RunConversationMigrationAsync(db, migration.DownOperations));
        Assert.Contains("memory_input_evidence_downgrade_requires_snapshot", downgrade.MessageText);
        // A completed/stale lease cannot append a new receipt, even through raw SQL.
        var insert = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemoryExtractionInputReceipts" ("Id","OrganizationId","JobId","LeaseToken","RetryGeneration","EvidenceJson","ReceiptHash","CreatedAt")
            SELECT {Guid.NewGuid()},"OrganizationId","JobId","LeaseToken","RetryGeneration","EvidenceJson","ReceiptHash","CreatedAt"
            FROM "MemoryExtractionInputReceipts" WHERE "Id"={receipt.Id}
            """));
        Assert.Contains("memory_input_evidence_lease_lost", insert.MessageText);
        await db.MemoryCaptureOutbox.ExecuteDeleteAsync();
        Assert.Empty(await db.MemoryExtractionInputReceipts.ToListAsync());
        await RunConversationMigrationAsync(db, migration.DownOperations);
    }
}
