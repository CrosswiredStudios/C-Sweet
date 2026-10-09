using System.Text;
using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Infrastructure.Setup;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private const string ModelDiagnosticSecret = "private-model-diagnostic-should-be-erased";

    private static async Task<AgentRunLog> SeedModelDiagnosticAsync(DurabilityFixture f, CSweetDbContext db, Guid work, Guid turn)
    {
        var log = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, EmployeeId = f.EmployeeId,
            AgentInstallationId = f.InstallationId, AgentWorkItemId = work, ChatTurnId = turn, ProviderProfileId = f.ProviderId,
            AgentKey = "test-agent", Model = "test-model", Status = "Failed", StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow, ProviderStartedAt = DateTimeOffset.UtcNow.AddSeconds(-4),
            MeasurementKind = "ProviderAttempt", AttributionKind = "Unknown", AgentPackageVersion = "1.2.3",
            PromptPreview = ModelDiagnosticSecret, OutputPreview = ModelDiagnosticSecret, FailureMessage = ModelDiagnosticSecret,
            InferenceSettingsJson = JsonSerializer.Serialize(new { custom = ModelDiagnosticSecret }),
            UsageAdditionalCountsJson = JsonSerializer.Serialize(new { custom = ModelDiagnosticSecret }),
            RequestEvidenceJson = "not-json: " + ModelDiagnosticSecret,
            TokenInputCount = 51, TokenOutputCount = 13, ReportedInputTokens = 51, ReportedOutputTokens = 13,
            DurationMs = 1234, PromptMemoryCharacters = 42, PromptMessageCharacters = 61 };
        db.Add(log); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); return log;
    }

    [MemoryPostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ModelDiagnosticErasureScrubsCopiesPreservesAccountingAndRollsBackAtomically(bool failAudit)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, turn, work) = await SeedErasureWorkflowAsync(f);
        await using var db = f.Context(); await db.Database.ExecuteSqlRawAsync(MemoryModelDiagnosticErasure.InstallGuards);
        var log = await SeedModelDiagnosticAsync(f, db, work, turn.Id);
        var before = await db.AgentRunLogs.ProviderCalls().SumAsync(default);
        var unrelated = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, EmployeeId = f.EmployeeId,
            AgentInstallationId = f.InstallationId, ProviderProfileId = f.ProviderId, AgentKey = "unrelated",
            StartedAt = DateTimeOffset.UtcNow, Status = "Completed", OutputPreview = ModelDiagnosticSecret };
        db.Add(unrelated); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (failAudit) await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_model_erasure_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER fail_model_erasure_audit BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW
                WHEN (NEW."SourceEntityType"='MemoryErasure') EXECUTE FUNCTION fail_model_erasure_audit();
            """);
        var service = ErasureService(f, db);
        var preview = await service.GetErasureImpactAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor);
        Assert.Null(preview.ApplyBlockedReason); Assert.Equal(1, preview.Execution!.ModelRuns);
        Assert.DoesNotContain(ModelDiagnosticSecret, JsonSerializer.Serialize(preview));
        var request = new EraseMemorySourceRequest(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken));
        if (failAudit) await Assert.ThrowsAsync<DbUpdateException>(() => service.EraseSourceAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor, request));
        else
        {
            var response = await service.EraseSourceAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor, request);
            Assert.Equal(1, response.ClearedModelRuns); Assert.Equal("completed", response.Status);
            Assert.Equal(1, (await service.GetErasureStatusAsync(f.OrganizationId, f.EmployeeId, request.OperationId, actor)).ClearedModelRuns);
            var replay = await service.EraseSourceAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor, request);
            Assert.True(replay.WasReplay); Assert.Equal(1, replay.ClearedModelRuns);
        }
        await using var check = f.Context(); var current = await check.AgentRunLogs.SingleAsync(x => x.Id == log.Id);
        if (failAudit)
        {
            Assert.Null(current.MemoryErasedAt); Assert.Equal(ModelDiagnosticSecret, current.OutputPreview);
            Assert.Empty(await check.MemoryErasureReceipts.ToListAsync());
            Assert.NotNull(await ((IMemorySourceReader)f.Store).GetEpisodeAsync(f.Partition, f.MessageId));
        }
        else
        {
            Assert.NotNull(current.MemoryErasedAt); Assert.Null(current.PromptPreview); Assert.Null(current.OutputPreview);
            Assert.Null(current.FailureMessage); Assert.Null(current.InferenceSettingsJson); Assert.Null(current.UsageAdditionalCountsJson);
            Assert.DoesNotContain(ModelDiagnosticSecret, current.MemoryErasureAuditJson!);
            Assert.Equal(log.Model, current.Model); Assert.Equal(log.Status, current.Status); Assert.Equal(log.MeasurementKind, current.MeasurementKind);
            Assert.Equal(log.AttributionKind, current.AttributionKind); Assert.Equal(log.AgentPackageVersion, current.AgentPackageVersion);
            Assert.Equal(before, await check.AgentRunLogs.Where(x => x.Id == log.Id).ProviderCalls().SumAsync(default));
            Assert.Equal(ModelDiagnosticSecret, (await check.AgentRunLogs.SingleAsync(x => x.Id == unrelated.Id)).OutputPreview);
            Assert.Contains("Alice", (await check.CoreConversationMessages.SingleAsync(x => x.Id == f.MessageId)).Content);
            Assert.Null(await ((IMemorySourceReader)f.Store).GetEpisodeAsync(f.Partition, f.MessageId));
        }
    }

    [MemoryPostgresTheory]
    [InlineData("copy")] [InlineData("metadata")] [InlineData("unbound")] [InlineData("foreign-employee")]
    public async Task ModelDiagnosticErasureRequiresReviewForUnownedOrHistoricalContent(string defect)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); var (actor, turn, work) = await SeedErasureWorkflowAsync(f);
        await using var db = f.Context(); var log = await SeedModelDiagnosticAsync(f, db, work, turn.Id);
        if (defect == "unbound") await db.AgentRunLogs.Where(x => x.Id == log.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgentWorkItemId, (Guid?)null));
        else if (defect == "foreign-employee") await db.AgentRunLogs.Where(x => x.Id == log.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.EmployeeId, f.HumanId));
        else
        {
            db.QueueAudit(new("model.response.chunk", "Model", OrganizationId: f.OrganizationId, EntityType: nameof(AgentRunLog), EntityId: log.Id,
                MetadataJson: defect == "metadata" ? JsonSerializer.Serialize(new { note = ModelDiagnosticSecret }) : null,
                ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(defect == "copy"
                    ? new { sequence = 1, text = ModelDiagnosticSecret } : (object)new { sequence = 1, contentPolicy = "memory-content-omitted-v1" }),
                UseAmbientOrganization: false)); await db.SaveChangesAsync();
        }
        var service = ErasureService(f, db); var preview = await service.GetErasureImpactAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor);
        Assert.Equal("memory_erasure_diagnostics_review_required", preview.ApplyBlockedReason); Assert.Null(preview.EvidenceToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EraseSourceAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor,
            new(Guid.NewGuid(), new string('a', 64))));
        Assert.Null((await db.AgentRunLogs.AsNoTracking().SingleAsync(x => x.Id == log.Id)).MemoryErasedAt);
        Assert.NotNull(await ((IMemorySourceReader)f.Store).GetEpisodeAsync(f.Partition, f.MessageId));
    }

    [MemoryPostgresTheory]
    [InlineData("output")] [InlineData("audit")]
    public async Task ModelDiagnosticErasureRejectsChangesAfterPreviewWithoutScrubbing(string change)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); var (actor, turn, work) = await SeedErasureWorkflowAsync(f);
        await using var db = f.Context(); var log = await SeedModelDiagnosticAsync(f, db, work, turn.Id); var service = ErasureService(f, db);
        var preview = await service.GetErasureImpactAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor);
        if (change == "output") await db.AgentRunLogs.Where(x => x.Id == log.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.OutputPreview, "new output"));
        else { db.QueueAudit(new("model.response.chunk", "Model", OrganizationId: f.OrganizationId, EntityType: nameof(AgentRunLog), EntityId: log.Id,
            ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new { sequence = 1, contentPolicy = "memory-content-omitted-v1" }),
            UseAmbientOrganization: false)); await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.EraseSourceAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor,
            new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken))));
        Assert.Empty(await db.MemoryErasureReceipts.ToListAsync()); Assert.Null((await db.AgentRunLogs.AsNoTracking().SingleAsync()).MemoryErasedAt);
    }

    [MemoryPostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ModelDiagnosticErasureAllowsReviewedAuditDeliveryBeforeAndAfterCleanup(bool deliveredFirst)
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); var (actor, turn, work) = await SeedErasureWorkflowAsync(f);
        await using var seed = f.Context(); await seed.Database.ExecuteSqlRawAsync(MemoryModelDiagnosticErasure.InstallGuards);
        var log = await SeedModelDiagnosticAsync(f, seed, work, turn.Id);
        var options = (DbContextOptions<CSweetDbContext>)seed.GetService<IDbContextOptions>();
        await using var services = new ServiceCollection().AddScoped(_ => new CSweetDbContext(options, WorkflowProtection)).BuildServiceProvider();
        var writer = new AuditEventWriter(services.GetRequiredService<IServiceScopeFactory>(), new AuditExecutionContextAccessor(), WorkflowProtection);
        await using var db = new CSweetDbContext(options, WorkflowProtection);
        var dispatcher = new AuditOutboxDispatcher(db, writer, TimeProvider.System);
        db.QueueAudit(new("model.response.chunk", "Model", OrganizationId: f.OrganizationId, EntityType: nameof(AgentRunLog), EntityId: log.Id,
            ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new { sequence = 1, contentPolicy = "memory-content-omitted-v1",
                inputTokens = 51, outputTokens = 13 }), UseAmbientOrganization: false)); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (deliveredFirst) { await dispatcher.DispatchAsync(default); db.ChangeTracker.Clear(); }
        var service = ErasureService(f, db); var preview = await service.GetErasureImpactAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor);
        Assert.Null(preview.ApplyBlockedReason);
        await service.EraseSourceAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor, new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken)));
        db.ChangeTracker.Clear(); await dispatcher.DispatchAsync(default); db.ChangeTracker.Clear();
        var outbox = await db.AuditOutbox.Where(x => x.SourceEntityId == log.Id).ToArrayAsync(); Assert.NotEmpty(outbox);
        Assert.All(outbox, x => { Assert.NotNull(x.DeliveredAt); Assert.Null(x.LastError); });
        var events = await db.AuditEvents.Where(x => x.EntityId == log.Id).ToArrayAsync(); Assert.Equal(outbox.Length, events.Length);
        foreach (var item in events)
        {
            Assert.DoesNotContain(ModelDiagnosticSecret, item.PayloadPreview!);
            if (item.EventType == "model.response.chunk") Assert.Null(item.Summary); else Assert.Equal(item.EventType, item.Summary);
            var body = await db.AuditEventPayloads.SingleAsync(x => x.AuditEventId == item.Id);
            var json = Encoding.UTF8.GetString(WorkflowProtection.CreateProtector("CSweet.AuditPayload.v1").Unprotect(body.ProtectedContent));
            Assert.Contains("memory-content-omitted-v1", json); Assert.DoesNotContain(ModelDiagnosticSecret, json);
            await Assert.ThrowsAsync<PostgresException>(() => db.AuditEventPayloads.Where(x => x.AuditEventId == item.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProtectedContent, new byte[] { 1, 2, 3 })));
            await Assert.ThrowsAsync<PostgresException>(() => db.AuditEvents.Where(x => x.Id == item.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.PayloadPreview, ModelDiagnosticSecret).SetProperty(x => x.EntityId, (Guid?)null)));
        }
    }

    [MemoryPostgresFact]
    public async Task ModelDiagnosticErasureFencesLateWritesRebindingDeletionAndDowngrade()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); var (actor, turn, work) = await SeedErasureWorkflowAsync(f);
        await using var db = f.Context(); await db.Database.ExecuteSqlRawAsync(MemoryModelDiagnosticErasure.InstallGuards);
        var log = await SeedModelDiagnosticAsync(f, db, work, turn.Id);
        await using var stale = f.Context(); var staleLog = await stale.AgentRunLogs.SingleAsync();
        var service = ErasureService(f, db); var preview = await service.GetErasureImpactAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor);
        await service.EraseSourceAsync(f.OrganizationId, f.EmployeeId, f.MessageId, actor, new(Guid.NewGuid(), Assert.IsType<string>(preview.EvidenceToken)));
        staleLog.OutputPreview = "late private response"; await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await Assert.ThrowsAsync<PostgresException>(() => db.AgentRunLogs.Where(x => x.Id == log.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.OutputPreview, ModelDiagnosticSecret)));
        await Assert.ThrowsAsync<PostgresException>(() => db.AgentRunLogs.Where(x => x.Id == log.Id).ExecuteDeleteAsync());
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"AgentRunLogs\""));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(MemoryModelDiagnosticErasure.RemoveGuards));
        var prior = await db.AuditOutbox.FirstAsync(x => x.SourceEntityId == log.Id);
        await Assert.ThrowsAsync<PostgresException>(() => db.AuditOutbox.Where(x => x.Id == prior.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceEntityId, (Guid?)null).SetProperty(x => x.RequestJson, ModelDiagnosticSecret)));
        db.QueueAudit(new("model.response.chunk", "Model", OrganizationId: f.OrganizationId, EntityType: nameof(AgentRunLog), EntityId: log.Id,
            ContentType: "application/json", Payload: JsonSerializer.SerializeToUtf8Bytes(new { text = ModelDiagnosticSecret }), UseAmbientOrganization: false));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        var late = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, AgentWorkItemId = work,
            ProviderProfileId = f.ProviderId, StartedAt = DateTimeOffset.UtcNow, Status = "Completed", AgentKey = "late" };
        db.Add(late); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [MemoryPostgresFact]
    public async Task ModelDiagnosticMigrationPreservesLegacyAccountingAndMatchesFinalModel()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true); await using var db = f.Context();
        var log = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.OrganizationId, ProviderProfileId = f.ProviderId,
            AgentKey = "legacy", Status = "Completed", Model = "test-model", StartedAt = DateTimeOffset.UtcNow, TokenInputCount = 7 };
        db.Add(log); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync(MemoryModelDiagnosticErasure.InstallGuards);
        var generator = db.GetService<IMigrationsSqlGenerator>(); var migration = new MemoryModelDiagnosticErasure();
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await tx.CommitAsync(); var current = await db.AgentRunLogs.SingleAsync();
        Assert.Null(current.MemoryErasedAt); Assert.Null(current.MemoryErasureAuditJson); Assert.Equal(log.Model, current.Model); Assert.Equal(7, current.TokenInputCount);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("20261009163000_MemoryModelDiagnosticErasure", db.Database.GetMigrations().Last());
    }

    [MemoryPostgresFact]
    public async Task ModelDiagnosticProviderOmitsExplicitChatContextBeforeAnyAutomaticCaseInstruction()
    {
        await using var f = await DurabilityFixture.CreateAsync(postgres: true);
        var turn = await SeedRecallTurnAsync(f); var (session, initial) = await SeedBrokerReadLeaseAsync(f);
        await using var db = f.Context(); await db.AgentWorkItems.Where(x => x.Id == initial.Id).ExecuteDeleteAsync();
        var (inbox, work) = await QueueRecallAsync(f, db, turn);
        Assert.NotNull(await inbox.ClaimAsync(DeliverySession(session), default)); db.ChangeTracker.Clear();
        (await db.LlmProviderProfiles.SingleAsync()).DefaultChatModel = "test-model"; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var provider = new InstructionDispatchProvider();
        var results = await RunInstructionModelAsync(f, session, work, provider, callerInstructions: ModelDiagnosticSecret);
        Assert.All(results, x => Assert.True(x.Succeeded, x.Error)); Assert.Equal(1, provider.Sends);
        Assert.Contains(ModelDiagnosticSecret, provider.Context);
        var log = await db.AgentRunLogs.SingleAsync(); Assert.Equal(work.Id, log.AgentWorkItemId); Assert.Null(log.OutputPreview);
        var copies = await db.AuditOutbox.Where(x => x.SourceEntityId == log.Id).ToArrayAsync(); Assert.True(copies.Length >= 3);
        foreach (var copy in copies)
        {
            var request = JsonSerializer.Deserialize<CSweet.Application.Setup.AuditEventWriteRequest>(copy.RequestJson)!;
            var body = Encoding.UTF8.GetString(request.Payload!.Value.Span);
            Assert.Contains("memory-content-omitted-v1", body); Assert.DoesNotContain(ModelDiagnosticSecret, body);
            Assert.DoesNotContain("Completed the requested paddle change", body);
        }
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync()); Assert.Null((await db.AgentRuntimeInstances.SingleAsync()).MemoryResetRequestedAt);
    }
}
