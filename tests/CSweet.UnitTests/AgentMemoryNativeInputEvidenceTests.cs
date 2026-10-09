using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using DomainSession = CSweet.Domain.Communications.AgentCoordinationSession;
using DomainTurn = CSweet.Domain.Communications.AgentCoordinationTurn;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private const string NativeInputSecret = "native-collaboration-input-do-not-copy";
    private static async Task<(DomainSession Source, AgentCoordinationTurnRequest Request)> NativeCoordinationInputAsync(
        DurabilityFixture f, CSweetDbContext db)
    {
        var scoped = await SeedScopedAudienceAsync(f, "Case");
        var (_, original) = await SeedBrokerReadLeaseAsync(f);
        var coordination = await SeedScopedCoordinationAsync(f, scoped, original.Id);
        await db.Database.ExecuteSqlRawAsync(NativeWorkInputOrigins.InstallGuards);
        var source = await db.AgentCoordinationSessions.Include(x => x.Turns).SingleAsync(x => x.Id == coordination.Session);
        source.Subject = source.Objective = NativeInputSecret; source.SuccessCriteriaJson = JsonSerializer.Serialize(new[] { NativeInputSecret });
        var initialTurn = new DomainTurn { Id = Guid.NewGuid(), SessionId = source.Id, EventId = Guid.NewGuid(),
            SpeakerOrganizationUserId = source.TargetOrganizationUserId, Content = NativeInputSecret, Disposition = "Continue",
            IdempotencyKey = Guid.NewGuid().ToString("D"), Ordinal = 0, CreatedAt = DateTimeOffset.UtcNow };
        db.AgentCoordinationTurns.Add(initialTurn);
        await db.SaveChangesAsync();
        var turn = source.Turns.Single();
        var request = new AgentCoordinationTurnRequest(source.Id, source.Revision, source.NextTurnOrdinal,
            source.Subject, source.Objective, [NativeInputSecret], new(f.EmployeeId, f.InstallationId, "Self", "Director"),
            new(source.TargetOrganizationUserId, source.TargetInstallationId, "Partner", "Producer"), false,
            [new(turn.Id, turn.Ordinal, turn.SpeakerOrganizationUserId, turn.Disposition, turn.Content, turn.CreatedAt)])
        {
            SourceKind = "WorkItem", MaximumTurns = source.MaximumTurns,
            WorkSource = new(source.SourceBoardId!.Value, source.SourceWorkItemId!.Value, source.SourceSprintExecutionId!.Value,
                source.SourceStageExecutionId!.Value, source.SourceAssignmentRevision!.Value)
        };
        return (source, request);
    }

    [MemoryPostgresFact]
    public async Task NativeInputReceiptBindsExactQueuedCopyAndReplaysWithoutDuplicatingOrCopyingText()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        var (source, request) = await NativeCoordinationInputAsync(f, db);
        var protection = new EphemeralDataProtectionProvider();
        var inbox = new AgentWorkInbox(db, protection, TimeProvider.System); var eventId = Guid.NewGuid();
        Guid workId; string proof;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var work = await inbox.EnqueueCaseCoordinationAsync(source, request, eventId, DateTimeOffset.UtcNow.AddHours(1), default);
            workId = work.Id; proof = Assert.IsType<string>(work.NativeWorkInputReceiptJson);
            Assert.DoesNotContain(NativeInputSecret, proof); Assert.DoesNotContain(source.Subject, proof);
            var repeated = await inbox.EnqueueCaseCoordinationAsync(source, request, eventId, DateTimeOffset.UtcNow.AddHours(1), default);
            Assert.Equal(work.Id, repeated.Id); Assert.Equal(proof, repeated.NativeWorkInputReceiptJson);
            using var receipt = JsonDocument.Parse(proof);
            Assert.Equal(work.Id, receipt.RootElement.GetProperty("workId").GetGuid());
            Assert.Equal(work.PayloadHash, receipt.RootElement.GetProperty("payloadHash").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(work.ProtectedPayload)), receipt.RootElement.GetProperty("protectedPayloadHash").GetString());
            Assert.Equal(source.SourceWorkItemId, receipt.RootElement.GetProperty("origins").GetProperty("caseId").GetGuid());
            Assert.Equal(source.ConversationId, receipt.RootElement.GetProperty("origins").GetProperty("conversationId").GetGuid());
            Assert.Equal(source.Turns.Single().Id, receipt.RootElement.GetProperty("origins").GetProperty("turnIds")[0].GetGuid());
            Assert.Null(work.MemoryRecallReceiptJson);
            Assert.Contains(NativeInputSecret, Encoding.UTF8.GetString(protection.CreateProtector("CSweet.AgentWorkInbox.v1").Unprotect(work.ProtectedPayload)));
            await transaction.CommitAsync();
        }
        await using var reconnect = f.Context();
        var persisted = await reconnect.AgentWorkItems.SingleAsync(x => x.Id == workId); Assert.Equal(proof, persisted.NativeWorkInputReceiptJson);
        var audit = await reconnect.AuditOutbox.Where(x => x.SourceEntityId == workId).ToArrayAsync(); Assert.NotEmpty(audit);
        foreach (var copy in audit)
        {
            var record = JsonSerializer.Deserialize<CSweet.Application.Setup.AuditEventWriteRequest>(copy.RequestJson)!;
            Assert.NotNull(record.Payload);
            var content = Encoding.UTF8.GetString(record.Payload!.Value.Span);
            Assert.Contains("memory-content-omitted-v1", content); Assert.DoesNotContain(NativeInputSecret, content);
        }
        Assert.Null((await reconnect.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData("case")]
    [InlineData("private-source")]
    [InlineData("speaker")]
    [InlineData("content")]
    public async Task NativeInputBuilderRejectsForeignOrUnmatchedOriginsBeforeEnqueue(string change)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        var (source, request) = await NativeCoordinationInputAsync(f, db);
        if (change == "case") request = request with { WorkSource = request.WorkSource! with { ItemId = Guid.NewGuid() } };
        if (change == "private-source") source.SourceConversationId = Guid.NewGuid();
        if (change == "speaker") request = request with { Transcript = [request.Transcript[0] with { SpeakerOrganizationUserId = f.HumanId }] };
        if (change == "content") request = request with { Transcript = [request.Transcript[0] with { Content = "Changed after building" }] };
        await using var tx = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System)
            .EnqueueCaseCoordinationAsync(source, request, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), default));
        Assert.False(await db.AgentWorkItems.AnyAsync(x => x.NativeWorkInputReceiptJson != null));
    }

    [MemoryPostgresFact]
    public async Task NativeInputEnqueueRequiresPrimaryTransactionAndRollsBackItsWorkAndAudit()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        var (source, request) = await NativeCoordinationInputAsync(f, db);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.EnqueueCaseCoordinationAsync(source, request,
            Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), default));
        Guid id;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            id = (await inbox.EnqueueCaseCoordinationAsync(source, request, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), default)).Id;
            source.FinalSummary = "Uncommitted primary effect"; await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        await using var reconnect = f.Context();
        Assert.False(await reconnect.AgentWorkItems.AnyAsync(x => x.Id == id));
        Assert.False(await reconnect.AuditOutbox.AnyAsync(x => x.SourceEntityId == id));
        Assert.Null((await reconnect.AgentCoordinationSessions.SingleAsync()).FinalSummary);
    }

    [MemoryPostgresTheory]
    [InlineData("receipt")]
    [InlineData("payload")]
    [InlineData("correlation")]
    [InlineData("source")]
    [InlineData("retrofit")]
    public async Task NativeInputDatabaseGuardsRejectMutationAndRetrospectiveCertification(string change)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        var (source, request) = await NativeCoordinationInputAsync(f, db);
        AgentWorkItem work;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            work = await new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System)
                .EnqueueCaseCoordinationAsync(source, request, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), default);
            await tx.CommitAsync();
        }
        await using var connection = f.IndependentConnection(); await connection.OpenAsync();
        var id = work.Id;
        var field = change switch { "receipt" or "retrofit" => "NativeWorkInputReceiptJson", "payload" => "ProtectedPayload",
            "correlation" => "CorrelationId", _ => "SourceId" };
        if (change == "retrofit") id = await db.AgentWorkItems.Where(x => x.NativeWorkInputReceiptJson == null).Select(x => x.Id).SingleAsync();
        await using var command = new NpgsqlCommand($"UPDATE \"AgentWorkItems\" SET \"{field}\"=@value WHERE \"Id\"=@id", connection);
        command.Parameters.AddWithValue("id", id);
        if (change == "payload") command.Parameters.AddWithValue("value", "changed"u8.ToArray());
        else command.Parameters.AddWithValue("value", change == "retrofit" ? work.NativeWorkInputReceiptJson! : "changed");
        Assert.Equal("memory_native_input_immutable", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).MessageText);
    }

    [MemoryPostgresFact]
    public async Task NativeInputUsedEvidenceRefusesDowngradeAndTruncationAndLegacyEnqueueStaysUncertified()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        var (source, request) = await NativeCoordinationInputAsync(f, db); var protection = new EphemeralDataProtectionProvider();
        var inbox = new AgentWorkInbox(db, protection, TimeProvider.System);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var work = await inbox.EnqueueCaseCoordinationAsync(source, request, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), default);
            var publicReplay = await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.EnqueueAsync(work.OrganizationId,
                work.AgentInstallationId, work.Kind, work.Name, JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                work.IdempotencyKey, DateTimeOffset.UtcNow.AddHours(1), work.CorrelationId, work.CausationId, work.SourceType, work.SourceId));
            Assert.Contains("different work content", publicReplay.Message);
            await tx.CommitAsync();
        }
        var legacy = await inbox.EnqueueAsync(f.OrganizationId.ToString("D"), f.InstallationId, CSweet.Domain.Setup.AgentWorkKind.Event,
            AgentCoordinationEvents.TurnRequested, JsonSerializer.SerializeToElement(request), "legacy-enqueue", DateTimeOffset.UtcNow.AddHours(1),
            sourceType: "agent-coordination", sourceId: Guid.NewGuid().ToString("D"));
        Assert.Null(legacy.NativeWorkInputReceiptJson);
        foreach (var sql in new[] { NativeWorkInputOrigins.RemoveGuards, "TRUNCATE \"AgentWorkItems\" CASCADE" })
            Assert.Equal("memory_native_input_downgrade_requires_snapshot",
                (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).MessageText);
    }

    [MemoryPostgresFact]
    public async Task NativeInputMalformedInsertFailsBeforeWorkBecomesVisible()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        await db.Database.ExecuteSqlRawAsync(NativeWorkInputOrigins.InstallGuards);
        db.AgentWorkItems.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId.ToString("D"), AgentInstallationId = f.InstallationId,
            IdempotencyKey = "malformed", NativeWorkInputReceiptJson = "{}" });
        Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException);
        await using var reconnect = f.Context(); Assert.False(await reconnect.AgentWorkItems.AnyAsync());
    }

    [MemoryPostgresFact]
    public async Task NativeInputEmptyMigrationRoundTripsAndPreservesUncertifiedExistingWork()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        var (_, existing) = await SeedBrokerReadLeaseAsync(f);
        await db.Database.ExecuteSqlRawAsync(NativeWorkInputOrigins.InstallGuards);
        var generator = db.GetService<IMigrationsSqlGenerator>(); var migration = new NativeWorkInputOrigins();
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await tx.CommitAsync();
        Assert.Null((await db.AgentWorkItems.SingleAsync(x => x.Id == existing.Id)).NativeWorkInputReceiptJson);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261009150000_NativeWorkInputOrigins", db.Database.GetMigrations());
    }

    [MemoryPostgresTheory]
    [InlineData("null-case")]
    [InlineData("missing-case")]
    [InlineData("wrong-coverage")]
    [InlineData("duplicate")]
    [InlineData("protected-payload")]
    public async Task NativeInputInsertChecksContentBindingAndStrictOriginShape(string change)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        var (source, request) = await NativeCoordinationInputAsync(f, db); AgentWorkItem original;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            original = await new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System)
                .EnqueueCaseCoordinationAsync(source, request, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), default);
            await tx.CommitAsync();
        }
        var clone = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = original.OrganizationId,
            AgentInstallationId = original.AgentInstallationId, Kind = original.Kind, Name = original.Name,
            CorrelationId = original.CorrelationId, CausationId = original.CausationId, SourceType = original.SourceType,
            SourceId = original.SourceId, IdempotencyKey = "malformed-clone", ProtectedPayload = original.ProtectedPayload,
            PayloadHash = original.PayloadHash, CreatedAt = DateTimeOffset.UtcNow, DeadlineAt = original.DeadlineAt };
        var proof = JsonNode.Parse(original.NativeWorkInputReceiptJson!)!;
        proof["workId"] = clone.Id.ToString("D");
        proof["idempotencyHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clone.IdempotencyKey)));
        if (change == "null-case") proof["origins"]!["caseId"] = null;
        if (change == "missing-case") proof["origins"]!.AsObject().Remove("caseId");
        if (change == "wrong-coverage") proof["coverage"] = "permission-to-erase";
        if (change == "protected-payload") clone.ProtectedPayload = "changed-after-binding"u8.ToArray();
        clone.NativeWorkInputReceiptJson = proof.ToJsonString();
        if (change == "duplicate") clone.NativeWorkInputReceiptJson = clone.NativeWorkInputReceiptJson.Insert(1, "\"version\":1,");
        db.Add(clone);
        Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).InnerException);
        await using var reconnect = f.Context(); Assert.False(await reconnect.AgentWorkItems.AnyAsync(x => x.Id == clone.Id));
    }

    [MemoryPostgresFact]
    public async Task NativeInputTrackedReceiptCannotBeChangedAndInternalErasurePreservesContentFreeOrigins()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        var (source, request) = await NativeCoordinationInputAsync(f, db); var protection = new EphemeralDataProtectionProvider();
        AgentWorkItem original;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            original = await new AgentWorkInbox(db, protection, TimeProvider.System)
                .EnqueueCaseCoordinationAsync(source, request, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), default);
            await tx.CommitAsync();
        }
        var proof = original.NativeWorkInputReceiptJson;
        original.NativeWorkInputReceiptJson = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync(WorkMemoryErasure.InstallTriggers);
        // Exercise compatibility of the internal scrub writer, not public erasure approval.
        // Input origins alone still cannot authorize the retained output/artifact closure.
        await using (var tx = await db.Database.BeginTransactionAsync())
        using (var erasure = new CSweet.Infrastructure.Core.MemoryWorkErasure(db, protection))
        {
            await erasure.AcquireAsync(default);
            var plan = await erasure.PrepareAsync(f.OrganizationId, [original.Id], [], default);
            Assert.Equal(1, (await erasure.StageAsync(plan, default)).ClearedWorks);
            await tx.CommitAsync();
        }
        await using var reconnect = f.Context(); var erased = await reconnect.AgentWorkItems.SingleAsync(x => x.Id == original.Id);
        Assert.NotNull(erased.MemoryErasedAt); Assert.Equal(proof, erased.NativeWorkInputReceiptJson);
        Assert.Equal("{}", Encoding.UTF8.GetString(protection.CreateProtector("CSweet.AgentWorkInbox.v1").Unprotect(erased.ProtectedPayload)));
        Assert.Equal(NativeInputSecret, (await reconnect.AgentCoordinationSessions.SingleAsync()).Objective);
        Assert.Equal(NativeInputSecret, (await reconnect.AgentCoordinationTurns.SingleAsync()).Content);
        Assert.Null((await reconnect.AgentWorkItems.SingleAsync(x => x.Id != original.Id)).NativeWorkInputReceiptJson);
    }
}
