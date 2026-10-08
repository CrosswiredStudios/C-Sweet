using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private static async Task<MemoryEpisode> SeedLegacyEpisode(DurabilityFixture fixture, bool classified = false,
        Func<MemoryEpisode, MemoryEpisode>? change = null)
    {
        await fixture.Store.ExportAsync(fixture.Partition);
        await using var db = fixture.Context();
        var message = await db.CoreConversationMessages.SingleAsync(x => x.Id == fixture.MessageId);
        var source = new MemoryEpisode(message.Id, fixture.Partition, MemoryScope.User, message.Content, "text/plain",
            new(message.Role == ConversationRole.User ? "user" : "assistant", message.Id.ToString("D"), message.Role.ToString()),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Content))).ToLowerInvariant(), message.CreatedAt, message.CreatedAt,
            message.Id.ToString("D"), DateTimeOffset.UtcNow.AddDays(2), LegalHold: true,
            Metadata: new Dictionary<string, string> { ["conversationId"] = message.ConversationId.ToString("D"),
                ["messageId"] = message.Id.ToString("D"), ["installationId"] = fixture.InstallationId.ToString("D"), ["role"] = message.Role.ToString() },
            Sensitivity: MemorySensitivity.Confidential);
        if (change is not null) source = change(source);
        var json = JsonSerializer.SerializeToNode(source, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (!classified) json.Remove("sensitivity");
        json["legacyExtension"] = "preserve me";
        var payload = json.ToJsonString();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO csweet_memory_episodes(id,partition_key,idempotency_key,content,occurred_at,expires_at,payload)
            VALUES ({source.Id},{source.Partition.StorageKey},{source.IdempotencyKey},{source.Content},{source.OccurredAt},{source.ExpiresAt},CAST({payload} AS jsonb))
            """);
        return source;
    }

    [MemoryPostgresFact]
    public async Task LegacyReviewEstablishesOnlyMatchingEvidenceAndPreservesAudienceRetentionAndHistory()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        Assert.True(preview.CanEstablishEvidence); Assert.Equal("Unclassified", preview.Sensitivity); Assert.Equal("Personal", preview.MinimumSensitivity);
        var request = new ReviewMemoryLegacyRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "Personal");
        var result = await service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request);
        var updated = Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes);
        Assert.True(MemorySourceIntegrity.IsVerified(updated)); Assert.Equal(episode.Source, updated.Source);
        Assert.Equal(episode.Partition, updated.Partition); Assert.Equal(episode.Content, updated.Content);
        Assert.Equal(episode.ExpiresAt, updated.ExpiresAt); Assert.True(updated.LegalHold); Assert.Equal(MemorySensitivity.Personal, updated.Sensitivity);
        Assert.True((await service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request)).WasReplay);
        Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
        var history = await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", episode.Id, actor);
        Assert.Equal(2, history.Items.Count); Assert.Contains("unverified", history.Items[0].State!);
        Assert.Equal("Unclassified", history.Items[0].RecordedSensitivity); Assert.Equal("Personal", history.Items[1].RecordedSensitivity);
        Assert.Contains("established", history.Items[1].State!); Assert.Equal(result.ReceiptId, Assert.Single(history.Items[1].Reviews).ReceiptId);
        var extension = await db.Database.SqlQuery<string>($"SELECT payload->>'legacyExtension' AS \"Value\" FROM csweet_memory_episodes WHERE id={episode.Id}").SingleAsync();
        Assert.Equal("preserve me", extension);
        await db.CoreOrganizationUsers.Where(x => x.Id == fixture.HumanId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request));
    }

    [MemoryPostgresFact]
    public async Task LegacyAssistantReviewPreservesSourceRoleAndDoesNotConfirmDerivedClaims()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture);
        await using var db = fixture.Context();
        await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Role, ConversationRole.Assistant).SetProperty(x => x.SenderOrganizationUserId, fixture.EmployeeId));
        var episode = await SeedLegacyEpisode(fixture);
        var block = new MemoryBlock(Guid.NewGuid(), fixture.Partition, "Legacy assertion", "Unconfirmed assertion", 1, 100, false,
            MemoryTrustTier.AgentInference, DateTimeOffset.UtcNow) { SourceEpisodeIds = [episode.Id], Confirmation = MemoryConfirmationState.Pending };
        await fixture.Store.WriteBlockAsync(block);
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        Assert.True(preview.CanEstablishEvidence); Assert.Equal("Internal", preview.MinimumSensitivity);
        await service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "Internal"));
        var raw = await fixture.Store.ExportAsync(fixture.Partition);
        Assert.Equal("assistant", Assert.Single(raw.Episodes).Source.Type);
        Assert.Equal(MemoryTrustTier.AgentInference, Assert.Single(raw.Blocks).Trust);
        Assert.Equal(MemoryConfirmationState.Pending, Assert.Single(raw.Blocks).Confirmation);
        Assert.DoesNotContain(await fixture.Store.SearchAsync(new(fixture.Partition, MemoryScope.User, "assertion")), x => x.Id == block.Id);
    }

    [MemoryPostgresFact]
    public async Task LegacyReviewCannotDowngradeExplicitSensitivityOrResealEstablishedEvidence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture, classified: true);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        Assert.True(preview.CanEstablishEvidence); Assert.Equal("Confidential", preview.MinimumSensitivity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "Personal")));
        await service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "Restricted"));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{content}}','\"tampered\"') WHERE id={episode.Id}");
        preview = await service.GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        Assert.False(preview.CanEstablishEvidence);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "Restricted")));
    }

    [MemoryPostgresFact]
    public async Task LegacyReviewRefusesForgedMetadataAuthorshipReferencesAndUnavailableSources()
    {
        foreach (var scenario in new[] { "author", "role", "metadata", "references", "checksum", "expired", "excluded", "message", "foreign", "installation", "sender" })
        {
            await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
            var (actor, _) = await SeedRecoveryAsync(fixture);
            var episode = await SeedLegacyEpisode(fixture, change: x => scenario switch
            {
                "author" => x with { Source = x.Source with { Author = "forged" } },
                "role" => x with { Source = x.Source with { Type = "application" } },
                "metadata" => x with { Metadata = new Dictionary<string, string>(x.Metadata!) { ["authority"] = "system" } },
                "references" => x with { OperationalReferences = [new("work-item", Guid.NewGuid().ToString())] },
                "checksum" => x with { Checksum = "forged" },
                "expired" => x with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) },
                "foreign" => x with { Partition = x.Partition with { UserId = Guid.NewGuid().ToString("D") } },
                "installation" => x with { Metadata = new Dictionary<string, string>(x.Metadata!) { ["installationId"] = Guid.NewGuid().ToString("D") } },
                _ => x
            });
            await using var db = fixture.Context();
            if (scenario == "message") await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, "changed"));
            if (scenario == "sender") await db.CoreConversationMessages.Where(x => x.Id == fixture.MessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SenderOrganizationUserId, fixture.EmployeeId));
            if (scenario == "excluded")
            {
                db.MemoryCaptureExclusions.Add(new() { SourceMessageId = fixture.MessageId, OrganizationId = fixture.OrganizationId,
                    EmployeeId = fixture.EmployeeId, TriggerJobId = Guid.NewGuid(), ReasonCode = "memory_capture_excluded", ExcludedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }
            var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
            if (scenario == "foreign")
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor)); continue;
            }
            var preview = await service.GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
            Assert.False(preview.CanEstablishEvidence, scenario);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
                new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "Restricted")));
            Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        }
    }

    [MemoryPostgresFact]
    public async Task LegacyReviewRejectsChangedPreviewAndAuditFailureRollsBackEvidence()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        await using var db = fixture.Context(); var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        var request = new ReviewMemoryLegacyRequest(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "Restricted");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,'{{legalHold}}','false') WHERE id={episode.Id}");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request));
        preview = await service.GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        request = request with { ExpectedRevision = preview.Revision, EvidenceToken = preview.EvidenceToken };
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_legacy_review() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected_failure'; END $$;
            CREATE TRIGGER fail_legacy_review BEFORE INSERT ON "ComputeAuditOutbox" FOR EACH ROW WHEN (NEW."SourceEntityType"='MemoryEpisode') EXECUTE FUNCTION fail_legacy_review();
            """);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor, request));
        Assert.Equal("injected_failure", Assert.IsType<PostgresException>(failure.InnerException).MessageText);
        Assert.Null(Assert.Single((await fixture.Store.ExportAsync(fixture.Partition)).Episodes).SourceFingerprint);
        Assert.Empty(await db.MemoryReviewReceipts.ToListAsync());
        Assert.Equal(2, (await service.ReadHistoryAsync(fixture.OrganizationId, fixture.EmployeeId, "Episode", episode.Id, actor)).Items.Count);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_legacy_review ON \"ComputeAuditOutbox\"; DROP FUNCTION fail_legacy_review();");
        async Task<bool> Attempt()
        {
            await using var context = fixture.Context();
            try { await new AgentMemoryReviewService(context, fixture.Store, TimeProvider.System).ReviewLegacyEpisodeAsync(fixture.OrganizationId,
                fixture.EmployeeId, episode.Id, actor, request with { OperationId = Guid.NewGuid() }); return true; }
            catch (DbUpdateConcurrencyException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(), Attempt()), x => x);
        Assert.Single(await db.MemoryReviewReceipts.ToListAsync());
    }

    [MemoryPostgresFact]
    public async Task LegacyReviewLetsAnExistingEnrichmentSourceReaderFinishBeforeTakingWriterBarrier()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        await using var worker = fixture.Context(); await using var transaction = await worker.Database.BeginTransactionAsync();
        await worker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreConversationMessages\" WHERE \"Id\"={episode.Id} FOR SHARE");
        await worker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM csweet_memory_episodes WHERE id={episode.Id} FOR SHARE");
        var entered = new LegacyMessageLockObserver(); await using var review = fixture.Context(entered);
        var pending = new AgentMemoryReviewService(review, fixture.Store, TimeProvider.System)
            .GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        try
        {
            await entered.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(pending.IsCompleted);
            await worker.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='2s'");
            await worker.Database.ExecuteSqlInterpolatedAsync($"UPDATE csweet_memory_episodes SET content=content WHERE id={episode.Id}");
        }
        finally { await transaction.CommitAsync(); }
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(20))).CanEstablishEvidence);
    }

    private sealed class LegacyMessageLockObserver : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CoreConversationMessages") && command.CommandText.Contains("FOR UPDATE")) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    [MemoryPostgresFact]
    public async Task LegacyReviewHoldsOriginalMessageAndAuthorityUntilCommit()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var (actor, _) = await SeedRecoveryAsync(fixture); var episode = await SeedLegacyEpisode(fixture);
        var gate = new ReviewCommitGate(); await using var db = fixture.Context(gate);
        var service = new AgentMemoryReviewService(db, fixture.Store, TimeProvider.System);
        var preview = await service.GetLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor);
        var pending = service.ReviewLegacyEpisodeAsync(fixture.OrganizationId, fixture.EmployeeId, episode.Id, actor,
            new(Guid.NewGuid(), preview.Revision, preview.EvidenceToken, "Restricted"));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await using var concurrent = fixture.Context(); await concurrent.Database.OpenConnectionAsync();
            await concurrent.Database.ExecuteSqlRawAsync("SET lock_timeout='200ms'");
            var sourceError = await Assert.ThrowsAsync<PostgresException>(() => concurrent.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"CoreConversationMessages\" SET \"Content\"='changed' WHERE \"Id\"={episode.Id}"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, sourceError.SqlState);
            var authorityError = await Assert.ThrowsAsync<PostgresException>(() => concurrent.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"CoreOrganizationUsers\" SET \"IsActive\"=false WHERE \"Id\"={fixture.HumanId}"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, authorityError.SqlState);
        }
        finally { gate.Release.TrySetResult(); }
        await pending;
    }
}
