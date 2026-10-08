using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(Guid User, MemoryEpisode Episode)> SeedJoblessProposalAsync(DurabilityFixture fixture,
        Func<MemoryEpisode, MemoryEpisode>? change = null, bool verified = true)
    {
        var (user, _) = await SeedRecoveryAsync(fixture);
        var episode = await PrepareGenericProposalAsync(fixture);
        if (change is not null) episode = change(episode);
        if (verified) await fixture.Store.AppendEpisodeAsync(episode);
        else
        {
            await using var db = fixture.Context();
            var payload = JsonSerializer.Serialize(episode, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO csweet_memory_episodes(id,partition_key,idempotency_key,content,occurred_at,expires_at,payload)
                VALUES ({episode.Id},{episode.Partition.StorageKey},{episode.IdempotencyKey},{episode.Content},
                    {episode.OccurredAt},{episode.ExpiresAt},CAST({payload} AS jsonb))
                """);
        }
        return (user, episode);
    }
    private static AgentMemoryIngestionRecoveryService IngestionRecovery(DurabilityFixture fixture, CSweetDbContext db) =>
        new(db, fixture.Store, fixture.Service(db, new UsageProviderFactory()), TimeProvider.System);

    [MemoryPostgresFact]
    public async Task IngestionRecoveryReviewsExistingInputAndQueuesAtomicallyWithoutChangingIt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await SeedJoblessProposalAsync(fixture);
        var original = JsonSerializer.Serialize(await RetainedEpisodeAsync(fixture, episode.Id));
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        var candidates = await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user);
        Assert.Equal(episode.Id, Assert.Single(candidates.Items).EpisodeId);
        Assert.DoesNotContain("Alice", JsonSerializer.Serialize(candidates));
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.True(preview.CanQueue); Assert.Equal("Personal", preview.Sensitivity);
        var request = new RecoverMemoryIngestionRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken);
        var first = await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        Assert.False(first.Replayed); Assert.Equal("Pending", first.Status);
        Assert.True((await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request)).Replayed);
        Assert.Empty((await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user)).Items);
        Assert.False((await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user)).CanQueue);
        Assert.Equal(original, JsonSerializer.Serialize(await RetainedEpisodeAsync(fixture, episode.Id)));
        var receipt = await db.MemoryReviewReceipts.SingleAsync(x => x.RecordKind == "EpisodeIngestion");
        Assert.Equal(preview.Revision, receipt.ResultRevision); Assert.Equal("queue-enrichment", receipt.Action);
        Assert.DoesNotContain("Alice", JsonSerializer.Serialize(receipt));
        Assert.Single(await db.AuditOutbox.Where(x => x.SourceEntityType == "MemoryEpisode").ToListAsync());
        var provider = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(1, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(1, provider.Calls);
        var replay = await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        Assert.Equal("Completed", replay.Status); Assert.True(replay.Replayed);
        Assert.Single(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Equal(MemoryConfirmationState.Pending, Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims).Confirmation);
    }

    [MemoryPostgresFact]
    public async Task IngestionRecoveryDiscoveryIsBoundedPagedAndActorBound()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, first) = await SeedJoblessProposalAsync(fixture);
        var secondId = Guid.NewGuid();
        await fixture.Store.AppendEpisodeAsync(first with { Id = secondId, Source = first.Source with { Id = secondId.ToString("D") }, IdempotencyKey = secondId.ToString("D") });
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        var page = await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user, limit: 1);
        Assert.Single(page.Items); Assert.NotNull(page.NextCursor);
        var last = await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user, page.NextCursor, 1);
        Assert.Single(last.Items); Assert.Null(last.NextCursor); Assert.NotEqual(page.Items[0].EpisodeId, last.Items[0].EpisodeId);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user, new string('a', 513)));
    }

    [MemoryPostgresTheory]
    [InlineData("grant")]
    [InlineData("installation")]
    [InlineData("relationship")]
    [InlineData("suppressed")]
    [InlineData("revision")]
    [InlineData("manager")]
    public async Task IngestionRecoveryRechecksSourceAndAuthorityAfterPreview(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await SeedJoblessProposalAsync(fixture);
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var request = new RecoverMemoryIngestionRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken);
        switch (change)
        {
            case "grant": await db.AgentInstallationGrants.ExecuteUpdateAsync(x => x.SetProperty(j => j.RequiredCapabilitiesJson, "[]")); break;
            case "installation": await db.AgentInstallations.ExecuteUpdateAsync(x => x.SetProperty(j => j.IsEnabled, false)); break;
            case "relationship": await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(j => j.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "suppressed": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{isSuppressed}}','true') WHERE id={episode.Id}"); break;
            case "revision":
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','true') WHERE id={episode.Id}");
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','false') WHERE id={episode.Id}"); break;
            case "manager": await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.ReportsToOrganizationUserId, (Guid?)null)); break;
        }
        var error = await Record.ExceptionAsync(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request));
        Assert.True(error is UnauthorizedAccessException or InvalidOperationException or DbUpdateConcurrencyException, error?.ToString());
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Empty(await db.MemoryReviewReceipts.Where(x => x.RecordKind == "EpisodeIngestion").ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("fingerprint")]
    [InlineData("installation-metadata")]
    [InlineData("author")]
    [InlineData("expired")]
    [InlineData("public")]
    public async Task IngestionRecoveryDoesNotInventMissingOrAmbiguousEvidence(string defect)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await SeedJoblessProposalAsync(fixture, x => defect switch
        {
            "installation-metadata" => x with { Metadata = new Dictionary<string,string> { ["installationId"] = Guid.NewGuid().ToString("D") } },
            "author" => x with { Source = x.Source with { Author = Guid.NewGuid().ToString("D") } },
            "expired" => x with { OccurredAt = DateTimeOffset.UtcNow.AddDays(-2), ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) },
            "public" => x with { Sensitivity = MemorySensitivity.Public },
            _ => x
        }, verified: defect != "fingerprint");
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.False(preview.CanQueue); Assert.NotNull(preview.BlockedReason);
        await Assert.ThrowsAnyAsync<Exception>(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken)));
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task IngestionRecoveryCannotExposeAnotherHumanPrivateAudience()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var otherHuman = Guid.NewGuid();
        var (user, episode) = await SeedJoblessProposalAsync(fixture, x => x with {
            Partition = EmployeeMemoryNamespaces.UserRelationship(fixture.OrganizationId.ToString("D"),
                fixture.EmployeeId.ToString("D"), otherHuman.ToString("D"), "csweet").Partition });
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        Assert.Empty((await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user)).Items);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
    }

    [MemoryPostgresFact]
    public async Task IngestionRecoveryAuditFailureRollsBackJobAndReceipt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await SeedJoblessProposalAsync(fixture);
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_ingestion_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER fail_ingestion_audit BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW EXECUTE FUNCTION fail_ingestion_audit();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken)));
        Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        Assert.Empty(await db.MemoryReviewReceipts.Where(x => x.RecordKind == "EpisodeIngestion").ToListAsync());
        Assert.NotNull(await RetainedEpisodeAsync(fixture, episode.Id));
    }

    [MemoryPostgresFact]
    public async Task IngestionRecoveryConcurrentRequestsHaveOneWinnerAndNeverResurrectADeletedJob()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await SeedJoblessProposalAsync(fixture);
        await using var first = fixture.Context(); await using var second = fixture.Context();
        var service = IngestionRecovery(fixture, first);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        async Task<Exception?> Attempt(CSweetDbContext context) => await Record.ExceptionAsync(() => IngestionRecovery(fixture, context)
            .RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken)));
        var attempts = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.Single(attempts, x => x is null); Assert.Single(attempts, x => x is InvalidOperationException);
        Assert.Single(await first.MemoryEpisodeEnrichmentJobs.ToListAsync());
        var receipt = await first.MemoryReviewReceipts.AsNoTracking().SingleAsync(x => x.RecordKind == "EpisodeIngestion");
        await first.MemoryEpisodeEnrichmentJobs.ExecuteDeleteAsync();
        Assert.Empty((await service.ListAsync(fixture.OrganizationId, fixture.EmployeeId, user)).Items);
        Assert.False((await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user)).CanQueue);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id,
            user, new(receipt.OperationId, preview.Revision, preview.EvidenceToken)));
        Assert.Empty(await first.MemoryEpisodeEnrichmentJobs.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("verified")]
    [InlineData("receipt-missing")]
    [InlineData("source-owner-revoked")]
    [InlineData("job-present-origin-revoked")]
    public async Task IngestionRecoveryTransferUsesOriginalAuthenticatedApplyEvidence(string scenario)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Completed)
            .SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow));
        var transfer = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await transfer.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, user, TransferRequest(target, claim));
        var preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user);
        await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user);
        var applied = await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user, new(Guid.NewGuid(), preview.ReviewToken, "apply"));
        var id = applied.AppliedEpisodeId!.Value;
        var original = JsonSerializer.Serialize(await RetainedEpisodeAsync(fixture, id));
        if (scenario != "job-present-origin-revoked") await db.MemoryEpisodeEnrichmentJobs.ExecuteDeleteAsync(); // Simulate the pre-outbox acceptance gap.
        if (scenario == "receipt-missing") await db.MemoryTransferReceipts.Where(x => x.Action == "apply").ExecuteDeleteAsync();
        if (scenario.EndsWith("revoked", StringComparison.Ordinal)) await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId)
            .ExecuteUpdateAsync(x => x.SetProperty(j => j.ReportsToOrganizationUserId, (Guid?)null));
        var service = IngestionRecovery(fixture, db);
        var recovery = await service.PreviewAsync(fixture.OrganizationId, target, id, user);
        Assert.Equal(scenario == "verified", recovery.CanQueue);
        if (recovery.CanQueue)
        {
            await service.RecoverAsync(fixture.OrganizationId, target, id, user, new(Guid.NewGuid(), recovery.Revision, recovery.EvidenceToken));
            Assert.Equal(user, (await db.MemoryEpisodeEnrichmentJobs.SingleAsync()).ReviewerApplicationUserId);
            Assert.Equal(original, JsonSerializer.Serialize(await RetainedEpisodeAsync(fixture, id)));
        }
        else
        {
            Assert.DoesNotContain("Alice", recovery.Content);
            Assert.Equal("Transferred evidence is unavailable until its retained source lineage can be verified.", recovery.Content);
            if (scenario != "job-present-origin-revoked") Assert.Empty(await db.MemoryEpisodeEnrichmentJobs.ToListAsync());
        }
    }

    [MemoryPostgresFact]
    public async Task IngestionRecoveryReplayCannotRebindTheReviewedJobToANewerSourceRevision()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await SeedJoblessProposalAsync(fixture);
        await using var db = fixture.Context();
        var service = IngestionRecovery(fixture, db);
        var preview = await service.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var request = new RecoverMemoryIngestionRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken);
        await service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        var originalJob = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['sensitivity'],to_jsonb({(int)MemorySensitivity.Restricted})) WHERE id={episode.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['sensitivity'],to_jsonb({(int)MemorySensitivity.Personal})) WHERE id={episode.Id}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request));
        var current = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(originalJob.SourceJson, current.SourceJson); Assert.Equal(originalJob.Id, current.Id);
        Assert.Single(await db.MemoryReviewReceipts.Where(x => x.RecordKind == "EpisodeIngestion").ToListAsync());
    }
}
