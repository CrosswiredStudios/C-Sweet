using System.Data.Common;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed class QueueEvidenceReadFailure : DbCommandInterceptor
    {
        public Exception? Failure { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Failure is { } error && command.CommandText.Contains("\"CoreConversations\"", StringComparison.Ordinal))
            {
                Failure = null;
                throw error;
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [MemoryPostgresFact]
    public async Task QueuedMemoryRecoveryLeavesInfrastructureFailuresPendingAndAllowsRetry()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        var failure = new QueueEvidenceReadFailure();
        await using var db = fixture.Context(failure); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        foreach (var error in new Exception[] { new TimeoutException("injected_timeout"), new Npgsql.NpgsqlException("injected_database_failure") })
        {
            failure.Failure = error;
            var observed = await Record.ExceptionAsync(() => inbox.ClaimAsync(DeliverySession(session), default));
            Assert.NotNull(observed);
            // The EF provider can wrap transient failures; neither wrapper nor cause is stale evidence.
            Assert.True(ReferenceEquals(error, observed) || ReferenceEquals(error, observed.InnerException));
            await using var fresh = fixture.Context();
            var pending = await fresh.AgentWorkItems.SingleAsync(x => x.Id == work.Id);
            Assert.Equal(AgentWorkStatus.Pending, pending.Status); Assert.Null(pending.ProtectedResult);
            Assert.Empty(await fresh.AgentWorkAttempts.ToListAsync()); Assert.Empty(await fresh.AgentMemoryReadReceipts.ToListAsync());
        }
        Assert.Equal(work.Id, (await inbox.ClaimAsync(DeliverySession(session), default))?.WorkId);
        Assert.Single(await db.AgentMemoryReadReceipts.ToListAsync());
    }

    private sealed class QueueRejectionSaveFailure : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<AgentWorkItem>().Any(x =>
                    x.Entity.Status == AgentWorkStatus.DeadLetter))
                // The UPDATE has reached PostgreSQL; the owning claim transaction must roll it back.
                throw new InvalidOperationException("injected_rejection_save_failure");
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    [MemoryPostgresFact]
    public async Task QueuedMemoryRecoveryPersistsTerminalResultAtomicallyAndOnlyOnce()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(fixture); var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
        var failure = new QueueRejectionSaveFailure();
        await using var db = fixture.Context(failure); await db.Database.OpenConnectionAsync();
        await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
        await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "changed evidence"));
        failure.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.ClaimAsync(DeliverySession(session), default));
        await using (var fresh = fixture.Context())
        {
            var pending = await fresh.AgentWorkItems.SingleAsync(x => x.Id == work.Id);
            Assert.Equal(AgentWorkStatus.Pending, pending.Status); Assert.Null(pending.ProtectedResult); Assert.Null(pending.CompletedAt);
            Assert.Empty(await fresh.AgentWorkAttempts.ToListAsync()); Assert.Empty(await fresh.AgentMemoryReadReceipts.ToListAsync());
        }
        failure.Enabled = false; db.ChangeTracker.Clear();
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        var terminal = await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id);
        Assert.Equal(AgentWorkStatus.DeadLetter, terminal.Status); Assert.NotNull(terminal.CompletedAt); Assert.NotNull(terminal.ResultHash);
        Assert.Equal(MemoryRecallDeliveryRejectedException.Code, (await inbox.ReadStateAsync(work.Id, default)).Completion?.FailureCode);
        Assert.Null(await inbox.ClaimAsync(DeliverySession(session), default));
        var replay = await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id);
        Assert.Equal(terminal.CompletedAt, replay.CompletedAt); Assert.Equal(terminal.ResultHash, replay.ResultHash);
    }
}
