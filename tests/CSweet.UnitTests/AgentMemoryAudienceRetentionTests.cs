using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    [InlineData("InstallationEmployee")]
    [InlineData("InstallationRelationship")]
    public async Task AudienceRetentionHoldsPreserveProcessingAndSuppressionThenAllowExplicitReleaseAndForgetting(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context(); var recovery = IngestionRecovery(fixture, db);
        var review = await recovery.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await recovery.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), review.Revision, review.EvidenceToken));
        db.ChangeTracker.Clear(); var sourceJson = await db.MemoryEpisodeEnrichmentJobs.Select(x => x.SourceJson).SingleAsync();
        var service = ErasureService(fixture, db);
        var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var place = new ReviewMemoryHoldRequest(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true);
        var placed = await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, place);
        Assert.True(placed.LegalHold);
        Assert.Equal(1, await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).ProcessPendingAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(sourceJson, await db.MemoryEpisodeEnrichmentJobs.Select(x => x.SourceJson).SingleAsync());
        Assert.Equal(MemoryConfirmationState.Pending, Assert.Single((await fixture.Store.ExportAsync(episode.Partition)).Claims).Confirmation);
        var forgetting = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.Equal("memory_legal_hold_prevents_deletion", forgetting.ApplyBlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.DeleteScopeAsync(episode.Partition));
        var suppress = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.True(suppress.LegalHold);
        var suppressRequest = new SuppressMemorySourceRequest(Guid.NewGuid(), suppress.Revision, suppress.EvidenceToken);
        await service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, suppressRequest);
        var suppressed = await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition, episode.Id);
        Assert.True(suppressed!.IsSuppressed); Assert.True(suppressed.LegalHold); Assert.True(MemorySourceIntegrity.IsVerified(suppressed));
        Assert.True((await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, place)).WasReplay);
        hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.True(hold.CanRelease);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, false));
        Assert.True((await service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, suppressRequest)).WasReplay);
        Assert.Equal(3, await db.MemoryReviewReceipts.CountAsync(x => x.Action == "place-hold" || x.Action == "release-hold" || x.Action == "suppress"));
        forgetting = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        Assert.Null(forgetting.ApplyBlockedReason);
        await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), Assert.IsType<string>(forgetting.EvidenceToken)));
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition, episode.Id));
    }

    [MemoryPostgresTheory]
    [InlineData("Team", "actor-ended")]
    [InlineData("Team", "actor-future")]
    [InlineData("Team", "employee-ended")]
    [InlineData("Team", "archive")]
    [InlineData("Role", "actor-role")]
    [InlineData("Role", "employee-role")]
    [InlineData("Role", "foreign-role")]
    [InlineData("InstallationEmployee", "installation")]
    [InlineData("InstallationRelationship", "installation")]
    public async Task AudienceRetentionRechecksMembershipForPreviewApplyAndReplay(string kind, string revoke)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var suppression = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var holdRequest = new ReviewMemoryHoldRequest(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true);
        var suppressRequest = new SuppressMemorySourceRequest(Guid.NewGuid(), suppression.Revision, suppression.EvidenceToken);
        await RevokeOperatorAudienceAsync(fixture, db, audience, revoke);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, holdRequest));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, suppressRequest));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        await using var second = await DurabilityFixture.CreateAsync(postgres: true);
        var (secondUser, secondEpisode, secondAudience) = await SeedOperatorAudienceAsync(second, kind);
        await using var secondDb = second.Context(); var secondService = ErasureService(second, secondDb);
        var secondHold = await secondService.GetHoldAsync(second.OrganizationId, second.EmployeeId, secondEpisode.Id, secondUser);
        var secondHoldRequest = new ReviewMemoryHoldRequest(Guid.NewGuid(), secondHold.Revision, secondHold.EvidenceToken, true);
        await secondService.ReviewHoldAsync(second.OrganizationId, second.EmployeeId, secondEpisode.Id, secondUser, secondHoldRequest);
        var secondSuppression = await secondService.GetSuppressionAsync(second.OrganizationId, second.EmployeeId, secondEpisode.Id, secondUser);
        var secondSuppressRequest = new SuppressMemorySourceRequest(Guid.NewGuid(), secondSuppression.Revision, secondSuppression.EvidenceToken);
        await secondService.SuppressSourceAsync(second.OrganizationId, second.EmployeeId, secondEpisode.Id, secondUser, secondSuppressRequest);
        await RevokeOperatorAudienceAsync(second, secondDb, secondAudience, revoke);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => secondService.ReviewHoldAsync(second.OrganizationId, second.EmployeeId, secondEpisode.Id, secondUser, secondHoldRequest));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => secondService.SuppressSourceAsync(second.OrganizationId, second.EmployeeId, secondEpisode.Id, secondUser, secondSuppressRequest));
        Assert.Equal(2, await secondDb.MemoryReviewReceipts.CountAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team", true)]
    [InlineData("Team", false)]
    [InlineData("Role", true)]
    [InlineData("Role", false)]
    [InlineData("InstallationEmployee", true)]
    [InlineData("InstallationEmployee", false)]
    public async Task AudienceRetentionBindsChangedAndRestoredAuthorityWithoutChangingSourceRevision(string kind, bool hold)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, audience) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var holdPreview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var suppressPreview = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        if (kind == "Team")
        {
            await db.OrganizationTeams.Where(x => x.Id == audience).ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, "Changed"));
            await db.OrganizationTeams.Where(x => x.Id == audience).ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, "Design"));
        }
        else if (kind == "Role")
        {
            await db.CoreRoles.Where(x => x.Id == audience).ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, "Changed"));
            await db.CoreRoles.Where(x => x.Id == audience).ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, "Designer"));
        }
        else
        {
            await db.AgentInstallations.Where(x => x.Id == fixture.InstallationId).ExecuteUpdateAsync(x => x.SetProperty(t => t.IsEnabled, false));
            await db.AgentInstallations.Where(x => x.Id == fixture.InstallationId).ExecuteUpdateAsync(x => x.SetProperty(t => t.IsEnabled, true));
        }
        Assert.Equal(holdPreview.Revision, (await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user)).Revision);
        if (hold) await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), holdPreview.Revision, holdPreview.EvidenceToken, true)));
        else await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), suppressPreview.Revision, suppressPreview.EvidenceToken)));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team")]
    [InlineData("Role")]
    [InlineData("InstallationEmployee")]
    [InlineData("InstallationRelationship")]
    public async Task AudienceRetentionAuditFailureRollsBackHoldSuppressionAndHistory(string kind)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, kind);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var suppression = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_audience_retention() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER fail_audience_retention BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryEpisode') EXECUTE FUNCTION fail_audience_retention();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true)));
        await Assert.ThrowsAsync<DbUpdateException>(() => service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), suppression.Revision, suppression.EvidenceToken)));
        var current = await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition, episode.Id);
        Assert.False(current!.LegalHold); Assert.False(current.IsSuppressed);
        Assert.Equal(hold.Revision, (await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user)).Revision);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM csweet_memory_suppressions").SingleAsync());
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData("Team", "producer")]
    [InlineData("Team", "installation")]
    [InlineData("Team", "source")]
    [InlineData("Team", "classification")]
    [InlineData("InstallationEmployee", "producer")]
    [InlineData("InstallationEmployee", "installation")]
    [InlineData("InstallationEmployee", "source")]
    [InlineData("InstallationEmployee", "classification")]
    public async Task AudienceRetentionCannotInventProposalOwnershipOrBlessUnknownSource(string kind, string defect)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, kind);
        var id = Guid.NewGuid();
        var invalid = episode with { Id = id, Source = episode.Source with { Id = id.ToString("D") }, IdempotencyKey = "invalid:" + id.ToString("D") };
        invalid = defect switch
        {
            "producer" => invalid with { Source = invalid.Source with { Author = Guid.NewGuid().ToString("D") } },
            "installation" => invalid with { Metadata = new Dictionary<string, string> { ["installationId"] = Guid.NewGuid().ToString("D") } },
            "source" => invalid with { Source = invalid.Source with { Type = "unknown-source" } },
            _ => invalid with { Sensitivity = MemorySensitivity.Public }
        };
        await fixture.Store.AppendEpisodeAsync(invalid);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var holdError = await Record.ExceptionAsync(() => service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, id, user));
        var suppressError = await Record.ExceptionAsync(() => service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, id, user));
        Assert.True(holdError is UnauthorizedAccessException or InvalidOperationException, holdError?.ToString());
        Assert.True(suppressError is UnauthorizedAccessException or InvalidOperationException, suppressError?.ToString());
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AudienceRetentionPreservesNativeLegacyEvidenceTokens(bool hold)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, _) = await SeedRecoveryAsync(fixture);
        var episode = await SeedLegacyEpisode(fixture);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var payload = await db.Database.SqlQuery<string>($"SELECT payload::text AS \"Value\" FROM csweet_memory_episodes WHERE id={episode.Id}").SingleAsync();
        var preview = hold ? (await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user)).EvidenceToken :
            (await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user)).EvidenceToken;
        var revision = await db.Database.SqlQuery<long>($"SELECT MAX(revision) AS \"Value\" FROM csweet_memory_revisions WHERE record_id={episode.Id} AND kind=0").SingleAsync();
        var expected = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { revision, Payload = payload },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ToLowerInvariant();
        Assert.Equal(expected, preview);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudienceRetentionFencesSuppressionButAcceptsHoldOnlyChangesDuringTheProviderCall(bool suppress)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, "Team");
        await using (var seed = fixture.Context())
        {
            var recovery = IngestionRecovery(fixture, seed); var preview = await recovery.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
            await recovery.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken));
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProviderFactory(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        await using var worker = fixture.Context(); var processing = fixture.Service(worker, provider).ProcessPendingAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); await using var db = fixture.Context(); var service = ErasureService(fixture, db);
            if (suppress)
            {
                var preview = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
                await service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken));
            }
            else
            {
                var preview = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
                await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, true));
            }
        }
        finally { release.TrySetResult(); }
        Assert.Equal(suppress ? 0 : 1, await processing.WaitAsync(TimeSpan.FromSeconds(30)));
        await using var check = fixture.Context(); var job = await check.MemoryEpisodeEnrichmentJobs.SingleAsync();
        Assert.Equal(suppress ? MemoryCaptureStatus.Failed : MemoryCaptureStatus.Completed, job.Status);
        if (suppress) { Assert.Null(job.AcceptedExtractionJson); Assert.Empty((await fixture.Store.ExportAsync(episode.Partition)).Claims); }
        else { Assert.NotNull(job.AcceptedExtractionJson); Assert.Single((await fixture.Store.ExportAsync(episode.Partition)).Claims); }
    }

    [MemoryPostgresTheory]
    [InlineData(64)]
    [InlineData(65)]
    public async Task AudienceRetentionBoundsHoldOnlyRevisionHistoryWithoutWeakeningSemanticFences(int changes)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await QueueEpisodeErasureAsync(fixture);
        await using var db = fixture.Context();
        for (var index = 0; index < changes; index++)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],to_jsonb({index % 2 == 0})) WHERE id={episode.Id}");
        var provider = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(changes == 64 ? 1 : 0, await fixture.Service(db, provider).ProcessPendingAsync());
        Assert.Equal(changes == 64 ? 1 : 0, provider.Calls);
        Assert.Equal(changes == 64 ? MemoryCaptureStatus.Completed : MemoryCaptureStatus.Failed,
            (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).Status);
    }

    [MemoryPostgresFact]
    public async Task AudienceRetentionIngestionRecoveryReplayPreservesOriginalInputAfterHoldChanges()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode) = await SeedJoblessProposalAsync(fixture);
        await using var db = fixture.Context(); var recovery = IngestionRecovery(fixture, db);
        var preview = await recovery.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        var request = new RecoverMemoryIngestionRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken);
        await recovery.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        var original = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        var service = ErasureService(fixture, db);
        foreach (var held in new[] { true, false })
        {
            var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
            await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
                new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, held));
        }
        var replay = await recovery.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user, request);
        Assert.True(replay.Replayed); Assert.Equal(original.Id, replay.JobId);
        var current = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, current.Id); Assert.Equal(original.SourceJson, current.SourceJson);
        Assert.Equal(original.SourceHash, current.SourceHash);
        Assert.Single(await db.MemoryReviewReceipts.Where(x => x.RecordKind == "EpisodeIngestion").ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task AudienceRetentionRejectsChangedAndRestoredUnknownSourceFields()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (_, episode) = await QueueEpisodeErasureAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['source','operatorExtension'],'true'::jsonb) WHERE id={episode.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=payload #- ARRAY['source','operatorExtension'] WHERE id={episode.Id}");
        var provider = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("Do not dispatch changed source history"));
        Assert.Equal(0, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(0, provider.Calls);
        Assert.Null((await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).AcceptedExtractionJson);
    }

    [MemoryPostgresFact]
    public async Task AudienceRetentionReplaysAcceptedOutputAfterAReviewedHoldWithoutAnotherProviderCall()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context(); var recovery = EpisodeRecovery(fixture, db);
        await recovery.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 0));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_retention_claim() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER fail_retention_claim BEFORE INSERT ON csweet_memory_claims FOR EACH ROW EXECUTE FUNCTION fail_retention_claim();
            """);
        Assert.Equal(0, await fixture.Service(db, new ScriptedProviderFactory((_, _) => Task.CompletedTask)).ProcessPendingAsync());
        var accepted = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.NotNull(accepted.AcceptedExtractionJson);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_retention_claim ON csweet_memory_claims; DROP FUNCTION fail_retention_claim();");
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed).SetProperty(j => j.Attempts, 10));
        db.ChangeTracker.Clear(); var service = ErasureService(fixture, db);
        var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, accepted.EpisodeId, user);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, accepted.EpisodeId, user,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true));
        await recovery.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 1));
        var provider = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("Do not replace accepted output"));
        Assert.Equal(1, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(0, provider.Calls);
        var completed = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(accepted.AcceptedExtractionJson, completed.AcceptedExtractionJson);
        Assert.Equal(accepted.SourceJson, completed.SourceJson); Assert.Equal(accepted.SourceHash, completed.SourceHash);
        Assert.Equal(accepted.ExtractionAcceptedAt, completed.ExtractionAcceptedAt);
        Assert.True((await db.MemoryEpisodeRetryReceipts.SingleAsync(x => x.RetryGeneration == 2)).ReusesAcceptedExtraction);
    }

    [MemoryPostgresFact]
    public async Task AudienceRetentionPreservesPartialReconciliationAcrossAReviewedHold()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, claim) = await SeedPartialLegacyAsync(fixture);
        await using var db = fixture.Context(); var recovery = IngestionRecovery(fixture, db);
        var preview = await recovery.PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await recovery.RecoverAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, preview.RequiredReconciliationPolicy));
        db.ChangeTracker.Clear();
        var before = JsonSerializer.Serialize((await fixture.Store.ExportAsync(episode.Partition)).Claims.Single(x => x.Id == claim));
        var service = ErasureService(fixture, db); var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true));
        var provider = new ReconciliationProvider();
        var processed = await fixture.Service(db, provider).ProcessPendingAsync();
        Assert.True(processed == 1, $"Processed={processed}; calls={provider.Calls}; error={(await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).LastError}");
        Assert.Equal(before, JsonSerializer.Serialize((await fixture.Store.ExportAsync(episode.Partition)).Claims.Single(x => x.Id == claim)));
        Assert.True((await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition, episode.Id))!.LegalHold);
    }

    [MemoryPostgresFact]
    public async Task AudienceRetentionRejectsAnotherHumansInstallationPrivateRelationship()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, "InstallationRelationship");
        var id = Guid.NewGuid();
        var foreign = episode with { Id = id, Source = episode.Source with { Id = id.ToString("D") }, IdempotencyKey = "foreign:" + id.ToString("D"),
            Partition = EmployeeMemoryNamespaces.UserRelationship(fixture.OrganizationId.ToString("D"), fixture.EmployeeId.ToString("D"),
                Guid.NewGuid().ToString("D"), fixture.InstallationId.ToString("D")).Partition };
        await fixture.Store.AppendEpisodeAsync(foreign);
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, id, user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, id, user));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task AudienceRetentionAllowsHistoricalPrivateHoldAndSuppressionWithoutRestoringRecovery()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, "InstallationRelationship");
        await using var db = fixture.Context();
        await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(t => t.ArchivedAt, DateTimeOffset.UtcNow));
        var service = ErasureService(fixture, db); var hold = await service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await service.ReviewHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), hold.Revision, hold.EvidenceToken, true));
        var suppress = await service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user);
        await service.SuppressSourceAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user,
            new(Guid.NewGuid(), suppress.Revision, suppress.EvidenceToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => IngestionRecovery(fixture, db).PreviewAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        var current = await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(episode.Partition, episode.Id);
        Assert.True(current!.LegalHold); Assert.True(current.IsSuppressed);
    }

    [MemoryPostgresFact]
    public async Task AudienceRetentionBoundsAuthorityBytesBeforeDisclosingProposalContent()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, episode, _) = await SeedOperatorAudienceAsync(fixture, "Role");
        await using var db = fixture.Context();
        // A single oversized relevant authority row must fail before returning content.
        var setup = JsonSerializer.Serialize(new { padding = new string('x', 1_048_576) });
        await db.AgentInstallations.Where(x => x.Id == fixture.InstallationId).ExecuteUpdateAsync(x => x.SetProperty(t => t.SetupDataJson, setup));
        var service = ErasureService(fixture, db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetHoldAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSuppressionAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, user));
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task AudienceRetentionBoundsTotalRevisionBytesBeforeProviderDispatch()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, _) = await SeedRecoveryAsync(fixture);
        var episode = await PrepareGenericProposalAsync(fixture);
        episode = episode with { Metadata = new Dictionary<string, string>(episode.Metadata!) { ["padding"] = new string('x', 70_000) } };
        await using var db = fixture.Context(); await AcceptGenericAsync(fixture, db, episode);
        for (var index = 0; index < 64; index++)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['legalHold'],to_jsonb({index % 2 == 0})) WHERE id={episode.Id}");
        var provider = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("Do not dispatch oversized retention evidence"));
        Assert.Equal(0, await fixture.Service(db, provider).ProcessPendingAsync()); Assert.Equal(0, provider.Calls);
        Assert.Null((await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).AcceptedExtractionJson);
    }

}
