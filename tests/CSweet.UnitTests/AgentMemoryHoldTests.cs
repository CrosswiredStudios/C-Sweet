using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task HoldReviewPreservesSuppressionFingerprintAndHistoryAndRechecksReplayAuthority()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var episode = await SeedLegacyEpisode(fixture, classified: true, change: x => MemorySourceIntegrity.Seal(x with { IsSuppressed = true }));
        await using var db = fixture.Context();
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        Assert.True(preview.LegalHold); Assert.True(preview.CanRelease);
        var request = new ReviewMemoryHoldRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false);
        var result = await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request);
        Assert.False(result.LegalHold);
        Assert.True((await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request)).WasReplay);
        var current = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.False(current.LegalHold); Assert.True(current.IsSuppressed);
        Assert.Equal(episode.SourceFingerprint, current.SourceFingerprint); Assert.True(MemorySourceIntegrity.IsVerified(current));
        Assert.Equal("preserve me", await db.Database.SqlQuery<string>($"SELECT payload->>'legacyExtension' AS \"Value\" FROM csweet_memory_episodes WHERE id={episode.Id}").SingleAsync());
        var history = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", episode.Id, actor);
        Assert.Equal("release-hold", Assert.Single(history.Items[^1].Reviews).Action);
        Assert.Equal(result.ReceiptId, Assert.Single(history.Items[^1].Reviews).ReceiptId);
        Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request with { LegalHold = true }));
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request));
    }

    [MemoryPostgresFact]
    public async Task HoldPlacementBlocksDeletionUntilReviewedReleaseWithoutClassifyingLegacyEvidence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var episode = await SeedLegacyEpisode(fixture, change: x => x with { LegalHold = false });
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.DeleteScopeAsync(fixture.Partition));
        var held = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.True(held.LegalHold); Assert.Null(held.SourceFingerprint);
        preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false));
        await fixture.Store.DeleteScopeAsync(fixture.Partition);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.Equal(2, await db.MemoryReviewReceipts.CountAsync());
    }

    [MemoryPostgresFact]
    public async Task HoldReviewRejectsIntermediateManagerAndForeignAudience()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        var root = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, EmployeeType = EmployeeType.Human,
            IsActive = true, DisplayName = "Root" };
        db.CoreOrganizationUsers.Add(root); await db.SaveChangesAsync();
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReportsToOrganizationUserId, root.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetHoldAsync(Guid.NewGuid(), fixture.EmployeeId, episode.Id, actor));
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).LegalHold);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HoldReleaseRejectsTransferredObligationsAndDoesNotReleaseOtherCaptures(bool legacy)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        var copied = episode with { Id = Guid.NewGuid(), IdempotencyKey = "copy", Source = new("knowledge-transfer", Guid.NewGuid().ToString()),
            TransferEvidence = legacy ? null : new(Guid.NewGuid(), "unresolved", [], true) };
        await fixture.Store.AppendEpisodeAsync(copied);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, copied.Id, actor);
        Assert.False(preview.CanRelease);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, copied.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false)));
        preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false));
        var captures = (await fixture.Store.ExportAsync(fixture.Partition)).Episodes;
        Assert.False(captures.Single(x => x.Id == episode.Id).LegalHold); Assert.True(captures.Single(x => x.Id == copied.Id).LegalHold);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.DeleteScopeAsync(fixture.Partition));
    }

    [MemoryPostgresFact]
    public async Task HoldReviewRejectsStaleEvidenceAndRollsBackOnAuditFailure()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        var request = new ReviewMemoryHoldRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, false);
        await ((IMemorySuppressionStore)fixture.Store).SuppressEpisodeAsync(fixture.Partition, episode.Id);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request));
        preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        request = request with { ExpectedRevision = preview.Revision, EvidenceToken = preview.EvidenceToken };
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_hold_review() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER fail_hold_review BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryEpisode') EXECUTE FUNCTION fail_hold_review();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request));
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).LegalHold);
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        Assert.Equal(preview.Revision, (await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor)).Revision);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_hold_review ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_hold_review();");
        async Task<bool> Attempt()
        {
            await using var context = fixture.Context();
            try { await new AgentMemoryReviewService(context, fixture.Store, TimeProvider.System).ReviewHoldAsync(fixture.OrganizationId,
                fixture.EmployeeId, episode.Id, actor, request with { OperationId = Guid.NewGuid() }); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(), Attempt()), x => x);
        Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task HoldReviewCannotReadOrChangeAnotherHumansPrivateAudience()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var otherAudience = EmployeeMemoryNamespaces.UserRelationship(fixture.OrganizationId.ToString("D"),
            fixture.EmployeeId.ToString("D"), Guid.NewGuid().ToString("D"), "csweet").Partition;
        var episode = await SeedLegacyEpisode(fixture, change: x => x with { Partition = otherAudience });
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), 1, new string('a', 64), false)));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task HoldPlacementSerializesWithScopeDeletionAndKeepsAuthorityLockedThroughCommit()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        var episode = await SeedLegacyEpisode(fixture, change: x => x with { LegalHold = false });
        var gate = new ReviewCommitGate(); await using var db = fixture.Context(gate);
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        var pending = service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, true));
        Task? deletion = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var concurrent = fixture.Context(); await concurrent.Database.OpenConnectionAsync();
            await concurrent.Database.ExecuteSqlRawAsync("SET lock_timeout='200ms'");
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => concurrent.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"CoreOrganizationUsers\" SET \"IsActive\"=false WHERE \"Id\"={fixture.HumanId}"));
            deletion = fixture.Store.DeleteScopeAsync(fixture.Partition);
            // Deterministically observe the deletion barrier waiting on this transaction.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!await concurrent.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_locks WHERE relation='csweet_memory_episodes'::regclass AND mode='ShareRowExclusiveLock' AND NOT granted) AS \"Value\"").SingleAsync(deadline.Token))
            {
                Assert.False(deletion.IsCompleted); await Task.Delay(25, deadline.Token);
            }
        }
        finally { gate.Release.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(deletion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => deletion.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).LegalHold);
    }
}
