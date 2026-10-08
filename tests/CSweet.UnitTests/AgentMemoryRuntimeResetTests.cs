using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Setup;
using CSweet.Memory;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task MemoryResetFencesOldCompletionThenSettlesOnceWithoutReplayingDeliveredWork()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var protection = new EphemeralDataProtectionProvider();
        var token = "test-lease-token";
        await using (var seed = fixture.Context())
        {
            (await seed.AgentWorkAttempts.SingleAsync()).LeaseTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            var persisted = DeliverySession(session);
            persisted.PackageVersionId = (await seed.AgentInstallations.SingleAsync()).PackageVersionId;
            seed.McpAgentSessions.Add(persisted);
            await seed.SaveChangesAsync();
        }
        await using var old = fixture.Context();
        await old.AgentWorkItems.Include(x => x.Attempts).SingleAsync(x => x.Id == work.Id);
        await using var staleRuntime = fixture.Context();
        await staleRuntime.AgentRuntimeInstances.SingleAsync();
        await using var db = fixture.Context();
        var reset = new AgentMemoryRuntimeReset(db);
        var runtimeId = Guid.Parse(session.RuntimeInstanceId);
        Assert.False(await reset.RequestAsync(runtimeId, Guid.NewGuid(), fixture.InstallationId, session.BusinessId, 1,
            MemoryRuntimeResetRequiredException.RetainedEvidence, default));
        Assert.False(await reset.RequestAsync(runtimeId, Guid.Parse(session.TickId), fixture.InstallationId, session.BusinessId, 2,
            MemoryRuntimeResetRequiredException.RetainedEvidence, default));
        Assert.True(await reset.RequestAsync(runtimeId, Guid.Parse(session.TickId), fixture.InstallationId, session.BusinessId, 1,
            MemoryRuntimeResetRequiredException.RetainedEvidence, default));
        var requested = (await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt;
        Assert.NotNull(requested);
        Assert.NotNull((await db.McpAgentSessions.SingleAsync()).RevokedAt);
        Assert.NotNull((await db.AgentWorkAttempts.SingleAsync()).FinishedAt);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new AgentRuntimeSignalService(staleRuntime)
            .RecordCompletionAsync(runtimeId, Guid.Parse(session.TickId), fixture.InstallationId, "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentRuntimeSignalService(db)
            .RecordMcpSessionEstablishedAsync(runtimeId, Guid.Parse(session.TickId), fixture.InstallationId, "old-token"));
        Assert.True(await reset.RequestAsync(runtimeId, Guid.Parse(session.TickId), fixture.InstallationId, session.BusinessId, 1,
            MemoryRuntimeResetRequiredException.ReceiptCapacity, default));
        Assert.Equal(requested, (await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        Assert.Single(await db.AgentRuntimeEvents.Where(x => x.Reason == MemoryRuntimeResetRequiredException.FailureCode).ToListAsync());
        var oldInbox = new AgentWorkInbox(old, protection, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldInbox.CompleteAsync(DeliverySession(session), work.Id, 1, token,
            new AgentWorkCompletion(true, null, null), default));
        var inbox = new AgentWorkInbox(db, protection, TimeProvider.System);
        var pending = await inbox.EnqueueAsync(session.BusinessId, fixture.InstallationId, AgentWorkKind.Capability, "future-work",
            JsonSerializer.SerializeToElement(new { text = "fresh request" }), "future-work", DateTimeOffset.UtcNow.AddMinutes(10));
        var settlementFailure = new StopEvidenceCommitFailure();
        await using (var failing = fixture.Context(settlementFailure))
            await Assert.ThrowsAsync<IOException>(() => new AgentWorkInbox(failing, protection, TimeProvider.System)
                .SettleMemoryResetAsync(runtimeId, default));
        Assert.True(settlementFailure.Observed);
        await using (var fresh = fixture.Context())
        {
            var retained = await fresh.AgentWorkItems.SingleAsync(x => x.Id == work.Id);
            Assert.Equal(AgentWorkStatus.Leased, retained.Status);
            Assert.Null(retained.ProtectedResult);
            Assert.Equal(MemoryRuntimeResetRequiredException.FailureCode, (await fresh.AgentWorkAttempts.SingleAsync()).Error);
        }
        await inbox.SettleMemoryResetAsync(runtimeId, default);
        var failed = await inbox.ReadStateAsync(work.Id, default);
        Assert.Equal(AgentWorkStatus.DeadLetter, failed.Status);
        Assert.Equal(MemoryRuntimeResetRequiredException.FailureCode, failed.Completion?.FailureCode);
        Assert.False(failed.Completion?.Retryable);
        Assert.Equal(AgentWorkStatus.Pending, (await inbox.ReadStateAsync(pending.Id, default)).Status);
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        var resultHash = (await db.AgentWorkItems.SingleAsync(x => x.Id == work.Id)).ResultHash;
        await inbox.SettleMemoryResetAsync(runtimeId, default);
        Assert.Equal(resultHash, (await db.AgentWorkItems.SingleAsync(x => x.Id == work.Id)).ResultHash);
    }

    [MemoryPostgresFact]
    public async Task MemoryResetRequestRollsBackFencingEventAndRequestOnCommitFailure()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, _) = await SeedBrokerReadLeaseAsync(fixture);
        await using (var seed = fixture.Context())
        {
            var persisted = DeliverySession(session);
            persisted.PackageVersionId = (await seed.AgentInstallations.SingleAsync()).PackageVersionId;
            seed.McpAgentSessions.Add(persisted);
            await seed.SaveChangesAsync();
        }
        var failure = new StopEvidenceCommitFailure();
        await using (var failing = fixture.Context(failure))
        {
            await Assert.ThrowsAsync<IOException>(() => new AgentMemoryRuntimeReset(failing).RequestAsync(
                Guid.Parse(session.RuntimeInstanceId), Guid.Parse(session.TickId), fixture.InstallationId, session.BusinessId,
                1, MemoryRuntimeResetRequiredException.RetainedEvidence, default));
            Assert.Empty(failing.ChangeTracker.Entries());
            // Reusing the same scope must try the transaction again, not acknowledge an
            // in-memory request whose earlier commit failed.
            await Assert.ThrowsAsync<IOException>(() => new AgentMemoryRuntimeReset(failing).RequestAsync(
                Guid.Parse(session.RuntimeInstanceId), Guid.Parse(session.TickId), fixture.InstallationId, session.BusinessId,
                1, MemoryRuntimeResetRequiredException.RetainedEvidence, default));
        }
        Assert.True(failure.Observed);
        await using var db = fixture.Context();
        Assert.Null((await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        Assert.Null((await db.AgentWorkAttempts.SingleAsync()).FinishedAt);
        Assert.Null((await db.McpAgentSessions.SingleAsync()).RevokedAt);
        Assert.Empty(await db.AgentRuntimeEvents.ToListAsync());
        Assert.True(await new AgentMemoryRuntimeReset(db).RequestAsync(Guid.Parse(session.RuntimeInstanceId), Guid.Parse(session.TickId),
            fixture.InstallationId, session.BusinessId, 1, MemoryRuntimeResetRequiredException.RetainedEvidence, default));
    }

    [MemoryPostgresFact]
    public async Task MemoryResetDistinguishesUnavailableEvidenceFromChangedRetainedEvidence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var claim = await SeedReviewClaim(fixture);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Confirmed);
        await using (var seed = fixture.Context())
            Assert.True((await ReadHandler(fixture, seed).HandleAsync(session, ReadSearch(fixture), default)).Succeeded);
        await using (var failing = fixture.Context(new ResetEvidenceUnavailable()))
            await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(failing)
                .AuthorizeDispatchAsync(session, work.Id, default));
        await using var db = fixture.Context();
        Assert.Null((await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        await fixture.Store.SetClaimConfirmationAsync(claim.Id, MemoryConfirmationState.Rejected);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db)
            .AuthorizeDispatchAsync(session, work.Id, default));
        Assert.Equal(MemoryRuntimeResetRequiredException.RetainedEvidence, (await db.AgentRuntimeInstances.SingleAsync()).MemoryResetReasonCode);
        Assert.Single(await db.AgentMemoryReadReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task MemoryResetMigrationPreservesExistingRuntimesAndRefusesEvidenceLoss()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (session, _) = await SeedBrokerReadLeaseAsync(fixture);
        await using var db = fixture.Context();
        await db.Database.OpenConnectionAsync();
        var migration = new DurableMemoryRuntimeReset();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Null((await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
        Assert.True(await new AgentMemoryRuntimeReset(db).RequestAsync(Guid.Parse(session.RuntimeInstanceId), Guid.Parse(session.TickId),
            fixture.InstallationId, session.BusinessId, 1, MemoryRuntimeResetRequiredException.LegacyEvidence, default));
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        });
        Assert.Contains("memory_runtime_reset_downgrade_requires_snapshot", error.MessageText);
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    private sealed class ResetEvidenceUnavailable : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("AgentMemoryReadReceipts", StringComparison.Ordinal))
                throw new InvalidOperationException("provider wrapped failure", new Npgsql.NpgsqlException("unavailable"));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
