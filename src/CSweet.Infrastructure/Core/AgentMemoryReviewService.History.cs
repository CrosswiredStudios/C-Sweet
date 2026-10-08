using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    public async Task<MemoryHistoryPage> ReadHistoryAsync(Guid organizationId, Guid employeeId, string kind, Guid recordId,
        Guid applicationUserId, long afterRevision = 0, int limit = 20, CancellationToken cancellationToken = default)
    {
        var recordKind = kind switch
        {
            "Episode" => MemoryRecordKind.Episode, "Entity" => MemoryRecordKind.Entity, "Claim" => MemoryRecordKind.Claim,
            "Relationship" => MemoryRecordKind.Edge, "Core" => MemoryRecordKind.Block, "Procedure" => MemoryRecordKind.Procedure,
            "Embedding" => MemoryRecordKind.Embedding, _ => throw new ArgumentException("Unsupported history kind.")
        };
        if (recordId == Guid.Empty || afterRevision < 0 || limit is < 1 or > 20) throw new ArgumentException("Invalid history page.");
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
        await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
        var tenant = organizationId.ToString("D"); var employee = employeeId.ToString("D");
        var partitions = new[] { EmployeeMemoryNamespaces.Employee(tenant, employee, "csweet").Partition,
            EmployeeMemoryNamespaces.UserRelationship(tenant, employee, actor.ToString("D"), "csweet").Partition,
            EmployeeMemoryNamespaces.Organization(tenant, "csweet").Partition };
        // History may survive record deletion. Resolve only among explicit, server-owned audience candidates.
        var keys = new List<string>();
        await using (var command = Command("""
            SELECT DISTINCT partition_key FROM csweet_memory_revisions
            WHERE partition_key=ANY(@partitions) AND kind=@kind AND record_id=@id LIMIT 2
            """))
        {
            command.Parameters.AddWithValue("partitions", partitions.Select(x => x.StorageKey).ToArray());
            command.Parameters.AddWithValue("kind", (int)recordKind); command.Parameters.AddWithValue("id", recordId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) keys.Add(reader.GetString(0));
        }
        if (keys.Count != 1) throw new KeyNotFoundException();
        var partition = partitions.Single(x => x.StorageKey == keys[0]);
        await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, partition, cancellationToken);
        await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
        var page = await store.ReadRevisionsAsync(partition, recordKind, recordId, afterRevision, limit, cancellationToken);
        if (page.Items.Count == 0 && afterRevision == 0) throw new KeyNotFoundException();
        var previous = new Dictionary<long, long>();
        if (page.Items.Count != 0)
        {
            await using var command = Command("""
                SELECT coalesce(max(revision),0) FROM csweet_memory_revisions
                WHERE partition_key=@partition AND kind=@kind AND record_id=@id AND revision<@first
                """);
            command.Parameters.AddWithValue("partition", partition.StorageKey); command.Parameters.AddWithValue("kind", (int)recordKind);
            command.Parameters.AddWithValue("id", recordId); command.Parameters.AddWithValue("first", page.Items[0].Revision);
            var prior = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            foreach (var revision in page.Items) { previous[revision.Revision] = prior; prior = revision.Revision; }
        }
        var revisionIds = page.Items.Select(x => x.Revision).ToArray(); var priorIds = previous.Values.Where(x => x > 0).ToArray();
        var receipts = await db.MemoryReviewReceipts.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.RecordKind == recordKind.ToString() &&
            ((x.ResultMemoryId == recordId && revisionIds.Contains(x.ResultRevision)) || (x.MemoryId == recordId && priorIds.Contains(x.PreviousRevision))))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(40).ToListAsync(cancellationToken);
        var reviewerIds = receipts.Select(x => x.ActorOrganizationUserId).Distinct().ToArray();
        var reviewers = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId && reviewerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
        var snapshots = page.Items.Select(revision => ProjectHistory(revision) with
        {
            Reviews = receipts.Where(x => (x.ResultMemoryId == recordId && x.ResultRevision == revision.Revision) ||
                    (x.MemoryId == recordId && x.PreviousRevision == previous[revision.Revision]))
                .Select(x => new MemoryHistoryReview(x.Id, x.Action, x.ActorOrganizationUserId,
                    reviewers.TryGetValue(x.ActorOrganizationUserId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : "Former or unnamed reviewer",
                    x.CreatedAt, x.ResultMemoryId != recordId ? new(kind, x.ResultMemoryId, "Corrected record") :
                        x.MemoryId != recordId ? new(kind, x.MemoryId, "Original record") : null)).ToArray()
        }).ToArray();
        await transaction.CommitAsync(cancellationToken);
        var scope = partition.CustomNamespace == "organization" ? "Organization" : partition.UserId is not null ? "Relationship" : "Employee";
        return new(kind, recordId, scope, snapshots, page.NextAfterRevision);
    }

    private static MemoryHistorySnapshot ProjectHistory(MemoryRevision revision)
    {
        T Read<T>() => JsonSerializer.Deserialize<T>(revision.PayloadJson, JsonOptions) ?? throw new JsonException();
        MemoryHistorySnapshot Snapshot(string title, string content, string? state, string? trust, string? sensitivity,
            DateTimeOffset? from, DateTimeOffset? to, IEnumerable<Guid> sources, IEnumerable<MemoryHistoryLink>? links = null, string? applicability = null) =>
            new(revision.Revision, revision.Operation.ToString(), revision.RecordedAt, title, content, applicability, state, trust, sensitivity, from, to,
                sources.Distinct().Select((id, i) => new MemoryHistoryLink("Episode", id, $"Source {i + 1}")).ToArray(), links?.ToArray() ?? [], []);
        switch (revision.Kind)
        {
            case MemoryRecordKind.Episode:
                var episode = Read<MemoryEpisode>();
                using (var payload = JsonDocument.Parse(revision.PayloadJson))
                {
                    var classification = payload.RootElement.TryGetProperty("sensitivity", out _) ? episode.Sensitivity.ToString() : "Unclassified";
                    return Snapshot(episode.Source.Type, episode.Content, (MemorySourceIntegrity.IsVerified(episode) ? "Evidence established" : "Evidence unverified") +
                        (episode.IsSuppressed ? "; suppressed" : "") +
                        (episode.LegalHold ? "; legal hold" : ""), null, classification, episode.OccurredAt, episode.ExpiresAt, []);
                }
            case MemoryRecordKind.Entity:
                var entity = Read<MemoryEntity>();
                return Snapshot(entity.CanonicalName, $"Type: {entity.Type}\nAliases: {string.Join(", ", entity.Aliases)}", null, null,
                    entity.Sensitivity.ToString(), null, null, entity.SourceEpisodeIds);
            case MemoryRecordKind.Claim:
                var claim = Read<MemoryClaim>();
                var claimLinks = new List<MemoryHistoryLink> { new("Entity", claim.SubjectEntityId, "Subject entity") };
                if (claim.ObjectEntityId is { } objectId) claimLinks.Add(new("Entity", objectId, "Target entity"));
                if (claim.SupersedesClaimId is { } original) claimLinks.Add(new("Claim", original, "Superseded claim"));
                return Snapshot(claim.Predicate, claim.Value ?? "Relationship to the linked target entity", claim.Confirmation.ToString(), claim.Trust.ToString(),
                    claim.Sensitivity.ToString(), claim.ValidFrom, claim.ValidTo, claim.SourceEpisodeIds.Prepend(claim.EpisodeId), claimLinks);
            case MemoryRecordKind.Edge:
                var edge = Read<MemoryEdge>();
                return Snapshot(edge.Relationship, "Relationship between linked entities", null, edge.Trust.ToString(), null, edge.ValidFrom, edge.ValidTo,
                    edge.SourceEpisodeIds.Prepend(edge.EpisodeId), [new("Entity", edge.FromEntityId, "From entity"), new("Entity", edge.ToEntityId, "To entity")]);
            case MemoryRecordKind.Block:
                var block = Read<MemoryBlock>();
                return Snapshot(block.Name, block.Content, $"{block.Confirmation}; {(block.IsPinned ? "Pinned" : "Unpinned")}", block.Trust.ToString(), block.Sensitivity.ToString(), null, null, block.SourceEpisodeIds);
            case MemoryRecordKind.Procedure:
                var procedure = Read<ProceduralMemory>();
                return Snapshot($"{procedure.Name} · Version {procedure.Version}", procedure.Procedure, procedure.Confirmation.ToString(), procedure.Trust.ToString(), null,
                    procedure.ValidFrom, procedure.ValidTo, procedure.SourceEpisodeIds.Prepend(procedure.EpisodeId), applicability: procedure.Applicability);
            case MemoryRecordKind.Embedding:
                var embedding = Read<MemoryEmbedding>();
                return Snapshot("Embedding", $"Model: {embedding.Model ?? "Not recorded"}\nDimensions: {embedding.Vector.Count}", null, null, null, null, null,
                    embedding.Layer == MemoryLayer.Episodic ? [embedding.MemoryId] : []);
            default: throw new ArgumentException("Unsupported history kind.");
        }
    }
}
