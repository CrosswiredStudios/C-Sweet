using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

/// <summary>Server-owned evidence for recalled context carried by a durable chat work item.</summary>
public sealed partial class MemoryRecallDispatchEvidence(CSweetDbContext db)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Binding(Guid TurnId, int Attempt, Guid OrganizationId, Guid ConversationId, Guid MessageId,
        Guid EmployeeId, Guid HumanId, Guid InstallationId, string AuthorityHash);
    internal sealed record Root(MemoryPartition Partition, MemoryRecordKind Kind, Guid Id, string ContentHash, Guid[] Sources, string Format = "candidate", MemorySensitivity? MinimumSensitivity = null);
    internal sealed record Record(MemoryPartition Partition, MemoryRecordKind Kind, Guid Id, long Revision, string Hash)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? SharedAudienceJson { get; init; }
    }
    internal sealed record Receipt(int Version, Binding Binding, string? ContextHash, Root[] Roots, Record[] Records, string? PayloadHash = null)
    {
        public PromptEvidence? Prompt { get; init; }
    }
    private sealed record Row(Record Record, JsonElement Payload);
    private static readonly string[] Tables = ["episodes", "entities", "claims", "edges", "blocks", "procedures", "embeddings"];

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Serialize(Receipt receipt) => JsonSerializer.Serialize(receipt, Json);

    internal static string BindPayload(string json, string payloadHash, string organization, Guid installation, string? sourceType, string? sourceId)
    {
        var receipt = ReadQueuedReceipt(json);
        if (receipt.Version != 2 || !ValidPrompt(receipt.Binding, receipt.Prompt) || receipt.PayloadHash is not null || sourceType != "chat-turn" ||
            receipt.Binding.TurnId.ToString("D") != sourceId || receipt.Binding.OrganizationId.ToString("D") != organization ||
            receipt.Binding.InstallationId != installation) throw Denied();
        return Serialize(receipt with { PayloadHash = payloadHash });
    }

    internal async Task<Binding> ReadBindingAsync(Guid turnId, CancellationToken token, bool retained = false)
    {
        var turn = await db.ChatTurns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == turnId, token) ?? throw Denied();
        if (!retained && turn.Status is (ChatTurnStatus.Cancelled or ChatTurnStatus.Failed or ChatTurnStatus.Completed or ChatTurnStatus.CompletedWithWarnings))
            throw Denied();
        var conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == turn.ConversationId &&
            x.OrganizationId == turn.OrganizationId && x.ArchivedAt == null && x.MergedIntoConversationId == null, token) ?? throw Denied();
        var message = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == turn.UserMessageId &&
            x.ConversationId == conversation.Id, token) ?? throw Denied();
        var senderId = message.SenderOrganizationUserId ?? conversation.InitiatedByOrganizationUserId;
        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == turn.TargetAgentOrganizationUserId &&
            x.OrganizationId == turn.OrganizationId && x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Agent, token) ?? throw Denied();
        var sender = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == senderId &&
            x.OrganizationId == turn.OrganizationId && x.IsActive && x.ArchivedAt == null, token) ?? throw Denied();
        if (employee.AgentInstallationId is not { } installation || !await db.AgentInstallations.AsNoTracking().AnyAsync(x =>
            x.Id == installation && x.BusinessId == turn.OrganizationId.ToString() && x.IsEnabled, token)) throw Denied();
        var members = await db.TeamMemberships.AsNoTracking().Where(x => x.OrganizationId == turn.OrganizationId &&
            (x.OrganizationUserId == employee.Id || x.OrganizationUserId == sender.Id) && x.EndedAt == null)
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.TeamId, x.OrganizationUserId, x.TeamRoleId,
                x.Team!.Revision, x.Team.ArchivedAt, x.Team.LeadOrganizationUserId }).Take(129).ToListAsync(token);
        if (members.Count > 128) throw Denied();
        var roleIds = new[] { employee.RoleId, sender.RoleId }.Concat(members.Select(x => x.TeamRoleId)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var roles = await db.CoreRoles.AsNoTracking().Where(x => roleIds.Contains(x.Id)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.OrganizationId, x.AuthorityLevel, x.ResponsibilitiesJson, x.UpdatedAt }).ToListAsync(token);
        var authority = Hash(JsonSerializer.Serialize(new { conversation.Kind, conversation.AgentOrganizationUserId, conversation.InitiatedByOrganizationUserId,
            conversation.TeamId, conversation.WorkstreamId, message.Role, message.SenderOrganizationUserId, message.Content, message.CreatedAt,
            employee = new { employee.Id, employee.Revision, employee.RoleId, employee.ReportsToOrganizationUserId, employee.PermissionLevel },
            sender = new { sender.Id, sender.ApplicationUserId, sender.Revision, sender.EmployeeType, sender.RoleId, sender.ReportsToOrganizationUserId, sender.PermissionLevel }, members, roles }, Json));
        return new(turn.Id, turn.Attempt, turn.OrganizationId, conversation.Id, message.Id, employee.Id, sender.Id, installation, authority);
    }

    internal async Task<(Root[] Roots, Record[] Records)> CaptureAsync(
        IReadOnlyList<(MemoryPartition Partition, MemoryCandidate Candidate)> selected, CancellationToken token)
    {
        var roots = selected.Select(x => new Root(x.Partition, x.Candidate.Layer switch
        {
            MemoryLayer.Episodic => MemoryRecordKind.Episode, MemoryLayer.Core => MemoryRecordKind.Block,
            MemoryLayer.Procedural => MemoryRecordKind.Procedure,
            MemoryLayer.Semantic => x.Candidate.RetrievalChannel == "graph" ? MemoryRecordKind.Edge : MemoryRecordKind.Claim,
            _ => throw Denied()
        }, x.Candidate.Id, Hash(x.Candidate.Content), x.Candidate.EpisodeIds.Distinct().Order().ToArray())).ToArray();
        return (roots, await ReadEvidenceAsync(roots, token));
    }

    public Task AuthorizeWorkAsync(AgentWorkItem work, string? employeeId, CancellationToken token) =>
        AuthorizeWorkCoreAsync(work, employeeId, token, retained: false);

    private async Task AuthorizeWorkCoreAsync(AgentWorkItem work, string? employeeId, CancellationToken token, bool retained,
        bool preserveInfrastructureFailure = false)
    {
        // Legacy chat work may contain unreceipted context. It must be recreated by the turn worker.
        if (work.MemoryRecallReceiptJson is null)
        {
            if (work.SourceType == "chat-turn") throw Denied();
            return;
        }
        try
        {
            if (work.MemoryRecallReceiptJson.Length > 131072) throw Denied();
            var receipt = ReadQueuedReceipt(work.MemoryRecallReceiptJson);
            if (receipt.Version != 2 || !ValidPrompt(receipt.Binding, receipt.Prompt) || receipt.PayloadHash != work.PayloadHash || work.SourceType != "chat-turn" ||
                receipt.Binding.TurnId.ToString("D") != work.SourceId || receipt.Binding.OrganizationId.ToString("D") != work.OrganizationId ||
                receipt.Binding.InstallationId != work.AgentInstallationId || receipt.Binding.EmployeeId.ToString("D") != employeeId ||
                receipt.Roots.Length > 8 || receipt.Records.Length > 512 ||
                (receipt.Roots.Length == 0) != (receipt.ContextHash is null) ||
                (receipt.Roots.Length == 0 && receipt.Records.Length != 0)) throw Denied();
            var binding = await ReadBindingAsync(receipt.Binding.TurnId, token, retained);
            if (binding != receipt.Binding) throw Denied();
            await ValidatePromptAsync(binding, receipt.Prompt!, token);
            if (receipt.Roots.Length == 0) return;
            await RequireAudienceAsync(binding, receipt.Roots, token);
            var current = await ReadEvidenceAsync(receipt.Roots, token);
            await RequireSharedAudienceAsync(binding, current, token);
            if (!current.SequenceEqual(receipt.Records) || binding != await ReadBindingAsync(binding.TurnId, token, retained)) throw Denied();
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not ProviderDispatchDeniedException &&
            (!preserveInfrastructureFailure || exception is JsonException or FormatException or NullReferenceException or KeyNotFoundException or ArgumentException))
        { throw Denied(); }
    }

    internal async Task RequireAudienceAsync(Binding binding, Root[] roots, CancellationToken token)
    {
        var org = binding.OrganizationId.ToString("D"); var employee = binding.EmployeeId.ToString("D"); var human = binding.HumanId.ToString("D");
        var partitions = new[] { EmployeeMemoryNamespaces.Employee(org, employee, "csweet").Partition,
            EmployeeMemoryNamespaces.UserRelationship(org, employee, human, "csweet").Partition, EmployeeMemoryNamespaces.Organization(org, "csweet").Partition };
        if (roots.Any(x => !partitions.Contains(x.Partition)) || !await db.CoreConversations.AsNoTracking().AnyAsync(x =>
            x.Id == binding.ConversationId && x.Kind == ConversationKind.DirectHumanAgent && x.AgentOrganizationUserId == binding.EmployeeId &&
            x.InitiatedByOrganizationUserId == binding.HumanId && db.CoreOrganizationUsers.Any(u => u.Id == binding.HumanId && u.EmployeeType == EmployeeType.Human), token)) throw Denied();
    }

    private async Task<Record[]> ReadEvidenceAsync(Root[] roots, CancellationToken token, int maximumRoots = 8)
    {
        if (!db.Database.IsNpgsql() || (roots.Length < 1 || roots.Length > maximumRoots) || roots.Any(x => x.Sources.Length > 512)) throw Denied();
        // Queue delivery owns a transaction so that the lease and evidence commit together.
        await using var ownedTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token) : null;
        var transaction = db.Database.CurrentTransaction ?? throw Denied();
        await using var memory = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
        var rows = new Dictionary<(MemoryPartition Partition, MemoryRecordKind Kind, Guid Id), Row>();
        var pending = new Queue<(MemoryPartition Partition, MemoryRecordKind Kind, Guid Id)>();
        foreach (var root in roots)
        {
            pending.Enqueue((root.Partition, root.Kind, root.Id));
            foreach (var source in root.Sources) pending.Enqueue((root.Partition, MemoryRecordKind.Episode, source));
        }
        var characters = 0;
        while (pending.TryDequeue(out var key))
        {
            if (rows.ContainsKey(key)) continue;
            if (rows.Count >= 512 || !Enum.IsDefined(key.Kind)) throw Denied();
            var table = "csweet_memory_" + Tables[(int)key.Kind];
            await using var command = new NpgsqlCommand($"""
                SELECT r.payload::text,(SELECT max(revision) FROM csweet_memory_revisions v
                    WHERE v.partition_key=r.partition_key AND v.kind=@kind AND v.record_id=r.id)
                FROM {table} r WHERE partition_key=@partition AND id=@id
                    AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q
                        WHERE q.table_name=@table AND q.record_id=r.id::text AND q.disposition='Quarantine')
                """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
            command.Parameters.AddWithValue("partition", key.Partition.StorageKey); command.Parameters.AddWithValue("id", key.Id);
            command.Parameters.AddWithValue("kind", (int)key.Kind); command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.IsDBNull(1)) throw Denied("read.source-missing-or-quarantined");
            var payload = reader.GetString(0); var revision = reader.GetInt64(1); characters += payload.Length;
            await reader.DisposeAsync();
            if (characters > 1_048_576) throw Denied();
            using var document = JsonDocument.Parse(payload); var value = document.RootElement.Clone();
            if (value.GetProperty("id").GetGuid() != key.Id || value.GetProperty("partition").Deserialize<MemoryPartition>(Json) != key.Partition) throw Denied();
            var episodeEvidence = key.Kind == MemoryRecordKind.Episode ? value.Deserialize<MemoryEpisode>(Json) : null;
            var shared = episodeEvidence is null ? null : MemorySharedAudiences.Required(episodeEvidence);
            rows.Add(key, new(new(key.Partition, key.Kind, key.Id, revision, Hash(payload))
                { SharedAudienceJson = shared is null ? null : JsonSerializer.Serialize(MemorySharedAudiences.Merge(shared), Json) }, value));
            if (value.TryGetProperty("sourceEpisodeIds", out var sources))
            {
                if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > MemoryProvenance.MaximumSourceEpisodes) throw Denied();
                foreach (var source in sources.EnumerateArray()) pending.Enqueue((key.Partition, MemoryRecordKind.Episode, source.GetGuid()));
            }
            foreach (var property in key.Kind switch
            {
                MemoryRecordKind.Claim => new[] { "episodeId", "subjectEntityId", "objectEntityId" },
                MemoryRecordKind.Edge => new[] { "episodeId", "fromEntityId", "toEntityId" },
                MemoryRecordKind.Procedure => new[] { "episodeId" }, _ => Array.Empty<string>()
            })
                if (value.TryGetProperty(property, out var id) && id.ValueKind != JsonValueKind.Null)
                    pending.Enqueue((key.Partition, property == "episodeId" ? MemoryRecordKind.Episode : MemoryRecordKind.Entity, id.GetGuid()));
            if (key.Kind == MemoryRecordKind.Embedding)
            {
                var embedding = value.Deserialize<MemoryEmbedding>(Json) ?? throw Denied();
                pending.Enqueue((key.Partition, await ReadKindAsync(key.Partition, embedding.MemoryId, embedding.Layer, token), embedding.MemoryId));
            }
            if (key.Kind == MemoryRecordKind.Episode && value.Deserialize<MemoryEpisode>(Json)?.TransferEvidence is { } transfer)
                foreach (var reference in transfer.Records) pending.Enqueue((reference.Partition, reference.Kind, reference.Id));
            if (episodeEvidence?.CorrectionEvidence is { } correction)
            {
                if (correction.Sources is not { Count: > 0 and <= MemoryProvenance.MaximumSourceEpisodes }) throw Denied();
                foreach (var reference in correction.Sources) pending.Enqueue((key.Partition, MemoryRecordKind.Episode, reference.EpisodeId));
            }
        }
        var resolved = new Dictionary<(MemoryPartition, Guid), MemoryEpisode>();
        foreach (var row in rows.Values.Where(x => x.Record.Kind == MemoryRecordKind.Episode))
        {
            var episode = await memory.GetEpisodeAsync(row.Record.Partition, row.Record.Id, token) ?? throw Denied();
            if (!MemoryProvenance.IsCurrent(episode, row.Record.Partition, row.Record.Id, DateTimeOffset.UtcNow)) throw Denied();
            await RequireCurrentConversationSourceAsync(episode, token);
            resolved.Add((row.Record.Partition, row.Record.Id), episode);
        }
        foreach (var group in roots.GroupBy(x => x.Partition))
        {
            List<T> Records<T>(MemoryRecordKind kind) => rows.Values.Where(x => x.Record.Partition == group.Key && x.Record.Kind == kind)
                .Select(x => x.Payload.Deserialize<T>(Json)!).ToList();
            var export = new MemoryExport("1.0", resolved.Where(x => x.Key.Item1 == group.Key).Select(x => x.Value).ToArray(),
                Records<MemoryEntity>(MemoryRecordKind.Entity), Records<MemoryClaim>(MemoryRecordKind.Claim), Records<MemoryEdge>(MemoryRecordKind.Edge),
                Records<MemoryBlock>(MemoryRecordKind.Block), Records<ProceduralMemory>(MemoryRecordKind.Procedure), Records<MemoryEmbedding>(MemoryRecordKind.Embedding));
            var projected = MemoryReadProjection.Create(export, group.Key, MemoryRecallPolicy.MaximumSensitivity(group.Key), DateTimeOffset.UtcNow);
            var items = MemoryReadProjection.TransferItems(projected, group.Key).ToArray();
            foreach (var root in group)
            {
                string content;
                if (root.Format == "record")
                {
                    object record = root.Kind switch
                    {
                        MemoryRecordKind.Episode => (object?)projected.Episodes.SingleOrDefault(x => x.Id == root.Id),
                        MemoryRecordKind.Entity => projected.Entities.SingleOrDefault(x => x.Id == root.Id),
                        MemoryRecordKind.Claim => projected.Claims.SingleOrDefault(x => x.Id == root.Id),
                        MemoryRecordKind.Edge => projected.Edges.SingleOrDefault(x => x.Id == root.Id),
                        MemoryRecordKind.Block => projected.Blocks.SingleOrDefault(x => x.Id == root.Id),
                        MemoryRecordKind.Procedure => projected.Procedures.SingleOrDefault(x => x.Id == root.Id),
                        MemoryRecordKind.Embedding => projected.Embeddings?.SingleOrDefault(x => x.Id == root.Id), _ => null
                    } ?? throw Denied("read.source-ineligible");
                    content = JsonSerializer.Serialize(record, record.GetType(), Json);
                }
                else
                {
                    var item = items.SingleOrDefault(x => x.MemoryId == root.Id) ?? throw Denied("read.source-ineligible");
                    if (root.Format == "transfer")
                    {
                        item = item with { Sensitivity = MemoryProvenance.Maximum(item.Sensitivity, root.MinimumSensitivity ?? throw Denied()) };
                        if (item.Sensitivity > MemoryRecallPolicy.MaximumSensitivity(group.Key)) throw Denied();
                        content = JsonSerializer.Serialize(item, Json);
                    }
                    else if (root.Format == "candidate")
                        content = root.Kind == MemoryRecordKind.Procedure ? projected.Procedures.Single(x => x.Id == root.Id).Procedure : item.Content;
                    else throw Denied();
                }
                if (Hash(content) != root.ContentHash) throw Denied("read.source-content-changed");
                if (root.Sources.Any(id => !resolved.TryGetValue((group.Key, id), out var episode) ||
                    episode.Sensitivity > MemoryRecallPolicy.MaximumSensitivity(group.Key))) throw Denied("read.source-sensitivity-changed");
            }
        }
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(token);
        return rows.Values.Select(x => x.Record).OrderBy(x => x.Partition.StorageKey, StringComparer.Ordinal).ThenBy(x => x.Kind).ThenBy(x => x.Id).ToArray();
    }

    private async Task RequireCurrentConversationSourceAsync(MemoryEpisode episode, CancellationToken token)
    {
        // Other established source types are governed by their library provenance/transfer checks.
        if (episode.Metadata?.TryGetValue("conversationId", out var raw) != true) return;
        if (!Guid.TryParse(raw, out var conversationId)) throw Denied();
        var message = await db.CoreConversationMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == episode.Id && x.ConversationId == conversationId, token) ?? throw Denied();
        var conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == conversationId &&
            x.Kind == ConversationKind.DirectHumanAgent && x.ArchivedAt == null && x.MergedIntoConversationId == null, token) ?? throw Denied();
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == conversation.AgentOrganizationUserId &&
            x.OrganizationId == conversation.OrganizationId && x.EmployeeType == EmployeeType.Agent && x.IsActive && x.ArchivedAt == null, token) ?? throw Denied();
        if (episode.Partition != EmployeeMemoryNamespaces.UserRelationship(conversation.OrganizationId.ToString("D"), owner.Id.ToString("D"),
                conversation.InitiatedByOrganizationUserId.ToString("D"), "csweet").Partition || episode.Content != message.Content ||
            episode.Checksum != Hash(message.Content).ToLowerInvariant() || episode.OccurredAt != message.CreatedAt ||
            message.Role is not (ConversationRole.User or ConversationRole.Assistant) ||
            episode.Source != new MemorySource(message.Role == ConversationRole.User ? "user" : "assistant", message.Id.ToString("D"), message.Role.ToString()) ||
            (message.SenderOrganizationUserId is { } sender && sender != (message.Role == ConversationRole.User ? conversation.InitiatedByOrganizationUserId : owner.Id)) ||
            !episode.Metadata.TryGetValue("installationId", out var installation) || installation != owner.AgentInstallationId?.ToString("D") ||
            !await db.AgentInstallations.AnyAsync(x => x.Id == owner.AgentInstallationId && x.IsEnabled && x.BusinessId == conversation.OrganizationId.ToString(), token) ||
            !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == conversation.InitiatedByOrganizationUserId && x.OrganizationId == conversation.OrganizationId &&
                x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, token) ||
            await db.MemoryCaptureExclusions.AnyAsync(x => x.SourceMessageId == message.Id, token) ||
            await db.MemorySourceInvalidations.AnyAsync(x => x.SourceMessageId == message.Id, token) ||
            await db.MemoryCaptureOutbox.AnyAsync(x => x.ConversationMessageId == message.Id && x.Status == MemoryCaptureStatus.Completed &&
                x.EpisodeCapturedAt == null && x.LastError != null, token)) throw Denied();
    }

    private static ProviderDispatchDeniedException Denied(string? validationCode = null)
    {
        var error = new ProviderDispatchDeniedException();
        if (validationCode is not null) error.Data["memory.validation"] = validationCode;
        return error;
    }
}
