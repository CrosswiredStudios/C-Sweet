using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(Guid Actor, Guid Assistant, MemoryCaptureErasure.Source Source)> SeedCaptureErasureAsync(DurabilityFixture fixture)
    {
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var assistant = await AddSourceAssistantAsync(fixture);
        await using var db = fixture.Context();
        var job = await db.MemoryCaptureOutbox.SingleAsync();
        job.Status = MemoryCaptureStatus.Pending; job.Attempts = 0; job.LastError = null;
        job.AcceptedExtractionJson = null; job.ExtractionAcceptedAt = null;
        await db.SaveChangesAsync();
        var conversation = await db.CoreConversationMessages.Where(x => x.Id == assistant).Select(x => x.ConversationId).SingleAsync();
        return (actor, assistant, new(assistant, conversation, fixture.EmployeeId, fixture.HumanId));
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureErasureEnlistsPairedPayloadCleanupSourceFencesStoreErasureAndAudit(bool failAudit)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, assistant, source) = await SeedCaptureErasureAsync(fixture);
        await using (var capture = fixture.Context())
            await fixture.Service(capture, new PairedEvidenceProviderFactory()).CaptureMessageAsync(fixture.MessageId, enrich: true);
        await using (var seed = fixture.Context())
        {
            var primary = await seed.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
            Assert.NotNull(primary.AcceptedExtractionJson);
            seed.MemoryEnrichmentProviderLeases.Add(new() { ProviderId = fixture.ProviderId, JobId = primary.Id,
                LeaseToken = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
            await seed.SaveChangesAsync();
            if (failAudit) await seed.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_erasure_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
                CREATE TRIGGER fail_erasure_audit BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW
                    WHEN (NEW."SourceEntityType"='MemoryErasure') EXECUTE FUNCTION fail_erasure_audit();
                """);
        }
        await using (var db = fixture.Context())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            Assert.Equal(2, plan.Jobs.Count);
            await using var enlisted = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var preview = await enlisted.PreviewEpisodeErasureAsync(fixture.Partition, assistant);
            Assert.Null(preview.BlockedReason);
            var cleanupResult = await cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default);
            Assert.Equal(new MemoryCaptureErasure.Result(1, 2), cleanupResult);
            await enlisted.EraseEpisodeAsync(fixture.Partition, assistant, preview.EvidenceToken);
            db.QueueAudit(new AuditEventWriteRequest("memory.erasure.test", "Memory", OrganizationId: fixture.OrganizationId,
                EntityType: "MemoryErasure", EntityId: assistant, Summary: "Content-free erasure evidence.", UseAmbientOrganization: false));
            if (failAudit)
            {
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                await transaction.RollbackAsync();
            }
            else
            {
                await db.SaveChangesAsync(); await transaction.CommitAsync();
            }
        }
        await using var check = fixture.Context();
        var stored = await fixture.Store.ExportAsync(fixture.Partition);
        var jobs = await check.MemoryCaptureOutbox.ToListAsync();
        if (failAudit)
        {
            Assert.Contains(stored.Episodes, x => x.Id == assistant);
            Assert.NotNull(jobs.Single(x => x.ConversationMessageId == fixture.MessageId).AcceptedExtractionJson);
            Assert.Empty(await check.MemorySourceInvalidations.ToListAsync());
            Assert.Single(await check.MemoryEnrichmentProviderLeases.ToListAsync());
        }
        else
        {
            Assert.DoesNotContain(stored.Episodes, x => x.Id == assistant);
            Assert.Contains(stored.Episodes, x => x.Id == fixture.MessageId); // Independent source remains.
            Assert.Empty(stored.Claims); Assert.Empty(stored.Entities); Assert.Empty(stored.Edges); Assert.Empty(stored.Procedures);
            Assert.All(jobs, job => { Assert.Null(job.AcceptedExtractionJson); Assert.Null(job.ExtractionAcceptedAt);
                Assert.Null(job.LeaseToken); Assert.Equal(1, job.RetryGeneration); Assert.Equal(MemoryCaptureStatus.Failed, job.Status);
                Assert.Equal(MemoryCaptureErasure.FailureCode, job.LastError); });
            Assert.Equal(assistant, (await check.MemorySourceInvalidations.SingleAsync()).SourceMessageId);
            Assert.Empty(await check.MemoryEnrichmentProviderLeases.ToListAsync());
        }
    }

    [MemoryPostgresFact]
    public async Task CaptureErasureUsesDurableInFlightInputsAndFencesLateProviderResults()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, assistant, source) = await SeedCaptureErasureAsync(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProviderFactory(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        await using var worker = fixture.Context();
        var processing = fixture.Service(worker, provider).CaptureMessageAsync(fixture.MessageId, enrich: true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using var db = fixture.Context(); await using var transaction = await db.Database.BeginTransactionAsync();
            var receipt = await db.MemoryExtractionInputReceipts.AsNoTracking().SingleAsync();
            var evidence = AgentMemoryService.InspectDispatchInputsForErasure(receipt);
            Assert.Equal(new[] { fixture.MessageId, assistant }, evidence.Inputs.Select(x => x.Id));
            Assert.DoesNotContain("My name is Alice", receipt.EvidenceJson);
            Assert.Null((await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.Id == receipt.JobId)).AcceptedExtractionJson);
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var preview = await store.PreviewEpisodeErasureAsync(fixture.Partition, assistant);
            await cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default);
            await store.EraseEpisodeAsync(fixture.Partition, assistant, preview.EvidenceToken);
            await transaction.CommitAsync();
        }
        finally { release.TrySetResult(); }
        await processing.WaitAsync(TimeSpan.FromSeconds(30));
        await using var fresh = fixture.Context();
        var primary = await fresh.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
        Assert.Null(primary.AcceptedExtractionJson); Assert.Equal(MemoryCaptureErasure.FailureCode, primary.LastError);
        Assert.Equal(MemoryCaptureStatus.Failed, primary.Status); Assert.Equal(1, provider.Calls);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        var failures = await new AgentMemoryRecoveryService(fresh, TimeProvider.System).ListFailuresAsync(
            fixture.OrganizationId, fixture.EmployeeId, actor);
        Assert.Contains(failures.Items, x => x.Id == primary.Id && x.FailureCode == MemoryCaptureErasure.FailureCode);
        Assert.Single(await fresh.MemoryExtractionInputReceipts.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentMemoryRecoveryService(fresh, TimeProvider.System)
            .RetryAsync(fixture.OrganizationId, fixture.EmployeeId, primary.Id, actor, new(Guid.NewGuid(), primary.RetryGeneration)));
        Assert.Equal(0, await fixture.Service(fresh, provider).ProcessPendingAsync());
        Assert.Equal(1, provider.Calls);
    }

    [MemoryPostgresFact]
    public async Task CaptureErasureAlsoFencesStaleRowsWithNoActiveLeaseAndIsIdempotent()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, assistant, source) = await SeedCaptureErasureAsync(fixture);
        await using var stale = fixture.Context();
        var job = await stale.MemoryCaptureOutbox.SingleAsync();
        job.AcceptedExtractionJson = "{\"late\":\"private-memory\"}";
        await using (var db = fixture.Context())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            // Directly forgotten source, with no active lease.
            var direct = source with { MessageId = fixture.MessageId };
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [direct], default);
            Assert.Equal(new MemoryCaptureErasure.Result(1, 1), await cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default));
            Assert.Equal(new MemoryCaptureErasure.Result(0, 0), await cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default));
            await transaction.CommitAsync();
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using var fresh = fixture.Context();
        Assert.Equal(1, (await fresh.MemoryCaptureOutbox.SingleAsync()).RetryGeneration);
        Assert.Null((await fresh.MemoryCaptureOutbox.SingleAsync()).AcceptedExtractionJson);
    }

    [MemoryPostgresTheory]
    [InlineData("conversation")]
    [InlineData("employee")]
    [InlineData("human")]
    [InlineData("organization")]
    public async Task CaptureErasureRejectsChangedSourceBindingsWithoutPartialCleanup(string field)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, _, source) = await SeedCaptureErasureAsync(fixture);
        source = field switch { "conversation" => source with { ConversationId = Guid.NewGuid() },
            "employee" => source with { EmployeeId = Guid.NewGuid() }, "human" => source with { HumanId = Guid.NewGuid() }, _ => source };
        await using var db = fixture.Context(); await using var transaction = await db.Database.BeginTransactionAsync();
        var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cleanup.PrepareAsync(
            field == "organization" ? Guid.NewGuid() : fixture.OrganizationId, [source], default));
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task CaptureErasureRejectsLegacyEnvelopeThatCannotProveItsContributors()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, _, source) = await SeedCaptureErasureAsync(fixture);
        await using (var seed = fixture.Context())
        {
            var job = await seed.MemoryCaptureOutbox.SingleAsync();
            job.AcceptedExtractionJson = "{\"SchemaVersion\":1,\"unknown\":\"private-memory\"}";
            await seed.SaveChangesAsync();
        }
        await using var db = fixture.Context(); await using var transaction = await db.Database.BeginTransactionAsync();
        var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.PrepareAsync(fixture.OrganizationId, [source], default));
        Assert.Equal("memory_erasure_lineage_review_required", error.Message);
        Assert.NotNull((await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync()).AcceptedExtractionJson);
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task CaptureErasureRollsBackCleanupWhenTheStoreFindsARetentionHold()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await SeedRecoveryAsync(fixture);
        var episode = await SeedLegacyEpisode(fixture);
        await using (var seed = fixture.Context())
            await seed.MemoryCaptureOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.AcceptedExtractionJson, (string?)null)
                .SetProperty(x => x.ExtractionAcceptedAt, (DateTimeOffset?)null));
        await using (var db = fixture.Context())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var conversation = await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).Select(x => x.ConversationId).SingleAsync();
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId,
                [new(fixture.MessageId, conversation, fixture.EmployeeId, fixture.HumanId)], default);
            await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var preview = await store.PreviewEpisodeErasureAsync(fixture.Partition, episode.Id);
            await cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.EraseEpisodeAsync(fixture.Partition, episode.Id, preview.EvidenceToken));
            Assert.Equal("memory_legal_hold_prevents_deletion", error.Message);
            await transaction.RollbackAsync();
        }
        await using var fresh = fixture.Context();
        Assert.Empty(await fresh.MemorySourceInvalidations.ToListAsync());
        Assert.Null((await fresh.MemoryCaptureOutbox.SingleAsync()).AcceptedExtractionJson);
        Assert.Equal(0, (await fresh.MemoryCaptureOutbox.SingleAsync()).RetryGeneration);
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).LegalHold);
    }

    [MemoryPostgresFact]
    public async Task CaptureErasurePreservesAnUnrelatedCompletedExtractionInTheSameConversation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, _, source) = await SeedCaptureErasureAsync(fixture);
        var other = Guid.NewGuid();
        await using (var capture = fixture.Context())
        {
            await fixture.Service(capture, new PairedEvidenceProviderFactory()).CaptureMessageAsync(fixture.MessageId, enrich: true);
            var sequence = await capture.CoreConversationMessages.MaxAsync(x => x.Sequence) + 1;
            capture.CoreConversationMessages.Add(new ConversationMessage { Id = other, ConversationId = source.ConversationId,
                Role = ConversationRole.User, Content = "My second independent preference.", CreatedAt = DateTimeOffset.UtcNow, Sequence = sequence });
            await capture.SaveChangesAsync();
            await fixture.Service(capture, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).CaptureMessageAsync(other, enrich: true);
        }
        await using var db = fixture.Context();
        var before = JsonSerializer.Serialize(await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.ConversationMessageId == other));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            Assert.Equal(2, plan.Jobs.Count);
            await cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default); await transaction.CommitAsync();
        }
        Assert.Equal(before, JsonSerializer.Serialize(await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.ConversationMessageId == other)));
    }

    [MemoryPostgresFact]
    public async Task CaptureErasureRequiresOwnedBarrierAndFailsFastBehindWorkers()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, _, source) = await SeedCaptureErasureAsync(fixture);
        await using var db = fixture.Context(); var cleanup = new MemoryCaptureErasure(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.AcquireAsync(default));
        await using (var writer = fixture.Context())
        await using (var writing = await writer.Database.BeginTransactionAsync())
        {
            await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"MemoryCaptureOutbox\" WHERE \"ConversationMessageId\"={fixture.MessageId} FOR UPDATE");
            await using var transaction = await db.Database.BeginTransactionAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => cleanup.AcquireAsync(deadline.Token));
        }
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.PrepareAsync(fixture.OrganizationId, [source], default));
            await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            await transaction.RollbackAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default));
        }
    }
}
