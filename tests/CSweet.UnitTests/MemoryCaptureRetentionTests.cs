using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(Guid Actor, Guid Assistant, MemoryCaptureErasure.Source Source)> SeedAcceptedCaptureOnlyAsync(DurabilityFixture fixture)
    {
        var result = await SeedCaptureErasureAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await fixture.Store.InitializeAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_retention_fixture_claim() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected apply failure'; END $$;
            CREATE TRIGGER reject_retention_fixture_claim BEFORE INSERT ON csweet_memory_claims FOR EACH ROW EXECUTE FUNCTION reject_retention_fixture_claim();
            """);
        try { await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).CaptureMessageAsync(fixture.MessageId, enrich: true); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_retention_fixture_claim ON csweet_memory_claims; DROP FUNCTION reject_retention_fixture_claim();"); }
        Assert.NotNull((await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.ConversationMessageId == fixture.MessageId)).AcceptedExtractionJson);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        return result;
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureRetentionProtectsIndependentAcceptedInputUntilReviewedRelease(bool forgetPrimary)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, assistant, source) = await SeedAcceptedCaptureOnlyAsync(fixture);
        var target = forgetPrimary ? fixture.MessageId : assistant;
        var independent = forgetPrimary ? assistant : fixture.MessageId;
        source = source with { MessageId = target };
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, independent, actor);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, independent, actor,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true));
        // The held source is not part of the store's dependency deletion set. Its copy
        // exists only inside the accepted provider result that the host would clear.
        var storePreview = await ((IMemoryErasureStore)fixture.Store).PreviewEpisodeErasureAsync(fixture.Partition, target);
        Assert.Null(storePreview.BlockedReason);
        var before = JsonSerializer.Serialize(await db.MemoryCaptureOutbox.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync());
        var impact = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, target, actor);
        Assert.Equal("memory_legal_hold_prevents_deletion", impact.BlockedReason);
        Assert.NotNull(impact.Execution); Assert.True(impact.Execution.ExtractionJobs > 0);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default));
            Assert.Equal("memory_legal_hold_prevents_deletion", error.Message);
            Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
            Assert.Equal(before, JsonSerializer.Serialize(await db.MemoryCaptureOutbox.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
            await transaction.RollbackAsync();
        }
        hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, independent, actor);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, independent, actor,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, false));
        Assert.Null((await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, target, actor)).BlockedReason);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var preview = await store.PreviewEpisodeErasureAsync(fixture.Partition, target);
            await cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default);
            await store.EraseEpisodeAsync(fixture.Partition, target, preview.EvidenceToken);
            await transaction.CommitAsync();
        }
        Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, independent));
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, target));
        Assert.Null((await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.ConversationMessageId == fixture.MessageId)).AcceptedExtractionJson);
    }

    [MemoryPostgresTheory]
    [InlineData("legacy")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("missing-contributor")]
    [InlineData("omitted-contributor")]
    [InlineData("bad-checksum")]
    [InlineData("wrong-checksum")]
    [InlineData("misattributed-contributor")]
    public async Task CaptureRetentionRejectsAmbiguousDirectJobEvidenceBeforeAnyCleanup(string corruption)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _, source) = await SeedAcceptedCaptureOnlyAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var job = await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
        var node = JsonNode.Parse(job.AcceptedExtractionJson!)!.AsObject();
        if (corruption == "legacy") node["SchemaVersion"] = 1;
        if (corruption == "unknown-field") node["OtherInput"] = "private";
        if (corruption == "missing-contributor") node["Sources"]!["Messages"]![1] = null;
        if (corruption == "omitted-contributor") node["Sources"]!["Messages"]!.AsArray().RemoveAt(1);
        if (corruption == "bad-checksum") node["Sources"]!["Messages"]![1]!["Checksum"] = "unknown";
        if (corruption == "wrong-checksum") node["Sources"]!["Messages"]![1]!["Checksum"] = new string('A', 64);
        if (corruption == "misattributed-contributor")
        {
            var original = (await fixture.Store.ExportAsync(fixture.Partition)).Episodes.Single(x => x.Source.Type == "assistant");
            var substitute = Guid.NewGuid(); var content = "An unrelated input from the same conversation.";
            var metadata = original.Metadata!.ToDictionary(x => x.Key, x => x.Value); metadata["messageId"] = substitute.ToString("D");
            await fixture.Store.AppendEpisodeAsync(MemorySourceIntegrity.Seal(original with { Id = substitute, Content = content,
                Source = original.Source with { Id = substitute.ToString("D") }, Metadata = metadata, IdempotencyKey = substitute.ToString("N"),
                Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant() }));
            node["Sources"]!["Messages"]![1]!["Id"] = substitute;
        }
        var json = node.ToJsonString();
        if (corruption == "duplicate-field") json = json[..^1] + ",\"schemaVersion\":3}";
        job.AcceptedExtractionJson = json; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var persisted = (await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.Id == job.Id)).AcceptedExtractionJson;
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_capture_lineage_review_required", impact.BlockedReason); Assert.Null(impact.Execution);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
        var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source with { MessageId = fixture.MessageId }], default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.StageAsync(plan, DateTimeOffset.UtcNow, default));
        Assert.Equal("memory_erasure_capture_lineage_review_required", error.Message);
        Assert.Equal(persisted, (await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.Id == job.Id)).AcceptedExtractionJson);
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task CaptureRetentionRequiresReviewWhenAnIndependentInputsCurrentStateIsMissing()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, assistant, _) = await SeedAcceptedCaptureOnlyAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM csweet_memory_episodes WHERE id={fixture.MessageId}");
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, assistant, actor);
        Assert.Equal("memory_erasure_capture_retention_review_required", impact.BlockedReason); Assert.Null(impact.Execution);
        Assert.NotNull((await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.ConversationMessageId == fixture.MessageId)).AcceptedExtractionJson);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureRetentionBindsHistoricalInputButUsesItsCurrentHold(bool held)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, assistant, _) = await SeedAcceptedCaptureOnlyAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var original = (await fixture.Store.ExportAsync(fixture.Partition)).Episodes.Single(x => x.Id == fixture.MessageId);
        var content = "The source changed after this extraction was accepted.";
        // Preserve the immutable seal: altered current content must not be blessed.
        // Its retained original snapshot proves the old input; current policy owns holds.
        var changed = original with { Content = content, LegalHold = held,
            Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant() };
        Assert.False(MemorySourceIntegrity.IsVerified(changed));
        var json = JsonSerializer.Serialize(changed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=CAST({json} AS jsonb) WHERE id={fixture.MessageId}");
        var impact = await new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, assistant, actor);
        Assert.Equal(held ? "memory_legal_hold_prevents_deletion" : null, impact.BlockedReason);
        Assert.NotNull(impact.Execution);
    }

    [MemoryPostgresFact]
    public async Task CaptureRetentionDeniesAnExtractionEnvelopeFromAnotherAudience()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _, _) = await SeedAcceptedCaptureOnlyAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var job = await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
        var node = JsonNode.Parse(job.AcceptedExtractionJson!)!;
        node["Episode"]!["Partition"]!["UserId"] = Guid.NewGuid().ToString("D");
        job.AcceptedExtractionJson = node.ToJsonString(); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System)
            .GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor));
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureRetentionSerializesWithIndependentHoldChanges(bool writerFirst)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, _, source) = await SeedAcceptedCaptureOnlyAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        await using var writer = fixture.Context(); await writer.Database.OpenConnectionAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        await using (var writing = await writer.Database.BeginTransactionAsync())
        {
            var cleanup = new MemoryCaptureErasure(db); await cleanup.AcquireAsync(default);
            var plan = await cleanup.PrepareAsync(fixture.OrganizationId, [source], default);
            if (writerFirst)
            {
                await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','true') WHERE id={fixture.MessageId}");
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => cleanup.CheckRetentionAsync(plan, default));
            }
            else
            {
                Assert.Null(await cleanup.CheckRetentionAsync(plan, default));
                await writer.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='150ms'");
                var error = await Assert.ThrowsAsync<PostgresException>(() => writer.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','true') WHERE id={fixture.MessageId}"));
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            }
            await writing.RollbackAsync(); await transaction.RollbackAsync();
        }
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
        Assert.NotNull((await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.ConversationMessageId == fixture.MessageId)).AcceptedExtractionJson);
    }
}
