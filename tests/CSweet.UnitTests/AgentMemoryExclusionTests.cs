using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task ExclusionSurvivesDeletedQueueAndSourceAndPreventsRecapture()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        var service = fixture.Service(db, factory);
        await service.CaptureMessageAsync(fixture.MessageId);
        await fixture.Store.DeleteScopeAsync(fixture.Partition);
        Assert.Equal(0, await service.ProcessPendingAsync());
        var job = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Failed, job.Status);
        var marker = Assert.Single(await db.MemoryCaptureExclusions.AsNoTracking().ToListAsync());
        Assert.Equal(fixture.MessageId, marker.SourceMessageId);
        Assert.Equal(job.Id, marker.TriggerJobId);
        var evidence = Assert.Single(await db.AuditOutbox.Where(x => x.SourceEntityType == nameof(MemoryCaptureExclusion)).ToListAsync());
        Assert.DoesNotContain("Alice", JsonSerializer.Serialize(marker));
        Assert.DoesNotContain("Alice", evidence.RequestJson);

        await db.MemoryCaptureOutbox.ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(0, await service.ProcessPendingAsync());
        Assert.Empty(await db.MemoryCaptureOutbox.ToListAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => service.CaptureMessageAsync(fixture.MessageId));
        await Assert.ThrowsAnyAsync<Exception>(() => service.CaptureMessageAsync(fixture.MessageId, enrich: true));
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE source_restore_fixture AS SELECT * FROM \"CoreConversationMessages\"");
        await db.CoreConversationMessages.ExecuteDeleteAsync();
        Assert.Single(await db.MemoryCaptureExclusions.AsNoTracking().ToListAsync());
        // Simulate restoring a database row, not creating a second message/audit event with the same ID.
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"CoreConversationMessages\" SELECT * FROM source_restore_fixture");
        Assert.Equal(0, await service.ProcessPendingAsync());
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.Equal(0, factory.Calls);
        Assert.Single(await db.AuditOutbox.Where(x => x.SourceEntityType == nameof(MemoryCaptureExclusion)).ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ExclusionAndTerminalStateRollBackTogetherWhenAuditPersistenceFails()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_exclusion_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."SourceEntityType" = 'MemoryCaptureExclusion' THEN RAISE EXCEPTION 'injected exclusion audit failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_exclusion_audit BEFORE INSERT ON "ComputeAuditOutbox"
                FOR EACH ROW EXECUTE FUNCTION reject_exclusion_audit();
            """);
        var factory = new ScriptedProviderFactory(async (_, _) =>
        {
            await using var changed = fixture.Context();
            await changed.CoreConversationMessages.ExecuteUpdateAsync(set => set.SetProperty(x => x.Content, "Changed source"));
        });
        var service = fixture.Service(db, factory);
        Assert.Equal(0, await service.ProcessPendingAsync());
        Assert.Equal(MemoryCaptureStatus.Processing, (await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.MemoryCaptureExclusions.ToListAsync());
        Assert.Empty(await db.AuditOutbox.Where(x => x.SourceEntityType == nameof(MemoryCaptureExclusion)).ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_exclusion_audit ON \"ComputeAuditOutbox\"; DROP FUNCTION reject_exclusion_audit();");
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(0, await service.ProcessPendingAsync());
        Assert.Equal(MemoryCaptureStatus.Failed, (await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await db.MemoryCaptureExclusions.ToListAsync());
        Assert.Single(await db.AuditOutbox.Where(x => x.SourceEntityType == nameof(MemoryCaptureExclusion)).ToListAsync());
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task ExclusionsCannotBeRewrittenOrDeletedThroughTrackedPersistence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        await SeedExclusionAsync(fixture, fixture.MessageId);
        await using var db = fixture.Context();
        var exclusion = await db.MemoryCaptureExclusions.SingleAsync();
        exclusion.ReasonCode = "override";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Remove(await db.MemoryCaptureExclusions.SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Empty(db.Model.FindEntityType(typeof(MemoryCaptureExclusion))!.GetForeignKeys());
    }

    [Fact]
    public async Task ExcludedAssistantIsNotUsedByNewExtraction()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var assistantId = await AddSourceAssistantAsync(fixture);
        await SeedExclusionAsync(fixture, assistantId);
        await using var db = fixture.Context();
        Assert.Equal(1, await fixture.Service(db, new UsageProviderFactory()).ProcessPendingAsync());
        var job = await db.MemoryCaptureOutbox.SingleAsync();
        using var envelope = JsonDocument.Parse(job.AcceptedExtractionJson!);
        Assert.Single(envelope.RootElement.GetProperty("Sources").GetProperty("Messages").EnumerateArray());
        Assert.DoesNotContain("Hello Alice", job.AcceptedExtractionJson);
    }

    [Fact]
    public async Task ExclusionDuringInferencePreventsAcceptanceAndAlsoExcludesDependentCapture()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var assistantId = await AddSourceAssistantAsync(fixture);
        var factory = new ScriptedProviderFactory(async (_, _) => await SeedExclusionAsync(fixture, assistantId));
        await using var db = fixture.Context();
        await fixture.Service(db, factory).ProcessPendingAsync();
        var job = await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
        Assert.Equal(MemoryCaptureStatus.Failed, job.Status);
        Assert.Null(job.AcceptedExtractionJson);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.Equal(2, await db.MemoryCaptureExclusions.CountAsync());
    }

    [Fact]
    public async Task ExclusionBlocksRetryEvenIfTheJobErrorWasOverwritten()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        await SeedExclusionAsync(fixture, fixture.MessageId);
        await using var db = fixture.Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentMemoryRecoveryService(db, TimeProvider.System)
            .RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, actorId, new(Guid.NewGuid(), 0)));
        Assert.Empty(await db.MemoryCaptureRetryReceipts.ToListAsync());
        var failures = await new AgentMemoryRecoveryService(db, TimeProvider.System)
            .ListFailuresAsync(fixture.OrganizationId, fixture.EmployeeId, actorId);
        Assert.Equal("memory_capture_excluded", Assert.Single(failures.Items).FailureCode);
    }

    [MemoryPostgresFact]
    public async Task ExclusionMigrationPreservesLegacyFailuresIndependentlyOfTheirJobs()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        await using var db = fixture.Context();
        var job = await db.MemoryCaptureOutbox.SingleAsync();
        job.Status = MemoryCaptureStatus.Failed;
        job.LastError = "memory_enrichment_source_invalidated";
        job.AcceptedExtractionJson = "{}";
        await db.SaveChangesAsync();
        var migration = new DurableMemoryCaptureExclusions();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var marker = Assert.Single(await db.MemoryCaptureExclusions.AsNoTracking().ToListAsync());
        Assert.Equal(job.ConversationMessageId, marker.SourceMessageId);
        Assert.Equal(job.Id, marker.TriggerJobId);
        Assert.Equal("{}", job.AcceptedExtractionJson);
        await db.MemoryCaptureOutbox.ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(0, await fixture.Service(db, new UsageProviderFactory()).ProcessPendingAsync());
        Assert.Empty(await db.MemoryCaptureOutbox.ToListAsync());
    }

    private static async Task SeedExclusionAsync(DurabilityFixture fixture, Guid sourceId)
    {
        await using var db = fixture.Context();
        db.MemoryCaptureExclusions.Add(new MemoryCaptureExclusion
        {
            SourceMessageId = sourceId, OrganizationId = fixture.OrganizationId, EmployeeId = fixture.EmployeeId,
            TriggerJobId = Guid.NewGuid(), ExcludedAt = DateTimeOffset.UtcNow, ReasonCode = "memory_enrichment_source_invalidated"
        });
        await db.SaveChangesAsync();
    }
}
