using System.Text.Json.Nodes;
using CSweet.AI.Providers;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    [MemoryPostgresFact]
    public async Task ApplyHoldsSourceLocksUntilDerivedWritesAndCompletionCommit()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var assistantId = await AddSourceAssistantAsync(fixture);
        await using var db = fixture.Context();
        await fixture.Store.InitializeAsync();
        await using var gate = new NpgsqlConnection(db.Database.GetConnectionString());
        await gate.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(6281006)", gate))
            await hold.ExecuteNonQueryAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION pause_source_test_apply() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(6281006); RETURN NEW; END $$;
            CREATE TRIGGER pause_source_test_apply BEFORE INSERT ON csweet_memory_claims
                FOR EACH ROW EXECUTE FUNCTION pause_source_test_apply();
            """);
        var processing = fixture.Service(db, new UsageProviderFactory()).ProcessPendingAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                await using var waiting = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=6281006 AND NOT granted)", gate);
                if ((bool)(await waiting.ExecuteScalarAsync(deadline.Token))!) break;
                if (processing.IsCompleted) Assert.Fail("Enrichment finished before reaching the apply gate.");
                await Task.Delay(25, deadline.Token);
            }
            await using var editor = new NpgsqlConnection(db.Database.GetConnectionString());
            await editor.OpenAsync();
            await using (var timeout = new NpgsqlCommand("SET lock_timeout = '250ms'", editor))
                await timeout.ExecuteNonQueryAsync();
            await using var edit = new NpgsqlCommand("UPDATE \"CoreConversationMessages\" SET \"Content\"='changed' WHERE \"Id\"=@id", editor);
            edit.Parameters.AddWithValue("id", assistantId);
            var conflict = await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, conflict.SqlState);
            edit.CommandText = "DELETE FROM csweet_memory_episodes WHERE id=@id";
            conflict = await Assert.ThrowsAsync<PostgresException>(() => edit.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, conflict.SqlState);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(6281006)", gate);
            await release.ExecuteNonQueryAsync();
            await processing.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(MemoryCaptureStatus.Completed,
            (await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId)).Status);
        Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("employee")]
    [InlineData("message")]
    [InlineData("installation")]
    [InlineData("endpoint")]
    public async Task DispatchRechecksSourcesAndAuthorityAfterClientCreation(string revoked)
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var factory = new ClientCreationProviderFactory(async () =>
        {
            await using var changed = fixture.Context();
            if (revoked == "provider") (await changed.LlmProviderProfiles.SingleAsync()).IsEnabled = false;
            if (revoked == "endpoint") (await changed.LlmProviderProfiles.SingleAsync()).BaseUrl = "http://changed-provider/v1";
            if (revoked == "employee") (await changed.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.EmployeeId)).IsActive = false;
            if (revoked == "installation") (await changed.AgentInstallations.SingleAsync()).IsEnabled = false;
            if (revoked == "message") (await changed.CoreConversationMessages.SingleAsync()).Content = "Changed after client selection";
            await changed.SaveChangesAsync();
        });
        await using var db = fixture.Context();
        Assert.Equal(0, await fixture.Service(db, factory).ProcessPendingAsync());
        Assert.Equal(0, factory.Calls);
        Assert.Null((await db.MemoryCaptureOutbox.SingleAsync()).AcceptedExtractionJson);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.Equal("Denied", (await db.AgentRunLogs.SingleAsync()).Status);
        Assert.Null((await db.AgentRunLogs.SingleAsync()).ProviderStartedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrDeletedAssistantDuringInferenceCannotBeAccepted(bool delete)
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var assistantId = await AddSourceAssistantAsync(fixture);
        var factory = new ScriptedProviderFactory(async (_, _) =>
        {
            await using var changed = fixture.Context();
            var assistant = await changed.CoreConversationMessages.SingleAsync(x => x.Id == assistantId);
            if (delete) changed.Remove(assistant); else assistant.Content = "Edited answer";
            await changed.SaveChangesAsync();
        });
        await using var db = fixture.Context();
        await fixture.Service(db, factory).ProcessPendingAsync();
        var job = await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
        Assert.Equal(MemoryCaptureStatus.Failed, job.Status);
        Assert.Equal("memory_enrichment_source_invalidated", job.LastError);
        Assert.Null(job.AcceptedExtractionJson);
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public async Task MissingCapturedEpisodeIsNotResurrected()
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
        await using var db = fixture.Context();
        var service = fixture.Service(db, factory);
        await service.CaptureMessageAsync(fixture.MessageId);
        await fixture.Store.DeleteScopeAsync(fixture.Partition);
        Assert.Equal(0, await service.ProcessPendingAsync());
        Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.Equal(0, factory.Calls);
        Assert.Equal("memory_enrichment_source_invalidated", (await db.MemoryCaptureOutbox.SingleAsync()).LastError);
    }

    [MemoryPostgresFact]
    public async Task AcceptedAssistantEvidenceIsValidatedOnReplayAndOldEnvelopesFailClosed()
    {
        foreach (var legacy in new[] { false, true })
        {
            await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
            var assistantId = await AddSourceAssistantAsync(fixture);
            var factory = new ScriptedProviderFactory((_, _) => Task.CompletedTask);
            await using var db = fixture.Context();
            await fixture.Store.InitializeAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_source_test_claim() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected apply failure'; END $$;
                CREATE TRIGGER reject_source_test_claim BEFORE INSERT ON csweet_memory_claims
                    FOR EACH ROW EXECUTE FUNCTION reject_source_test_claim();
                """);
            await fixture.Service(db, factory).ProcessPendingAsync();
            var job = await db.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
            var envelope = JsonNode.Parse(job.AcceptedExtractionJson!)!;
            Assert.Equal(3, envelope["SchemaVersion"]!.GetValue<int>());
            Assert.Equal(assistantId, envelope["Sources"]!["Messages"]![1]!["Id"]!.GetValue<Guid>());
            Assert.Equal(fixture.ProviderId, envelope["Provider"]!["Id"]!.GetValue<Guid>());
            if (legacy)
            {
                envelope["SchemaVersion"] = 1;
                envelope.AsObject().Remove("Sources");
                job.AcceptedExtractionJson = envelope.ToJsonString();
            }
            else (await db.CoreConversationMessages.SingleAsync(x => x.Id == assistantId)).Content = "Changed after acceptance";
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_source_test_claim ON csweet_memory_claims; DROP FUNCTION reject_source_test_claim();");
            await using var restarted = fixture.Context();
            await fixture.Service(restarted, factory).ProcessPendingAsync();
            job = await restarted.MemoryCaptureOutbox.SingleAsync(x => x.ConversationMessageId == fixture.MessageId);
            Assert.Equal(MemoryCaptureStatus.Failed, job.Status);
            Assert.Equal(legacy ? "memory_enrichment_unverifiable_output" : "memory_enrichment_source_invalidated", job.LastError);
            Assert.NotNull(job.AcceptedExtractionJson);
            Assert.Equal(1, factory.Calls);
            Assert.Empty((await fixture.Store.ExportAsync(fixture.Partition)).Claims);
        }
    }

    [Theory]
    [InlineData("memory_enrichment_source_invalidated")]
    [InlineData("memory_enrichment_unverifiable_output")]
    [InlineData("memory_enrichment_input_receipt_capacity")]
    public async Task OperatorRetryCannotReopenInvalidatedEvidence(string code)
    {
        await using var fixture = await DurabilityFixture.CreateAsync();
        var (actorId, jobId) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        (await db.MemoryCaptureOutbox.SingleAsync()).LastError = code;
        await db.SaveChangesAsync();
        var recovery = new AgentMemoryRecoveryService(db, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RetryAsync(fixture.OrganizationId,
            fixture.EmployeeId, jobId, actorId, new(Guid.NewGuid(), 0)));
        Assert.Empty(await db.MemoryCaptureRetryReceipts.ToListAsync());
        Assert.Equal(code, Assert.Single((await recovery.ListFailuresAsync(fixture.OrganizationId,
            fixture.EmployeeId, actorId)).Items).FailureCode);
    }

    private static async Task<Guid> AddSourceAssistantAsync(DurabilityFixture fixture)
    {
        await using var db = fixture.Context();
        var user = await db.CoreConversationMessages.SingleAsync();
        var assistant = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = user.ConversationId,
            Role = ConversationRole.Assistant, Content = "Hello Alice.", CreatedAt = DateTimeOffset.UtcNow, Sequence = user.Sequence + 1 };
        db.Add(assistant);
        await db.SaveChangesAsync();
        return assistant.Id;
    }

    private sealed class ClientCreationProviderFactory(Func<Task> beforeCreate) : ILlmProviderFactory
    {
        public int Calls { get; private set; }
        public Task<IChatClient> CreateChatClientAsync(Guid id, CancellationToken ct = default) => CreateChatClientAsync(id, null, ct);
        public async Task<IChatClient> CreateChatClientAsync(Guid id, string? model, CancellationToken ct = default)
        {
            await beforeCreate();
            return new ScriptedChatClient(_ => { Calls++; return Task.CompletedTask; });
        }
    }
}
