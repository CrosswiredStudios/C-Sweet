using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task GenericBusyProviderPreservesLastAttemptAcrossRepeatedDeferralsAndRestart()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        await AcceptGenericAsync(fixture, db, episode);
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(set => set.SetProperty(x => x.Attempts, 9));
        var capture = await db.MemoryCaptureOutbox.AsNoTracking().SingleAsync();
        var reservation = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, MemoryCaptureStatus.Processing)
            .SetProperty(x => x.LeaseToken, reservation).SetProperty(x => x.LeaseExpiresAt, expires));
        db.MemoryEnrichmentProviderLeases.Add(new() { ProviderId = fixture.ProviderId, JobId = capture.Id,
            LeaseToken = reservation, ExpiresAt = expires });
        await db.SaveChangesAsync();
        var original = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        for (var pass = 0; pass < 2; pass++)
        {
            await using var restarted = fixture.Context();
            Assert.Equal(0, await fixture.Service(restarted, factory).ProcessPendingAsync());
            var deferred = await restarted.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
            Assert.Equal(MemoryCaptureStatus.Pending, deferred.Status);
            Assert.Equal(9, deferred.Attempts);
            Assert.Equal("memory_provider_busy", deferred.LastError);
            Assert.Null(deferred.LeaseToken); Assert.Null(deferred.LeaseExpiresAt);
            Assert.Null(deferred.AcceptedExtractionJson);
            Assert.Equal(original.SourceHash, deferred.SourceHash); Assert.Equal(original.SourceJson, deferred.SourceJson);
            Assert.Empty(await restarted.MemoryEpisodeExtractionReceipts.ToListAsync());
            Assert.Equal(reservation, (await restarted.MemoryEnrichmentProviderLeases.AsNoTracking().SingleAsync()).LeaseToken);
            await restarted.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(set => set.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow));
        }
        Assert.Equal(0, factory.Calls);
        // The previous holder has finished. Acquisition reconciles that durable state,
        // even if its reservation cleanup never arrived.
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, MemoryCaptureStatus.Completed)
            .SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null));
        await using var recovery = fixture.Context();
        Assert.Equal(1, await fixture.Service(recovery, factory).ProcessPendingAsync());
        Assert.Equal(1, factory.Calls);
        var completed = await recovery.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Completed, completed.Status); Assert.Equal(10, completed.Attempts);
        Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.Empty(await recovery.MemoryEnrichmentProviderLeases.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericWorkerCancellationPreservesLastAttemptAndRecoversWithCurrentAuthority(bool revoke)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        await AcceptGenericAsync(fixture, db, episode);
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(set => set.SetProperty(x => x.Attempts, 9));
        using var stopping = new CancellationTokenSource();
        var interrupted = new ScriptedProviderFactory((_, token) => { stopping.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service(db, interrupted).ProcessPendingAsync(cancellationToken: stopping.Token));
        var deferred = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Pending, deferred.Status); Assert.Equal(9, deferred.Attempts);
        Assert.Equal("memory_enrichment_interrupted", deferred.LastError);
        Assert.Null(deferred.LeaseToken); Assert.Null(deferred.LeaseExpiresAt); Assert.Null(deferred.AcceptedExtractionJson);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.Single(await db.MemoryEpisodeExtractionReceipts.ToListAsync());
        Assert.Empty(await db.MemoryEnrichmentProviderLeases.ToListAsync());
        if (revoke)
            await db.AgentInstallationGrants.ExecuteUpdateAsync(set => set.SetProperty(x => x.RequiredCapabilitiesJson, "[]"));
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(set => set.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow));
        await using var restarted = fixture.Context();
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(revoke ? 0 : 1, await fixture.Service(restarted, factory).ProcessPendingAsync());
        Assert.Equal(1, interrupted.Calls); Assert.Equal(revoke ? 0 : 1, factory.Calls);
        var completed = await restarted.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(revoke ? MemoryCaptureStatus.Failed : MemoryCaptureStatus.Completed, completed.Status);
        Assert.Equal(10, completed.Attempts);
        Assert.Equal(revoke ? 1 : 2, await restarted.MemoryEpisodeExtractionReceipts.CountAsync());
        if (revoke)
        {
            Assert.Equal("memory_enrichment_authority_revoked", completed.LastError);
            Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        }
        else Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
    }

    [MemoryPostgresFact]
    public async Task GenericActualFailureStillExhaustsLastAttemptWithoutAutomaticReplay()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        await AcceptGenericAsync(fixture, db, episode);
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(set => set.SetProperty(x => x.Attempts, 9));
        var factory = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("provider unavailable"));
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        var failed = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Failed, failed.Status); Assert.Equal(10, failed.Attempts);
        Assert.Equal("memory_enrichment_failed", failed.LastError); Assert.Null(failed.AcceptedExtractionJson);
        await using var restarted = fixture.Context();
        Assert.Equal(0, await fixture.Service(restarted, factory).ProcessPendingAsync());
        Assert.Equal(1, factory.Calls);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.Empty(await restarted.MemoryEnrichmentProviderLeases.ToListAsync());
    }
}
