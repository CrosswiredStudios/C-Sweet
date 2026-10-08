using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<(Guid User, Guid Job)> SeedEpisodeRecoveryAsync(DurabilityFixture fixture)
    {
        var (user, _) = await SeedRecoveryAsync(fixture);
        var episode = await PrepareGenericProposalAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync(EpisodeMemoryEnrichmentRecovery.InstallTriggers);
        await AcceptGenericAsync(fixture, db, episode);
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed)
            .SetProperty(j => j.Attempts, 10).SetProperty(j => j.LastError, "private-provider-detail"));
        return (user, await db.MemoryEpisodeEnrichmentJobs.Select(x => x.Id).SingleAsync());
    }

    private static AgentMemoryRecoveryService EpisodeRecovery(DurabilityFixture fixture, CSweetDbContext db) =>
        new(db, TimeProvider.System, fixture.Service(db, new UsageProviderFactory()));

    [MemoryPostgresFact]
    public async Task GenericRecoveryListsBothJobFamiliesWithoutContentAndPagesOnce()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed));
        var service = EpisodeRecovery(fixture, db);
        var first = await service.ListFailuresAsync(fixture.OrganizationId, fixture.EmployeeId, user, limit: 1);
        var second = await service.ListFailuresAsync(fixture.OrganizationId, fixture.EmployeeId, user, first.NextCursor, 1);
        Assert.NotNull(first.NextCursor); Assert.Null(second.NextCursor);
        var all = first.Items.Concat(second.Items).ToList();
        Assert.Equal(2, all.Select(x => x.Id).Distinct().Count());
        var generic = Assert.Single(all, x => x.Id == jobId);
        Assert.Equal("episode", generic.JobKind); Assert.Equal(Guid.Empty, generic.ConversationId); Assert.NotNull(generic.EpisodeId);
        Assert.Equal("memory_enrichment_failed", generic.FailureCode);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(all));
        Assert.DoesNotContain("Alice", JsonSerializer.Serialize(all));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListFailuresAsync(fixture.OrganizationId,
            fixture.EmployeeId, user, new string('a', 513)));
    }

    [MemoryPostgresFact]
    public async Task GenericRecoveryAuditsOneRetryAndReplayReportsCompletedState()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var request = new RetryMemoryEnrichmentRequest(Guid.NewGuid(), 0);
        var service = EpisodeRecovery(fixture, db);
        var first = await service.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, request);
        Assert.False(first.Replayed); Assert.Equal("Pending", first.Job.Status); Assert.Equal(1, first.Job.RetryGeneration);
        Assert.Equal(0, first.Job.Attempts);
        var receipt = Assert.Single(await db.MemoryEpisodeRetryReceipts.ToListAsync());
        Assert.Equal(10, receipt.PreviousAttempts); Assert.False(receipt.ReusesAcceptedExtraction);
        Assert.Single(await db.AuditOutbox.Where(x => x.SourceEntityType == nameof(MemoryEpisodeEnrichmentJob)).ToListAsync());
        await using var services = new ServiceCollection().AddScoped(_ => fixture.Context()).BuildServiceProvider();
        var writer = new AuditEventWriter(services.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), new EphemeralDataProtectionProvider());
        var dispatcher = new AuditOutboxDispatcher(db, writer, TimeProvider.System);
        await dispatcher.DispatchAsync(default); await dispatcher.DispatchAsync(default);
        var audit = Assert.Single(await db.AuditEvents.Where(x => x.EventType == "memory.enrichment.retry-requested.v1").ToListAsync());
        Assert.Equal(receipt.Id, audit.Id);
        Assert.True((await service.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, request)).Replayed);
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(1, await fixture.Service(db, factory).ProcessPendingAsync());
        var replay = await service.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, request);
        Assert.True(replay.Replayed); Assert.Equal("Completed", replay.Job.Status);
        Assert.Single(await db.MemoryEpisodeRetryReceipts.ToListAsync()); Assert.Equal(1, factory.Calls);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, user, request with { ExpectedRetryGeneration = 1 }));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 0)));
        Assert.DoesNotContain(OrganizationDataPurgeService.ScopedEntityTypes(db.Model), x => x.ClrType == typeof(MemoryEpisodeRetryReceipt));
    }

    [MemoryPostgresTheory]
    [InlineData("source")]
    [InlineData("suppressed")]
    [InlineData("grant")]
    [InlineData("installation")]
    [InlineData("relationship")]
    [InlineData("manager")]
    [InlineData("audience")]
    public async Task GenericRecoveryRejectsChangedInputOrRevokedAuthority(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var job = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        switch (change)
        {
            case "source": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{content}}','\"changed\"') WHERE id={job.EpisodeId}"); break;
            case "suppressed": await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{isSuppressed}}','true') WHERE id={job.EpisodeId}"); break;
            case "grant": await db.AgentInstallationGrants.ExecuteUpdateAsync(x => x.SetProperty(j => j.RequiredCapabilitiesJson, "[]")); break;
            case "installation": await db.AgentInstallations.ExecuteUpdateAsync(x => x.SetProperty(j => j.IsEnabled, false)); break;
            case "relationship": await db.CoreConversations.ExecuteUpdateAsync(x => x.SetProperty(j => j.ArchivedAt, DateTimeOffset.UtcNow)); break;
            case "manager": await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.ReportsToOrganizationUserId, (Guid?)null)); break;
            case "audience":
                var account = new ApplicationUser { Id = Guid.NewGuid(), UserName = "different-manager", CreatedAt = DateTimeOffset.UtcNow };
                var other = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                    EmployeeType = EmployeeType.Human, ApplicationUserId = account.Id };
                db.AddRange(account, other); await db.SaveChangesAsync();
                await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.ReportsToOrganizationUserId, other.Id));
                user = other.ApplicationUserId!.Value; break;
        }
        var exception = await Record.ExceptionAsync(() => EpisodeRecovery(fixture, db).RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 0)));
        Assert.True(exception is UnauthorizedAccessException or InvalidOperationException, exception?.ToString());
        Assert.Empty(await db.MemoryEpisodeRetryReceipts.ToListAsync());
        Assert.Equal(MemoryCaptureStatus.Failed, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).Status);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericRecoveryRollsBackRetryWhenEvidenceCannotPersist(bool audit)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_episode_retry() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            """);
        await db.Database.ExecuteSqlRawAsync(audit
            ? "CREATE TRIGGER fail_episode_retry BEFORE INSERT ON \"ComputeAuditOutbox\" FOR EACH ROW EXECUTE FUNCTION fail_episode_retry();"
            : "CREATE TRIGGER fail_episode_retry BEFORE INSERT ON \"MemoryEpisodeRetryReceipts\" FOR EACH ROW EXECUTE FUNCTION fail_episode_retry();");
        await Assert.ThrowsAsync<DbUpdateException>(() => EpisodeRecovery(fixture, db).RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 0)));
        Assert.Empty(await db.MemoryEpisodeRetryReceipts.ToListAsync());
        Assert.Empty(await db.AuditEvents.Where(x => x.EventType == "memory.enrichment.retry-requested.v1").ToListAsync());
        Assert.Empty(await db.AuditOutbox.Where(x => x.SourceEntityType == nameof(MemoryEpisodeEnrichmentJob)).ToListAsync());
        var current = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(MemoryCaptureStatus.Failed, current.Status); Assert.Equal(0, current.RetryGeneration); Assert.Equal(10, current.Attempts);
    }

    [MemoryPostgresFact]
    public async Task GenericRecoveryConcurrentRetriesHaveOneWinnerAndReceiptsAreImmutable()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var first = fixture.Context(); await using var second = fixture.Context();
        async Task<Exception?> Attempt(CSweetDbContext context) => await Record.ExceptionAsync(() => EpisodeRecovery(fixture, context)
            .RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 0)));
        var results = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.Single(results, x => x is null); Assert.Single(results, x => x is DbUpdateConcurrencyException);
        var receipt = await first.MemoryEpisodeRetryReceipts.AsNoTracking().SingleAsync();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => first.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MemoryEpisodeRetryReceipts\" SET \"PreviousAttempts\"=0 WHERE \"Id\"={receipt.Id}"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => first.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"MemoryEpisodeRetryReceipts\" WHERE \"Id\"={receipt.Id}"));
    }

    [MemoryPostgresFact]
    public async Task GenericRecoveryReusesAcceptedOutputWithoutAnotherProviderCall()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var recovery = EpisodeRecovery(fixture, db);
        await recovery.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 0));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_recovery_claim() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER fail_recovery_claim BEFORE INSERT ON csweet_memory_claims FOR EACH ROW EXECUTE FUNCTION fail_recovery_claim();
            """);
        var provider = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        Assert.Equal(0, await fixture.Service(db, provider).ProcessPendingAsync());
        var accepted = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.NotNull(accepted.AcceptedExtractionJson);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_recovery_claim ON csweet_memory_claims; DROP FUNCTION fail_recovery_claim();");
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed)
            .SetProperty(j => j.Attempts, 10));
        await recovery.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 1));
        var noDispatch = new ScriptedProviderFactory((_, _) => throw new InvalidOperationException("Never re-extract accepted output"));
        Assert.Equal(1, await fixture.Service(db, noDispatch).ProcessPendingAsync()); Assert.Equal(0, noDispatch.Calls);
        var completed = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(accepted.AcceptedExtractionJson, completed.AcceptedExtractionJson);
        Assert.Equal(accepted.ExtractionAcceptedAt, completed.ExtractionAcceptedAt);
        Assert.True((await db.MemoryEpisodeRetryReceipts.SingleAsync(x => x.RetryGeneration == 2)).ReusesAcceptedExtraction);
    }

    [MemoryPostgresFact]
    public async Task GenericRecoveryReplayCannotBypassRevokedGrant()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var request = new RetryMemoryEnrichmentRequest(Guid.NewGuid(), 0);
        var recovery = EpisodeRecovery(fixture, db);
        await recovery.RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, request);
        await db.AgentInstallationGrants.ExecuteUpdateAsync(x => x.SetProperty(j => j.RequiredCapabilitiesJson, "[]"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recovery.RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, user, request));
        Assert.Single(await db.MemoryEpisodeRetryReceipts.ToListAsync());
        Assert.Equal(1, (await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync()).RetryGeneration);
    }

    [MemoryPostgresTheory]
    [InlineData("memory_enrichment_source_invalidated")]
    [InlineData("memory_enrichment_authority_revoked")]
    [InlineData("memory_enrichment_unverifiable_output")]
    [InlineData("memory_enrichment_input_receipt_capacity")]
    [InlineData("memory_transfer_source_unavailable")]
    public async Task GenericRecoveryNeverClearsPermanentFailure(string code)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context();
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.LastError, code));
        await Assert.ThrowsAsync<InvalidOperationException>(() => EpisodeRecovery(fixture, db).RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 0)));
        Assert.Empty(await db.MemoryEpisodeRetryReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task GenericRecoveryMigrationPreservesPopulatedInputAndRefusesEvidenceLoss()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, jobId) = await SeedEpisodeRecoveryAsync(fixture);
        await using var db = fixture.Context();
        var original = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        var migration = new EpisodeMemoryEnrichmentRecovery();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        var after = await db.MemoryEpisodeEnrichmentJobs.AsNoTracking().SingleAsync();
        Assert.Equal(original.SourceJson, after.SourceJson); Assert.Equal(original.SourceHash, after.SourceHash);
        Assert.Equal(0, after.RetryGeneration); Assert.Equal(10, after.Attempts);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemoryEpisodeRetryReceipts" ("Id","JobId","OperationId","OrganizationId","EmployeeId",
                "ActorOrganizationUserId","ActorApplicationUserId","PreviousGeneration","RetryGeneration","PreviousAttempts",
                "ReusesAcceptedExtraction","SourceHash","CreatedAt")
            VALUES ({Guid.NewGuid()},{jobId},{Guid.NewGuid()},{fixture.OrganizationId},{fixture.EmployeeId},
                {fixture.HumanId},{user},0,1,10,false,{after.SourceHash},{DateTimeOffset.UtcNow})
            """));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x
            .SetProperty(j => j.RetryGeneration, 1).SetProperty(j => j.Status, MemoryCaptureStatus.Pending).SetProperty(j => j.Attempts, 0)));
        await EpisodeRecovery(fixture, db).RetryAsync(fixture.OrganizationId, fixture.EmployeeId, jobId, user, new(Guid.NewGuid(), 0));
        var downgrade = generator.Generate(migration.DownOperations).First().CommandText;
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(downgrade));
        Assert.Single(await db.MemoryEpisodeRetryReceipts.ToListAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericRecoveryTransferRequiresCurrentSourceOwnerAuthority(bool revoke)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (user, target, claim) = await SeedTransferAsync(fixture);
        await using var db = fixture.Context();
        await db.MemoryCaptureOutbox.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Completed)
            .SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow));
        await db.Database.ExecuteSqlRawAsync(DurableEpisodeMemoryEnrichment.InstallTriggers);
        await db.Database.ExecuteSqlRawAsync(EpisodeMemoryEnrichmentRecovery.InstallTriggers);
        var transfer = new AgentMemoryTransferService(db, fixture.Store, TimeProvider.System);
        var draft = await transfer.PrepareAsync(fixture.OrganizationId, fixture.EmployeeId, user, TransferRequest(target, claim));
        var preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user);
        await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user, new(Guid.NewGuid(), preview.ReviewToken, "approve"));
        preview = await transfer.GetAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user);
        await transfer.TransitionAsync(fixture.OrganizationId, fixture.EmployeeId, draft.PackageId, user, new(Guid.NewGuid(), preview.ReviewToken, "apply"));
        await db.MemoryEpisodeEnrichmentJobs.ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, MemoryCaptureStatus.Failed)
            .SetProperty(j => j.Attempts, 10));
        var jobId = await db.MemoryEpisodeEnrichmentJobs.Select(x => x.Id).SingleAsync();
        if (revoke)
        {
            await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(x => x.SetProperty(j => j.ReportsToOrganizationUserId, (Guid?)null));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => EpisodeRecovery(fixture, db).RetryAsync(fixture.OrganizationId,
                target, jobId, user, new(Guid.NewGuid(), 0)));
            Assert.Empty(await db.MemoryEpisodeRetryReceipts.ToListAsync());
        }
        else
        {
            var result = await EpisodeRecovery(fixture, db).RetryAsync(fixture.OrganizationId, target, jobId, user, new(Guid.NewGuid(), 0));
            Assert.Equal("Pending", result.Job.Status); Assert.Single(await db.MemoryEpisodeRetryReceipts.ToListAsync());
        }
    }
}
