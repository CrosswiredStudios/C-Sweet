using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Setup;
using CSweet.Memory;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private const string ErasureSecret = "private-erasure-work-content";

    private static async Task<(Guid Work, Guid Runtime, Guid Pending, Guid Later, string Key)> SeedWorkErasureAsync(
        DurabilityFixture fixture, IDataProtectionProvider protection)
    {
        var (session, original) = await SeedBrokerReadLeaseAsync(fixture);
        var runtimeId = Guid.Parse(session.RuntimeInstanceId);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync(WorkMemoryErasure.InstallTriggers);
        var protector = protection.CreateProtector("CSweet.AgentWorkInbox.v1");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { text = ErasureSecret });
        var work = await db.AgentWorkItems.SingleAsync(x => x.Id == original.Id);
        work.Name = work.LastError = work.CorrelationId = work.CausationId = ErasureSecret;
        work.IdempotencyKey = ErasureSecret; work.SourceType = "chat-turn";
        work.ProtectedPayload = protector.Protect(bytes); work.PayloadHash = Convert.ToHexString(SHA256.HashData(bytes));
        work.ProtectedResult = protector.Protect(bytes);
        var attempt = await db.AgentWorkAttempts.SingleAsync(); attempt.Error = ErasureSecret;
        db.AgentWorkProgress.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, AgentWorkAttemptId = attempt.Id,
            Sequence = 1, ProtectedValue = protector.Protect(bytes), SizeBytes = bytes.Length });
        var later = new AgentWorkItem { Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId, OrganizationId = work.OrganizationId,
            IdempotencyKey = Guid.NewGuid().ToString(), Name = ErasureSecret, ProtectedPayload = protector.Protect(bytes),
            ProtectedResult = protector.Protect(bytes), Status = AgentWorkStatus.Completed, AttemptCount = 1 };
        db.AgentWorkItems.Add(later);
        db.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = later.Id, RuntimeInstanceId = runtimeId,
            Attempt = 1, FinishedAt = DateTimeOffset.UtcNow, Error = ErasureSecret });
        var pending = new AgentWorkItem { Id = Guid.NewGuid(), AgentInstallationId = fixture.InstallationId, OrganizationId = work.OrganizationId,
            IdempotencyKey = Guid.NewGuid().ToString(), Name = "unrelated", ProtectedPayload = protector.Protect("{}"u8.ToArray()) };
        db.AgentWorkItems.Add(pending);
        var persisted = DeliverySession(session); persisted.PackageVersionId = (await db.AgentInstallations.SingleAsync()).PackageVersionId;
        db.McpAgentSessions.Add(persisted);
        await db.SaveChangesAsync();
        return (work.Id, runtimeId, pending.Id, later.Id, work.IdempotencyKey);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkErasureCommitsOrRollsBackStoreSourceWorkResetAndAuditTogether(bool failAudit)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var protection = new EphemeralDataProtectionProvider();
        var seeded = await SeedWorkErasureAsync(fixture, protection);
        Guid conversation;
        await using (var seed = fixture.Context())
        {
            conversation = await seed.CoreConversationMessages.Select(x => x.ConversationId).SingleAsync();
            await fixture.Service(seed, new UsageProviderFactory()).CaptureMessageAsync(fixture.MessageId);
            if (failAudit) await seed.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_work_erasure_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
                CREATE TRIGGER fail_work_erasure_audit BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW
                    WHEN (NEW."SourceEntityType"='MemoryErasure') EXECUTE FUNCTION fail_work_erasure_audit();
                """);
        }
        await using (var db = fixture.Context())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        using (var work = new MemoryWorkErasure(db, protection, TimeProvider.System))
        {
            await work.AcquireAsync(default);
            var capture = new MemoryCaptureErasure(db); await capture.AcquireAsync(default);
            var plan = await work.PrepareAsync(fixture.OrganizationId, [seeded.Work], [], default);
            Assert.Equal(new[] { seeded.Work, seeded.Later }.Order(), plan.Works.Select(x => x.Id).Order());
            var extraction = await capture.PrepareAsync(fixture.OrganizationId,
                [new(fixture.MessageId, conversation, fixture.EmployeeId, fixture.HumanId)], default);
            await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var inventory = await store.PreviewEpisodeErasureAsync(fixture.Partition, fixture.MessageId);
            Assert.Equal(new MemoryWorkErasure.Result(2, 1, 1), await work.StageAsync(plan, default));
            Assert.Equal(new MemoryWorkErasure.Result(0, 0, 0), await work.StageAsync(plan, default));
            await capture.StageAsync(extraction, DateTimeOffset.UtcNow, default);
            await store.EraseEpisodeAsync(fixture.Partition, fixture.MessageId, inventory.EvidenceToken);
            db.QueueAudit(new AuditEventWriteRequest("memory.erasure.work.test", "Memory", OrganizationId: fixture.OrganizationId,
                EntityType: "MemoryErasure", EntityId: fixture.MessageId, Summary: "Content-free erasure receipt.", UseAmbientOrganization: false));
            if (failAudit)
            {
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                await transaction.RollbackAsync();
            }
            else { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        }
        await using var check = fixture.Context();
        var original = await check.AgentWorkItems.SingleAsync(x => x.Id == seeded.Work);
        var runtime = await check.AgentRuntimeInstances.SingleAsync();
        Assert.Equal(AgentWorkStatus.Pending, (await check.AgentWorkItems.SingleAsync(x => x.Id == seeded.Pending)).Status);
        var protector = protection.CreateProtector("CSweet.AgentWorkInbox.v1");
        if (failAudit)
        {
            Assert.Null(original.MemoryErasedAt); Assert.Null(runtime.MemoryResetRequestedAt);
            Assert.Contains(ErasureSecret, Encoding.UTF8.GetString(protector.Unprotect(original.ProtectedPayload)));
            Assert.Single(await check.AgentWorkProgress.ToListAsync());
            Assert.Null((await check.McpAgentSessions.SingleAsync()).RevokedAt);
            Assert.Empty(await check.MemorySourceInvalidations.ToListAsync());
            Assert.Contains((await fixture.Store.ExportAsync(fixture.Partition)).Episodes, x => x.Id == fixture.MessageId);
        }
        else
        {
            Assert.NotNull(original.MemoryErasedAt); Assert.Equal(AgentWorkStatus.DeadLetter, original.Status);
            Assert.NotNull(runtime.MemoryResetRequestedAt); Assert.Equal(MemoryRuntimeResetRequiredException.ErasedEvidence, runtime.MemoryResetReasonCode);
            Assert.NotNull((await check.McpAgentSessions.SingleAsync()).RevokedAt);
            Assert.Empty(await check.AgentWorkProgress.ToListAsync());
            Assert.All(await check.AgentWorkAttempts.ToListAsync(), x => { Assert.NotNull(x.FinishedAt); Assert.Equal(MemoryWorkErasure.FailureCode, x.Error); });
            Assert.Equal("{}", Encoding.UTF8.GetString(protector.Unprotect(original.ProtectedPayload)));
            Assert.DoesNotContain(ErasureSecret, Encoding.UTF8.GetString(protector.Unprotect(original.ProtectedResult!)) +
                original.Name + original.CorrelationId + original.CausationId + original.SourceId + original.IdempotencyKey + original.LastError);
            Assert.Equal(AgentWorkStatus.Completed, (await check.AgentWorkItems.SingleAsync(x => x.Id == seeded.Later)).Status);
            var result = await new AgentWorkInbox(check, protection, TimeProvider.System).ReadStateAsync(seeded.Work, default);
            Assert.Equal(MemoryWorkErasure.FailureCode, result.Completion!.FailureCode); Assert.False(result.Completion.Retryable);
            Assert.DoesNotContain((await fixture.Store.ExportAsync(fixture.Partition)).Episodes, x => x.Id == fixture.MessageId);
            Assert.Single(await check.MemorySourceInvalidations.ToListAsync());
        }
    }

    [MemoryPostgresFact]
    public async Task WorkErasureFencesStaleWritesRawRestorationNewProgressAndIdempotentReplay()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var protection = new EphemeralDataProtectionProvider(); var seeded = await SeedWorkErasureAsync(fixture, protection);
        await using var stale = fixture.Context(); var old = await stale.AgentWorkItems.SingleAsync(x => x.Id == seeded.Later);
        await using (var db = fixture.Context())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        using (var erasure = new MemoryWorkErasure(db, protection, TimeProvider.System))
        {
            await erasure.AcquireAsync(default);
            var plan = await erasure.PrepareAsync(fixture.OrganizationId, [], [seeded.Runtime], default);
            await erasure.StageAsync(plan, default); await transaction.CommitAsync();
        }
        old.ProtectedResult = [1, 2, 3];
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using var check = fixture.Context();
        await Assert.ThrowsAsync<PostgresException>(() => check.AgentWorkItems.Where(x => x.Id == seeded.Work)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProtectedPayload, new byte[] { 1, 2, 3 })));
        var attempt = await check.AgentWorkAttempts.FirstAsync();
        await Assert.ThrowsAsync<PostgresException>(() => check.AgentWorkAttempts.Where(x => x.Id == attempt.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Error, ErasureSecret)));
        await Assert.ThrowsAsync<PostgresException>(() => check.AgentWorkItems.Where(x => x.Id == seeded.Work)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryErasedAt, (DateTimeOffset?)null)));
        check.AgentWorkProgress.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = attempt.AgentWorkItemId,
            AgentWorkAttemptId = attempt.Id, Sequence = 99, ProtectedValue = [1, 2, 3] });
        await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync());
        check.ChangeTracker.Clear();
        var installation = await check.AgentInstallations.SingleAsync();
        installation.RevisionStatus = PluginRevisionStatus.Active; installation.IsEnabled = true;
        await check.SaveChangesAsync();
        var replayError = await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentWorkInbox(check, protection, TimeProvider.System).EnqueueAsync(
            fixture.OrganizationId.ToString("D"), fixture.InstallationId, AgentWorkKind.Capability, "fixture",
            JsonSerializer.SerializeToElement(new { text = ErasureSecret }), seeded.Key, DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.Contains("Erased work cannot be replayed", replayError.Message);
        Assert.Equal(3, await check.AgentWorkItems.CountAsync());
    }

    [MemoryPostgresFact]
    public async Task WorkErasureFollowsRetriesToAnotherRuntimeAndItsLaterWork()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var protection = new EphemeralDataProtectionProvider(); var seeded = await SeedWorkErasureAsync(fixture, protection);
        var secondRuntime = Guid.NewGuid();
        await using (var seed = fixture.Context())
        {
            (await seed.AgentRuntimeInstances.SingleAsync()).TransitionTo(AgentRuntimeStatus.ExitedWithoutCompletion, DateTimeOffset.UtcNow);
            await seed.SaveChangesAsync();
            seed.AgentRuntimeInstances.Add(new() { Id = secondRuntime, AgentInstallationId = fixture.InstallationId, TickId = Guid.NewGuid() });
            seed.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = seeded.Later, RuntimeInstanceId = secondRuntime, Attempt = 2 });
            seed.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = seeded.Pending, RuntimeInstanceId = secondRuntime, Attempt = 1 });
            await seed.SaveChangesAsync();
        }
        await using var db = fixture.Context(); await using var transaction = await db.Database.BeginTransactionAsync();
        using var erasure = new MemoryWorkErasure(db, protection, TimeProvider.System);
        await erasure.AcquireAsync(default);
        var plan = await erasure.PrepareAsync(fixture.OrganizationId, [seeded.Work], [], default);
        Assert.Equal(3, plan.Works.Count); Assert.Equal(2, plan.Runtimes.Count);
        Assert.Equal(new MemoryWorkErasure.Result(3, 1, 1), await erasure.StageAsync(plan, default));
        await transaction.CommitAsync();
        Assert.Equal(3, await db.AgentWorkItems.CountAsync(x => x.MemoryErasedAt != null));
    }

    [MemoryPostgresFact]
    public async Task WorkErasureResetsActiveRuntimeEvenAfterItsGrantWasRemoved()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var protection = new EphemeralDataProtectionProvider(); var seeded = await SeedWorkErasureAsync(fixture, protection);
        await using var db = fixture.Context(); await db.AgentInstallationGrants.ExecuteDeleteAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var erasure = new MemoryWorkErasure(db, protection, TimeProvider.System);
        await erasure.AcquireAsync(default);
        var plan = await erasure.PrepareAsync(fixture.OrganizationId, [seeded.Work], [], default);
        Assert.Equal(1, (await erasure.StageAsync(plan, default)).ResetRuntimes);
        await transaction.CommitAsync();
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task WorkErasureRefusesForeignBindingsAndBusyClaimGatesWithoutMutation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var protection = new EphemeralDataProtectionProvider(); var seeded = await SeedWorkErasureAsync(fixture, protection);
        await using var db = fixture.Context(); await using var transaction = await db.Database.BeginTransactionAsync();
        using var erasure = new MemoryWorkErasure(db, protection, TimeProvider.System);
        await erasure.AcquireAsync(default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => erasure.PrepareAsync(Guid.NewGuid(), [seeded.Work], [], default));
        var gate = AgentWorkInbox.ClaimLocks.GetOrAdd(fixture.InstallationId, _ => new(1, 1));
        await gate.WaitAsync();
        try { await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => erasure.PrepareAsync(fixture.OrganizationId, [seeded.Work], [], default)); }
        finally { gate.Release(); }
        Assert.False(await db.AgentWorkItems.AnyAsync(x => x.MemoryErasedAt != null));
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task WorkErasureMigrationPreservesExistingPayloadsAndRefusesTombstoneDowngrade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var protection = new EphemeralDataProtectionProvider(); var seeded = await SeedWorkErasureAsync(fixture, protection);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync(); var migration = new WorkMemoryErasure();
        await RunConversationMigrationAsync(db, migration.DownOperations);
        await RunConversationMigrationAsync(db, migration.UpOperations);
        Assert.Null((await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == seeded.Work)).MemoryErasedAt);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        using (var erasure = new MemoryWorkErasure(db, protection, TimeProvider.System))
        {
            await erasure.AcquireAsync(default); var plan = await erasure.PrepareAsync(fixture.OrganizationId, [seeded.Work], [], default);
            await erasure.StageAsync(plan, default); await transaction.CommitAsync();
        }
        var error = await Assert.ThrowsAsync<PostgresException>(() => RunConversationMigrationAsync(db, migration.DownOperations));
        Assert.Contains("memory_work_erasure_downgrade_requires_snapshot", error.MessageText);
        Assert.NotNull((await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == seeded.Work)).MemoryErasedAt);
    }

    [MemoryPostgresFact]
    public async Task WorkErasureNeverSkipsTheDatabaseClaimLockAfterAFailedPreparation()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var protection = new EphemeralDataProtectionProvider(); var seeded = await SeedWorkErasureAsync(fixture, protection);
        await using var db = fixture.Context();
        using var erasure = new MemoryWorkErasure(db, protection, TimeProvider.System);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await erasure.AcquireAsync(default);
            await using (var otherReplica = fixture.Context())
            await using (var claiming = await otherReplica.Database.BeginTransactionAsync())
            {
                var key = $"agent-work-claim:{fixture.OrganizationId:D}:{fixture.InstallationId:D}";
                await otherReplica.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))");
                for (var i = 0; i < 2; i++)
                    await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => erasure.PrepareAsync(fixture.OrganizationId, [seeded.Work], [], default));
            }
            var plan = await erasure.PrepareAsync(fixture.OrganizationId, [seeded.Work], [], default);
            await erasure.StageAsync(plan, default); await transaction.CommitAsync();
        }
        await using var nextTransaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => erasure.AcquireAsync(default));
    }

    [MemoryPostgresFact]
    public async Task WorkErasureDoesNotCopyUnknownLegacyContentIntoItsSettlementAudit()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var protection = new EphemeralDataProtectionProvider(); var seeded = await SeedWorkErasureAsync(fixture, protection);
        await using var db = fixture.Context();
        await db.AgentWorkItems.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceType, "legacy-unknown"));
        await db.AuditOutbox.ExecuteDeleteAsync(); // Examine only new cleanup evidence.
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var erasure = new MemoryWorkErasure(db, protection, TimeProvider.System);
        await erasure.AcquireAsync(default);
        var plan = await erasure.PrepareAsync(fixture.OrganizationId, [seeded.Work], [], default);
        await erasure.StageAsync(plan, default); await transaction.CommitAsync();
        var rows = await db.AuditOutbox.AsNoTracking().Where(x => x.SourceEntityType == nameof(AgentWorkAttempt)).ToListAsync();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(row.RequestJson)!;
            var payload = Encoding.UTF8.GetString(request.Payload!.Value.Span);
            Assert.Contains("memory-content-omitted-v1", payload);
            Assert.DoesNotContain(ErasureSecret, payload + request.CorrelationId + request.Summary);
        }
        Assert.False(db.OmitWorkAuditContent);
    }

    [MemoryPostgresFact]
    public async Task WorkErasureRequiresTransactionAndRejectsConcurrentWritersAndOversizedPlans()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var protection = new EphemeralDataProtectionProvider(); var seeded = await SeedWorkErasureAsync(fixture, protection);
        await using var db = fixture.Context();
        using var erasure = new MemoryWorkErasure(db, protection, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => erasure.AcquireAsync(default));
        await using (var writer = fixture.Context())
        await using (var writing = await writer.Database.BeginTransactionAsync())
        {
            await writer.AgentWorkItems.Where(x => x.Id == seeded.Work).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastError, "changing"));
            await using var transaction = await db.Database.BeginTransactionAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => erasure.AcquireAsync(deadline.Token));
        }
        await using var retry = await db.Database.BeginTransactionAsync();
        await erasure.AcquireAsync(default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => erasure.PrepareAsync(fixture.OrganizationId,
            Enumerable.Range(0, 1025).Select(_ => Guid.NewGuid()).ToArray(), [], default));
        Assert.Equal("memory_erasure_scan_limit", error.Message);
        Assert.False(await db.AgentWorkItems.AnyAsync(x => x.MemoryErasedAt != null));
    }
}
