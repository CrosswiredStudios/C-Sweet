using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private sealed record LegacyEpisode(MemoryEpisode Episode, bool HasSensitivity, string Payload);
    private sealed record LegacyEvidence(long Revision, string Token, bool Valid, MemorySensitivity Minimum);

    public async Task<MemoryLegacyReviewResponse> GetLegacyEpisodeAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
        await LockLegacyMessageAsync(episodeId, cancellationToken);
        await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
        var legacy = await LockLegacyEpisodeAsync(episodeId, cancellationToken);
        await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, legacy.Episode.Partition, cancellationToken);
        var evidence = await ReadLegacyEvidenceAsync(legacy, organizationId, employeeId, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(episodeId, evidence.Revision, evidence.Token, legacy.Episode.Content, legacy.Episode.Source.Type,
            legacy.HasSensitivity ? legacy.Episode.Sensitivity.ToString() : "Unclassified", evidence.Minimum.ToString(), evidence.Valid);
    }

    public async Task<ReviewMemoryLegacyResponse> ReviewLegacyEpisodeAsync(Guid organizationId, Guid employeeId, Guid episodeId,
        Guid applicationUserId, ReviewMemoryLegacyRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedRevision <= 0 || request.EvidenceToken?.Length != 64 ||
            !Enum.TryParse<MemorySensitivity>(request.Sensitivity, out var sensitivity) || !Enum.IsDefined(sensitivity) ||
            sensitivity.ToString() != request.Sensitivity) throw new ArgumentException("Invalid legacy evidence review.");
        await RequireBackendAsync(cancellationToken);
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString("D") + ":" + request.OperationId.ToString("D")}, 0))", cancellationToken);
            await LockLegacyMessageAsync(episodeId, cancellationToken);
            await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
            var legacy = await LockLegacyEpisodeAsync(episodeId, cancellationToken);
            var episode = legacy.Episode;
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, episode.Partition, cancellationToken);
            var hash = Hash(new { organizationId, employeeId, episodeId, applicationUserId, actor, request });
            var receipt = await db.MemoryReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.OperationId == request.OperationId, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.RecordKind != "Episode" || receipt.RequestHash != hash) throw Changed();
                return LegacyResponse(receipt, true);
            }
            var evidence = await ReadLegacyEvidenceAsync(legacy, organizationId, employeeId, actor, cancellationToken);
            if (evidence.Revision != request.ExpectedRevision || evidence.Token != request.EvidenceToken) throw Changed();
            if (!evidence.Valid || sensitivity < evidence.Minimum) throw new InvalidOperationException("memory_review_source_unavailable");
            var updated = MemorySourceIntegrity.Seal(episode with { Sensitivity = sensitivity });
            // Update only the reviewed policy field and new fingerprint. Preserve unknown legacy payload fields.
            await using (var command = Command("""
                UPDATE csweet_memory_episodes SET payload=jsonb_set(jsonb_set(payload,'{sensitivity}',@sensitivity),'{sourceFingerprint}',@fingerprint)
                WHERE id=@id AND partition_key=@partition AND (payload->>'sourceFingerprint') IS NULL
                """))
            {
                command.Parameters.AddWithValue("id", episodeId); command.Parameters.AddWithValue("partition", episode.Partition.StorageKey);
                command.Parameters.AddWithValue("sensitivity", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(sensitivity, JsonOptions));
                command.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(updated.SourceFingerprint, JsonOptions));
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw Changed();
            }
            receipt = new MemoryReviewReceipt { Id = Guid.NewGuid(), OrganizationId = organizationId, EmployeeId = employeeId,
                RecordKind = "Episode", MemoryId = episodeId, ResultMemoryId = episodeId, OperationId = request.OperationId,
                ActorApplicationUserId = applicationUserId, ActorOrganizationUserId = actor, RequestHash = hash, Action = "verify-source",
                PreviousRevision = evidence.Revision, ResultRevision = await RevisionAsync(episode.Partition, episodeId, cancellationToken, MemoryRecordKind.Episode),
                CreatedAt = clock.GetUtcNow() };
            db.MemoryReviewReceipts.Add(receipt);
            db.QueueAudit(new AuditEventWriteRequest("memory.episode.evidence-reviewed.v1", "Memory", OrganizationId: organizationId,
                EntityType: "MemoryEpisode", EntityId: episodeId, Summary: "A human reviewer established matching legacy conversation evidence.",
                MetadataJson: JsonSerializer.Serialize(new { receipt.Action, receipt.PreviousRevision, receipt.ResultRevision, request.Sensitivity }),
                OccurredAt: receipt.CreatedAt, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actor), EventId: receipt.Id,
                Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return LegacyResponse(receipt, false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task LockLegacyMessageAsync(Guid id, CancellationToken token)
    {
        var conversationId = await db.CoreConversationMessages.AsNoTracking().Where(x => x.Id == id)
            .Select(x => (Guid?)x.ConversationId).SingleOrDefaultAsync(token);
        if (conversationId is null) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreConversations\" WHERE \"Id\"={conversationId.Value} FOR SHARE", token);
        // Acquire before the memory writer barrier: enrichment holds shared message/episode locks
        // before writing derivatives. Waiting on those locks while holding the barrier can deadlock.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreConversationMessages\" WHERE \"Id\"={id} FOR UPDATE", token);
        if (await db.CoreConversationMessages.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.ConversationId).SingleOrDefaultAsync(token) != conversationId)
            throw Changed();
    }

    private async Task<LegacyEpisode> LockLegacyEpisodeAsync(Guid id, CancellationToken token)
    {
        await using var command = Command("""
            SELECT partition_key,payload::text FROM csweet_memory_episodes e WHERE id=@id AND partition_key LIKE 'mp2:%'
                AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q WHERE q.table_name='csweet_memory_episodes'
                    AND q.record_id=e.id::text AND q.disposition='Quarantine') FOR UPDATE
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new KeyNotFoundException();
        var payload = reader.GetString(1);
        var episode = JsonSerializer.Deserialize<MemoryEpisode>(payload, JsonOptions) ?? throw new KeyNotFoundException();
        if (episode.Id != id || episode.Partition.StorageKey != reader.GetString(0)) throw new InvalidOperationException("memory_review_source_unavailable");
        using var document = JsonDocument.Parse(payload);
        return new(episode, document.RootElement.TryGetProperty("sensitivity", out _), payload);
    }

    private async Task<LegacyEvidence> ReadLegacyEvidenceAsync(LegacyEpisode legacy, Guid organizationId, Guid employeeId, Guid actor, CancellationToken token)
    {
        var episode = legacy.Episode;
        // Exclusion writers take FOR UPDATE on this message; hold the shared lock through the review commit.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreConversationMessages\" WHERE \"Id\"={episode.Id} FOR SHARE", token);
        var message = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == episode.Id, token);
        Conversation? conversation = null;
        if (message is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreConversations\" WHERE \"Id\"={message.ConversationId} FOR SHARE", token);
            conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == message.ConversationId, token);
        }
        var installation = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == employeeId).Select(x => x.AgentInstallationId).SingleAsync(token);
        var excluded = await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == episode.Id, token) ||
            await db.MemorySourceInvalidations.AnyAsync(x => x.SourceMessageId == episode.Id, token) ||
            await db.MemoryCaptureOutbox.AnyAsync(x => x.ConversationMessageId == episode.Id && x.Status == MemoryCaptureStatus.Completed &&
                x.EpisodeCapturedAt == null && x.LastError != null, token);
        var minimum = message?.Role == ConversationRole.User ? MemorySensitivity.Personal : MemorySensitivity.Internal;
        if (legacy.HasSensitivity) minimum = MemoryProvenance.Maximum(minimum, episode.Sensitivity);
        bool Metadata(string key, string value) => episode.Metadata?.TryGetValue(key, out var actual) == true && actual == value;
        var valid = !episode.IsSuppressed && episode.SourceFingerprint is null && episode.TransferEvidence is null && !excluded && message is not null && conversation is not null &&
            conversation.OrganizationId == organizationId && conversation.AgentOrganizationUserId == employeeId && conversation.InitiatedByOrganizationUserId == actor &&
            conversation.Kind == ConversationKind.DirectHumanAgent && conversation.ArchivedAt is null && conversation.MergedIntoConversationId is null &&
            episode.Partition == EmployeeMemoryNamespaces.UserRelationship(organizationId.ToString("D"), employeeId.ToString("D"), actor.ToString("D"), "csweet").Partition && episode.Scope == MemoryScope.User &&
            message.Role is ConversationRole.User or ConversationRole.Assistant && episode.Content == message.Content && episode.Content.Length <= 65536 &&
            (message.SenderOrganizationUserId is null || message.SenderOrganizationUserId == (message.Role == ConversationRole.User ? actor : employeeId)) &&
            episode.ContentType == "text/plain" && episode.OccurredAt == message.CreatedAt && episode.OccurredAt <= clock.GetUtcNow() &&
            (episode.ExpiresAt is null || episode.ExpiresAt > clock.GetUtcNow()) &&
            episode.Source == new MemorySource(message.Role == ConversationRole.User ? "user" : "assistant", message.Id.ToString("D"), message.Role.ToString()) &&
            episode.Checksum == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Content))).ToLowerInvariant() &&
            episode.Metadata?.Count == 4 && Metadata("messageId", message.Id.ToString("D")) && Metadata("conversationId", conversation.Id.ToString("D")) &&
            installation.HasValue && Metadata("installationId", installation.Value.ToString("D")) && Metadata("role", message.Role.ToString()) &&
            episode.OperationalReferences is null or { Count: 0 } && (!legacy.HasSensitivity || Enum.IsDefined(episode.Sensitivity));
        var revision = await RevisionAsync(episode.Partition, episode.Id, token, MemoryRecordKind.Episode);
        // Bind the raw payload too: a policy/unknown-field change must invalidate an outstanding preview.
        return new(revision, Hash(new { legacy.Payload, revision, valid, minimum, installation,
            message = message is null ? null : new { message.Id, message.ConversationId, message.Role, message.Content, message.CreatedAt, message.ChatTurnId, message.SenderOrganizationUserId },
            conversation = conversation is null ? null : new { conversation.Id, conversation.OrganizationId, conversation.AgentOrganizationUserId,
                conversation.InitiatedByOrganizationUserId, conversation.Kind, conversation.ArchivedAt, conversation.MergedIntoConversationId } }), valid, minimum);
    }

    private static ReviewMemoryLegacyResponse LegacyResponse(MemoryReviewReceipt receipt, bool replay) =>
        new(receipt.Id, receipt.MemoryId, receipt.ResultRevision, receipt.CreatedAt, replay);
}
