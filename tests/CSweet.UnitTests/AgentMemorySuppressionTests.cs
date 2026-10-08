using CSweet.Contracts.Memory;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task SuppressionWaitsForExistingSourceReadersBeforeBlockingDerivativeWrites()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        await using var review = fixture.Context(); await review.Database.OpenConnectionAsync();
        var service = new AgentMemoryReviewService(review, fixture.Store, TimeProvider.System);
        var preview = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        await using var worker = fixture.Context();
        await using var transaction = await worker.Database.BeginTransactionAsync();
        await worker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM csweet_memory_episodes WHERE id={episode.Id} FOR SHARE");
        var pending = service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pid = ((NpgsqlConnection)review.Database.GetDbConnection()).ProcessID;
            while (!await worker.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid={pid} AND mode='ExclusiveLock' AND NOT granted) AS \"Value\"").SingleAsync(deadline.Token))
            {
                Assert.False(pending.IsCompleted, "Suppression must wait for the source reader.");
                await Task.Delay(25, deadline.Token);
            }
            await using var enlisted = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            await enlisted.WriteBlockAsync(new(Guid.NewGuid(), fixture.Partition, "Concurrent", "Concurrent derived evidence", 1, 100, true,
                MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow) { SourceEpisodeIds = [episode.Id] }, deadline.Token);
            await transaction.CommitAsync(deadline.Token);
            await pending.WaitAsync(deadline.Token);
            Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        }
        finally
        {
            if (transaction.GetDbTransaction().Connection is not null) await transaction.RollbackAsync();
            await pending.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    [MemoryPostgresFact]
    public async Task SuppressionDuringExtractionCannotCommitAcceptedOutputOrDerivedMemory()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var factory = new ScriptedProviderFactory((_, token) =>
            ((IMemorySuppressionStore)fixture.Store).SuppressEpisodeAsync(fixture.Partition, fixture.MessageId, token));
        await using var db = fixture.Context();
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(1, factory.Calls);
        var job = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        Assert.Null(job.AcceptedExtractionJson);
        Assert.Equal("memory_enrichment_source_invalidated", job.LastError);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.True(await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == fixture.MessageId));
    }

    [MemoryPostgresFact]
    public async Task SuppressionInvalidatesPreparedContextAndStopsPendingEnrichment()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture);
        await using var db = fixture.Context();
        var factory = new UsageProviderFactory();
        var service = fixture.Service(db, factory);
        var recall = await service.PrepareTurnRecallAsync(turn.Id);
        Assert.NotNull(recall.Context);
        var work = ReceiptWork(fixture, turn, recall.ReceiptJson);
        var guard = new MemoryRecallDispatchEvidence(db);
        await guard.AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default);
        await ((IMemorySuppressionStore)fixture.Store).SuppressEpisodeAsync(fixture.Partition, fixture.MessageId);
        await Assert.ThrowsAsync<CSweet.Infrastructure.Llm.ProviderDispatchDeniedException>(() =>
            guard.AuthorizeWorkAsync(work, fixture.EmployeeId.ToString("D"), default));
        Assert.Equal(0, await service.ProcessPendingAsync(limit: 1));
        Assert.Null(factory.SelectedProviderId);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        var job = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
        Assert.Null(job.AcceptedExtractionJson);
        Assert.Equal("memory_source_suppressed", job.LastError);
        Assert.True(await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == fixture.MessageId));
    }

    [MemoryPostgresFact]
    public async Task HumanSuppressionPreservesHeldEvidenceAuditsAndRechecksReplayAuthority()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var episode = await SeedLegacyEpisode(fixture, classified: true);
        await using var db = fixture.Context();
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        Assert.True(preview.LegalHold); Assert.False(preview.IsSuppressed);
        var request = new SuppressMemorySourceRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken);
        var result = await service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request);
        Assert.True((await service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request)).WasReplay);
        var current = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.True(current.IsSuppressed); Assert.True(current.LegalHold); Assert.Equal(episode.Content, current.Content);
        Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
        var history = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", episode.Id, actor);
        Assert.Contains("suppressed", history.Items[^1].State!);
        Assert.Equal(result.ReceiptId, Assert.Single(history.Items[^1].Reviews).ReceiptId);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SuppressSourceAsync(fixture.OrganizationId,
            fixture.EmployeeId, episode.Id, actor, request with { ExpectedRevision = request.ExpectedRevision + 1 }));
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request));
    }

    [MemoryPostgresFact]
    public async Task SuppressionRejectsStalePreviewAndAuditFailureRollsBackTombstoneAndSource()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        var request = new SuppressMemorySourceRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','false') WHERE id={episode.Id}");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request));
        preview = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        request = request with { ExpectedRevision = preview.Revision, EvidenceToken = preview.EvidenceToken };
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_suppression() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER fail_suppression BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryEpisode') EXECUTE FUNCTION fail_suppression();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request));
        Assert.False(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM csweet_memory_suppressions").SingleAsync());
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_suppression ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_suppression();");
        await service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request);
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).IsSuppressed);
    }
}
