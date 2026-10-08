using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CSweet.Infrastructure.Core;

/// <summary>
/// Versioned migration-time repair, not an online worker or an agent capability.
/// Run with old writers stopped; each bounded batch commits its cursor and repairs together.
/// </summary>
internal sealed class MemorySourceReconciler(CSweetDbContext db, IMemoryStore memory, ILogger<MemorySourceReconciler> logger)
{
    internal const string Version = "conversation-sources-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Row(Guid Id, string PartitionKey, string Payload);
    private sealed record Evidence(bool Matches, Guid? MessageId, Guid? ConversationId);

    internal async Task RunAsync(CancellationToken token = default)
    {
        while (true)
        {
            var progress = await ProcessBatchAsync(100, token);
            logger.LogInformation("Memory source reconciliation {Version}: {Scanned} checked, {Suppressed} suppressed; complete: {Complete}.",
                Version, progress.ScannedEpisodes, progress.SuppressedEpisodes, progress.CompletedAt.HasValue);
            if (progress.CompletedAt.HasValue) return;
        }
    }

    internal async Task<MemorySourceReconciliationCheckpoint> ProcessBatchAsync(int limit = 100, CancellationToken token = default)
    {
        if (!db.Database.IsNpgsql()) throw new NotSupportedException("Source reconciliation requires shared PostgreSQL.");
        await memory.InitializeAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Independent migrators serialize cursor ownership. Trigger writers take the marker
        // table before the episode barrier; match that order to avoid a marker/episode cycle.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(728145093)", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemorySourceReconciliationCheckpoints"("Id","ScannedEpisodes","SuppressedEpisodes","StartedAt")
                VALUES ({Version},0,0,{DateTimeOffset.UtcNow}) ON CONFLICT("Id") DO NOTHING
            """, token);
        var checkpoint = await db.MemorySourceReconciliationCheckpoints.AsNoTracking().SingleAsync(x => x.Id == Version, token);
        if (checkpoint.CompletedAt.HasValue) return checkpoint;
        await db.Database.ExecuteSqlRawAsync("""
            LOCK TABLE "MemorySourceInvalidations" IN SHARE ROW EXCLUSIVE MODE;
            LOCK TABLE csweet_memory_episodes IN EXCLUSIVE MODE;
            """, token);
        var rows = new List<Row>();
        await using (var command = Command("""
            SELECT id,partition_key,payload::text FROM csweet_memory_episodes
            WHERE (@after IS NULL OR id>@after) AND payload->'partition'->>'applicationId'='csweet'
                AND (lower(payload->'source'->>'type') IN ('user','assistant') OR
                    (payload->'metadata' ? 'conversationId' AND payload->'metadata' ? 'messageId'))
                AND payload->>'isSuppressed' IS DISTINCT FROM 'true'
            ORDER BY id LIMIT @limit
            """))
        {
            command.Parameters.AddWithValue("after", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)checkpoint.LastEpisodeId ?? DBNull.Value);
            command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var evidence = await MatchAsync(row, token);
            if (!evidence.Matches) checkpoint.SuppressedEpisodes += await SuppressAsync(row, evidence, token);
            checkpoint.LastEpisodeId = row.Id;
            checkpoint.ScannedEpisodes++;
        }
        // An empty final page proves the scan is exhausted; it is safe after a resumed batch.
        if (rows.Count == 0) checkpoint.CompletedAt = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemorySourceReconciliationCheckpoints" SET "LastEpisodeId"={checkpoint.LastEpisodeId},
                "ScannedEpisodes"={checkpoint.ScannedEpisodes},"SuppressedEpisodes"={checkpoint.SuppressedEpisodes},
                "CompletedAt"={checkpoint.CompletedAt} WHERE "Id"={Version}
            """, token);
        await transaction.CommitAsync(token);
        return checkpoint;
    }

    private async Task<Evidence> MatchAsync(Row row, CancellationToken token)
    {
        MemoryEpisode? episode;
        try { episode = JsonSerializer.Deserialize<MemoryEpisode>(row.Payload, Json); }
        catch (JsonException) { return new(false, null, null); }
        if (episode?.Metadata is null || episode.Partition is null || episode.Source is null ||
            !Guid.TryParse(episode.Source.Id, out var sourceId) || sourceId == Guid.Empty ||
            !episode.Metadata.TryGetValue("messageId", out var rawMessage) || !Guid.TryParse(rawMessage, out var messageId) || messageId != sourceId ||
            !episode.Metadata.TryGetValue("conversationId", out var rawConversation) || !Guid.TryParse(rawConversation, out var conversationId) || conversationId == Guid.Empty)
            return new(false, null, null);
        var invalid = new Evidence(false, sourceId, conversationId);
        if (episode.Id != row.Id || episode.Partition.StorageKey != row.PartitionKey || episode.Scope != MemoryScope.User ||
            episode.ContentType != "text/plain" || episode.TransferEvidence is not null ||
            (episode.SourceFingerprint is not null && !MemorySourceIntegrity.IsVerified(episode))) return invalid;
        var message = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, token);
        var conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == conversationId, token);
        if (message is null || conversation is null || message.ConversationId != conversationId ||
            conversation.Kind != ConversationKind.DirectHumanAgent || conversation.ArchivedAt.HasValue || conversation.MergedIntoConversationId.HasValue ||
            conversation.AgentOrganizationUserId is not Guid employeeId || message.Role is not (ConversationRole.User or ConversationRole.Assistant)) return invalid;
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == employeeId, token);
        var human = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == conversation.InitiatedByOrganizationUserId, token);
        if (owner is null || owner.OrganizationId != conversation.OrganizationId || owner.EmployeeType != EmployeeType.Agent ||
            human is null || human.OrganizationId != conversation.OrganizationId || human.EmployeeType != EmployeeType.Human ||
            owner.AgentInstallationId is not Guid installationId ||
            !episode.Metadata.TryGetValue("installationId", out var rawInstallation) || !Guid.TryParse(rawInstallation, out var capturedInstallation) || capturedInstallation != installationId ||
            !await db.AgentInstallations.AnyAsync(x => x.Id == installationId && x.BusinessId == conversation.OrganizationId.ToString(), token)) return invalid;
        var expected = EmployeeMemoryNamespaces.UserRelationship(conversation.OrganizationId.ToString("D"), employeeId.ToString("D"), human.Id.ToString("D"), "csweet").Partition;
        if (episode.Partition != expected || episode.Content != message.Content || episode.OccurredAt != message.CreatedAt ||
            episode.Source != new MemorySource(message.Role == ConversationRole.User ? "user" : "assistant", sourceId.ToString("D"), message.Role.ToString()) ||
            !episode.Metadata.TryGetValue("role", out var role) || role != message.Role.ToString() ||
            episode.Checksum != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Content))).ToLowerInvariant() ||
            (message.SenderOrganizationUserId is { } sender && sender != (message.Role == ConversationRole.User ? human.Id : employeeId)) ||
            await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == sourceId, token) ||
            await db.MemorySourceInvalidations.AnyAsync(x => x.SourceMessageId == sourceId, token) ||
            await db.MemoryCaptureOutbox.AnyAsync(x => x.ConversationMessageId == sourceId && x.Status == MemoryCaptureStatus.Completed &&
                x.EpisodeCapturedAt == null && x.LastError != null, token)) return invalid;
        // Missing legacy fingerprints remain unverified; this repair never confirms them.
        // Temporary participant/provider availability is checked at use time, not erased here.
        return new(true, sourceId, conversationId);
    }

    private async Task<int> SuppressAsync(Row row, Evidence evidence, CancellationToken token)
    {
        if (evidence.MessageId is Guid messageId && evidence.ConversationId is Guid conversationId)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MemorySourceInvalidations"("SourceMessageId","PreviousConversationId","ReasonCode","InvalidatedAt")
                    VALUES ({messageId},{conversationId},{"memory_source_reconciled"},{DateTimeOffset.UtcNow})
                    ON CONFLICT("SourceMessageId") DO NOTHING
                """, token);
        // Preserve raw evidence/unknown fields and immutable fingerprints. Ambiguous identity
        // only suppresses this episode; never invent an original message/conversation binding.
        const string affected = """
            payload->'partition'->>'applicationId'='csweet' AND
            (id=@id OR (@message IS NOT NULL AND lower(payload->'source'->>'type') IN ('user','assistant')
                AND lower(payload->'source'->>'id')=@message))
            """;
        await using (var tombstones = Command($"""
            INSERT INTO csweet_memory_suppressions(partition_key,source_type,source_id,episode_id,suppressed_at)
                SELECT partition_key,COALESCE(lower(payload->'source'->>'type'),'unverifiable'),COALESCE(payload->'source'->>'id',id::text),id,CURRENT_TIMESTAMP
                FROM csweet_memory_episodes WHERE {affected} ON CONFLICT(partition_key,episode_id) DO NOTHING
            """))
        {
            Bind(tombstones); await tombstones.ExecuteNonQueryAsync(token);
        }
        await using var suppress = Command($"""
            UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['isSuppressed'],'true'::jsonb)
                WHERE {affected} AND payload->>'isSuppressed' IS DISTINCT FROM 'true'
            """);
        Bind(suppress); return await suppress.ExecuteNonQueryAsync(token);

        void Bind(NpgsqlCommand command)
        {
            command.Parameters.AddWithValue("id", row.Id);
            command.Parameters.AddWithValue("message", NpgsqlTypes.NpgsqlDbType.Text, (object?)evidence.MessageId?.ToString("D") ?? DBNull.Value);
        }
    }

    private NpgsqlCommand Command(string sql) => new(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
        (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
}
