using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static readonly IDataProtectionProvider WorkflowProtection = new EphemeralDataProtectionProvider();
    private static AgentMemoryReviewService ErasureService(DurabilityFixture fixture, CSweet.Infrastructure.Persistence.CSweetDbContext db) =>
        new(db, fixture.Store, TimeProvider.System, WorkflowProtection);

    private static async Task<(Guid Actor, ChatTurn Turn, Guid Work)> SeedErasureWorkflowAsync(DurabilityFixture fixture, bool delivered = false)
    {
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        if (delivered)
        {
            var (session, initial) = await SeedBrokerReadLeaseAsync(fixture);
            await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
            var (inbox, work) = await QueueRecallAsync(fixture, db, turn);
            Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default));
        }
        else await QueueRecallAsync(fixture, db, turn);
        await db.ChatTurns.Where(x => x.Id == turn.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ChatTurnStatus.Completed));
        db.ChangeTracker.Clear();
        return (actor, turn, await db.AgentWorkItems.Select(x => x.Id).SingleAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErasureWorkflowCommitsStoreJobsWorkDiagnosticsAndReplayTogether(bool failAudit)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn, workId) = await SeedErasureWorkflowAsync(fixture);
        await using (var seed = fixture.Context())
        {
            seed.ChatTurnTraceEvents.Add(new() { Id = Guid.NewGuid(), ChatTurnId = turn.Id, Sequence = 1,
                EventType = "memory.recall", Title = "Alice private trace", DetailsJson = "{\"memory\":\"Alice\"}", OccurredAt = DateTimeOffset.UtcNow });
            (await seed.ChatTurns.SingleAsync()).PartialResponse = "Alice private partial";
            await seed.SaveChangesAsync();
            if (failAudit) await seed.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_erasure_workflow_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
                CREATE TRIGGER fail_erasure_workflow_audit BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW
                    WHEN (NEW."SourceEntityType"='MemoryErasure') EXECUTE FUNCTION fail_erasure_workflow_audit();
                """);
        }
        EraseMemorySourceRequest request;
        await using (var db = fixture.Context())
        {
            var service = ErasureService(fixture, db);
            var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
            Assert.Null(preview.ApplyBlockedReason); Assert.Equal(1, preview.DiagnosticTurns);
            request = new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
            if (failAudit) await Assert.ThrowsAsync<DbUpdateException>(() => service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, request));
            else
            {
                var result = await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, request);
                Assert.Equal("completed", result.Status); Assert.Equal(1, result.ClearedWorks); Assert.Equal(1, result.ClearedDiagnosticTurns);
                Assert.False(result.WasReplay); Assert.DoesNotContain("Alice", JsonSerializer.Serialize(result));
            }
        }
        await using var check = fixture.Context();
        var workRow = await check.AgentWorkItems.SingleAsync(x => x.Id == workId); var turnRow = await check.ChatTurns.SingleAsync();
        if (failAudit)
        {
            Assert.Null(workRow.MemoryErasedAt); Assert.Null(turnRow.MemoryErasedAt); Assert.Contains("Alice", turnRow.PartialResponse);
            Assert.Single(await check.ChatTurnTraceEvents.ToListAsync()); Assert.Empty(await check.MemoryErasureReceipts.ToListAsync());
            Assert.Empty(await check.MemorySourceInvalidations.ToListAsync()); Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, fixture.MessageId));
        }
        else
        {
            Assert.NotNull(workRow.MemoryErasedAt); Assert.Equal("{}"u8.ToArray(), WorkflowProtection.CreateProtector("CSweet.AgentWorkInbox.v1").Unprotect(workRow.ProtectedPayload));
            Assert.NotNull(turnRow.MemoryErasedAt); Assert.Empty(turnRow.PartialResponse);
            Assert.Empty(await check.ChatTurnTraceEvents.ToListAsync()); Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, fixture.MessageId));
            var receipt = await check.MemoryErasureReceipts.SingleAsync(); Assert.DoesNotContain("Alice", receipt.InventoryJson);
            var replay = await ErasureService(fixture, check).EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, request);
            Assert.True(replay.WasReplay); Assert.Equal(receipt.Id, replay.ReceiptId);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ErasureService(fixture, check).EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId,
                actor, request with { EvidenceToken = new string('a', 64) }));
            Assert.Contains("Alice", (await check.CoreConversationMessages.SingleAsync(x => x.Id == fixture.MessageId)).Content);
        }
    }

    [MemoryPostgresTheory]
    [InlineData("work")]
    [InlineData("diagnostic")]
    [InlineData("capture")]
    [InlineData("authority")]
    public async Task ErasureWorkflowBindsValuesBeyondMatchingCounts(string change)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn, work) = await SeedErasureWorkflowAsync(fixture);
        await using var db = fixture.Context();
        var service = ErasureService(fixture, db); var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        if (change == "work") await db.AgentWorkItems.Where(x => x.Id == work).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "changed"));
        if (change == "diagnostic") await db.ChatTurns.Where(x => x.Id == turn.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PartialResponse, "changed"));
        if (change == "capture") await db.MemoryCaptureOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.LastError, "changed"));
        if (change == "authority") await db.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DisplayName, "changed"));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, request));
        Assert.Empty(await db.MemoryErasureReceipts.ToListAsync()); Assert.NotNull(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, fixture.MessageId));
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErasureWorkflowKeepsRuntimeShutdownPendingUntilAcknowledgedAndRechecksReplayAuthority(bool missing)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _, _) = await SeedErasureWorkflowAsync(fixture, delivered: true);
        EraseMemorySourceRequest request;
        await using (var db = fixture.Context())
        {
            var service = ErasureService(fixture, db); var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
            request = new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
            var result = await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, request);
            Assert.Equal("runtime-reset-pending", result.Status); Assert.Equal(1, result.PendingRuntimes);
            var runtime = await db.AgentRuntimeInstances.AsNoTracking().SingleAsync(); Assert.NotNull(runtime.MemoryResetRequestedAt);
            Assert.NotNull(await db.AgentRuntimeEvents.FirstOrDefaultAsync(x => x.AgentRuntimeInstanceId == runtime.Id && x.Reason == MemoryRuntimeResetRequiredException.FailureCode));
        }
        await using var check = fixture.Context(); var current = ErasureService(fixture, check);
        Assert.Equal(1, (await current.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, request.OperationId, actor)).PendingRuntimes);
        if (missing)
        {
            await check.AgentWorkAttempts.ExecuteDeleteAsync();
            await check.AgentRuntimeInstances.ExecuteDeleteAsync();
            Assert.Equal(1, (await current.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, request.OperationId, actor)).PendingRuntimes);
        }
        else
        {
            await check.AgentRuntimeInstances.ExecuteUpdateAsync(s => s.SetProperty(x => x.MemoryResetCompletedAt, DateTimeOffset.UtcNow.AddDays(-1)));
            Assert.Equal(1, (await current.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, request.OperationId, actor)).PendingRuntimes);
            var stoppedRuntime = await check.AgentRuntimeInstances.SingleAsync();
            stoppedRuntime.TransitionTo(CSweet.Domain.Setup.AgentRuntimeStatus.Stopping, DateTimeOffset.UtcNow);
            stoppedRuntime.TransitionTo(CSweet.Domain.Setup.AgentRuntimeStatus.Cancelled, DateTimeOffset.UtcNow);
            stoppedRuntime.MemoryResetCompletedAt = DateTimeOffset.UtcNow; await check.SaveChangesAsync();
            Assert.Equal("completed", (await current.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, request.OperationId, actor)).Status);
        }
        await check.CoreOrganizationUsers.Where(x => x.Id == fixture.EmployeeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReportsToOrganizationUserId, (Guid?)null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => current.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => current.GetErasureStatusAsync(fixture.OrganizationId, fixture.EmployeeId, request.OperationId, actor));
    }

    [MemoryPostgresFact]
    public async Task ErasureWorkflowDoesNotOfferApplyWhileAnAffectedTurnIsRunning()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn) = await SeedErasureInventoryAsync(fixture);
        await using var db = fixture.Context(); await QueueRecallAsync(fixture, db, turn);
        var preview = await ErasureService(fixture, db).GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_chat_turn_in_progress", preview.ApplyBlockedReason); Assert.Null(preview.EvidenceToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ErasureService(fixture, db).EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, new(Guid.NewGuid(), new string('a', 64))));
        Assert.Empty(await db.MemorySourceInvalidations.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ErasureWorkflowIncludesGeneratedAnswerMemoryAndExcludesItFromFuturePrompts()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn, _) = await SeedErasureWorkflowAsync(fixture);
        var output = Guid.NewGuid();
        await using (var seed = fixture.Context())
        {
            seed.CoreConversationMessages.Add(new() { Id = output, ConversationId = turn.ConversationId, Role = ConversationRole.Assistant,
                SenderOrganizationUserId = fixture.EmployeeId, ChatTurnId = turn.Id, Content = "Alice echoed answer", CreatedAt = DateTimeOffset.UtcNow });
            (await seed.ChatTurns.SingleAsync()).AssistantMessageId = output; await seed.SaveChangesAsync();
            await fixture.Service(seed, new UsageProviderFactory()).CaptureMessageAsync(output);
        }
        await using var db = fixture.Context(); var service = ErasureService(fixture, db);
        var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Null(preview.ApplyBlockedReason); Assert.Equal(2, preview.Execution!.CaptureSources);
        await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken)));
        Assert.Null(await ((IMemorySourceReader)fixture.Store).GetEpisodeAsync(fixture.Partition, output));
        Assert.True(await db.MemorySourceInvalidations.AnyAsync(x => x.SourceMessageId == output));
        Assert.Contains("Alice", (await db.CoreConversationMessages.SingleAsync(x => x.Id == output)).Content);
    }

    [MemoryPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErasureWorkflowRefusesLegacyDiagnosticCopiesInsteadOfMutatingTheAuditLedger(bool extraMetadata)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn, _) = await SeedErasureWorkflowAsync(fixture);
        await using var db = fixture.Context();
        db.QueueAudit(extraMetadata ? new("chat.turn.completed", "ChatTurn", OrganizationId: fixture.OrganizationId, EntityType: "ChatTurn", EntityId: turn.Id,
            Summary: "chat.turn.completed", MetadataJson: "{\"details\":\"Alice private\"}", ContentType: "application/json",
            Payload: JsonSerializer.SerializeToUtf8Bytes(new { source = new { Id = turn.Id }, evidence = new { contentPolicy = "memory-content-omitted-v1" }, historical = false, historyNotice = (string?)null }),
            UseAmbientOrganization: false) : new("legacy.trace", "ChatTrace", OrganizationId: fixture.OrganizationId, EntityType: "ChatTurn", EntityId: turn.Id,
            Summary: "Alice private diagnostic", Payload: "Alice private"u8.ToArray(), UseAmbientOrganization: false));
        await db.SaveChangesAsync();
        var preview = await ErasureService(fixture, db).GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        Assert.Equal("memory_erasure_diagnostics_review_required", preview.ApplyBlockedReason); Assert.Null(preview.EvidenceToken);
        Assert.Empty(await db.MemoryErasureReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ErasureWorkflowMigrationPreservesRowsAndFencesRawWritesReplayAndDowngrade()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn, _) = await SeedErasureWorkflowAsync(fixture);
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync();
        var migration = new ReviewedMemoryErasure(); var generator = db.GetService<IMigrationsSqlGenerator>();
        await db.Database.ExecuteSqlRawAsync(ReviewedMemoryErasure.InstallTriggers);
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        Assert.Equal(turn.Id, await db.ChatTurns.Select(x => x.Id).SingleAsync());
        var service = ErasureService(fixture, db); var preview = await service.GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
        await service.EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken)));
        await Assert.ThrowsAsync<PostgresException>(() => db.ChatTurns.Where(x => x.Id == turn.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PartialResponse, "restore")));
        await Assert.ThrowsAsync<PostgresException>(() => db.MemoryErasureReceipts.ExecuteUpdateAsync(s => s.SetProperty(x => x.RequestHash, new string('a', 64))));
        db.ChatTurnTraceEvents.Add(new() { Id = Guid.NewGuid(), ChatTurnId = turn.Id, Sequence = 100, EventType = "late", Title = "restore", OccurredAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<PostgresException>(async () =>
        { foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText); });
        await db.ChatTurns.Where(x => x.Id == turn.Id).ExecuteDeleteAsync();
        Task<int> Insert(Guid id) => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ChatTurns" ("Id","OrganizationId","ConversationId","TargetAgentOrganizationUserId","UserMessageId",
                "Status","Attempt","NextTraceSequence","PartialResponse","CreatedAt","UpdatedAt")
            VALUES ({id},{fixture.OrganizationId},{turn.ConversationId},{fixture.EmployeeId},{turn.UserMessageId},
                {"Completed"},{1},{0},{string.Empty},{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow})
            """);
        var recreation = await Assert.ThrowsAsync<PostgresException>(() => Insert(turn.Id));
        Assert.Contains("memory_erased_chat_immutable", recreation.Message);
        var otherTurnId = Guid.NewGuid(); await Insert(otherTurnId);
        var mutation = await Assert.ThrowsAsync<PostgresException>(() => db.ChatTurns.Where(x => x.Id == otherTurnId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Id, turn.Id)));
        Assert.Contains("memory_erased_chat_immutable", mutation.Message);
        Assert.Single(await db.MemoryErasureReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task ErasureWorkflowConcurrentRetriesProduceOneReceiptAndOneErasure()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _, _) = await SeedErasureWorkflowAsync(fixture);
        EraseMemorySourceRequest request;
        await using (var db = fixture.Context())
        {
            var preview = await ErasureService(fixture, db).GetErasureImpactAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor);
            request = new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        }
        async Task<MemoryErasureResponse> Apply()
        {
            await using var db = fixture.Context();
            return await ErasureService(fixture, db).EraseSourceAsync(fixture.OrganizationId, fixture.EmployeeId, fixture.MessageId, actor, request);
        }
        var results = await Task.WhenAll(Apply(), Apply());
        Assert.Equal(results[0].ReceiptId, results[1].ReceiptId); Assert.Single(results, x => !x.WasReplay);
        await using var check = fixture.Context(); Assert.Single(await check.MemoryErasureReceipts.ToListAsync());
    }
}

