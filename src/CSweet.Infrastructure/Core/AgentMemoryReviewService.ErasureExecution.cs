using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private sealed record ErasureExecution(MemoryErasureExecutionImpact Impact, MemoryCaptureErasure.Plan Capture,
        MemoryWorkErasure.Plan Work, IReadOnlyList<MemoryRecallDispatchEvidence.ErasureRead> Reads,
        IReadOnlyList<MemoryPartition> Audiences);

    private async Task<ErasureExecution> ReadErasureExecutionAsync(Guid organization, Guid employee, Guid user, Guid actor,
        MemoryErasurePreview inventory, PostgreSqlMemoryStore store, MemoryWorkErasure work, MemoryCaptureErasure capture,
        CancellationToken token, IReadOnlyList<MemoryCaptureErasure.Source>? additionalSources = null)
    {
        var sources = (await ReadErasureCaptureSourcesAsync(organization, inventory, token))
            .Concat(additionalSources ?? []).Distinct().OrderBy(x => x.MessageId).ToArray();
        foreach (var source in sources)
        {
            if (source.HumanId != actor || await MemoryManagerAuthorization.RequireAsync(db, organization, source.EmployeeId, user, true, token, true) != actor)
                throw new UnauthorizedAccessException();
        }
        var captures = await capture.PrepareAsync(organization, sources, token);
        var captureRetention = await capture.CheckRetentionAsync(captures, token);
        if (captureRetention is not null && captureRetention != "memory_legal_hold_prevents_deletion")
            throw new InvalidOperationException(captureRetention);
        var reads = await new MemoryErasureReadInventory(db).ReadAsync(organization, inventory.Targets.ToArray(), token);
        // A chat prompt can include the source conversation without selecting a recall
        // candidate. Include those queued payloads as well as explicit read receipts.
        var conversations = sources.Select(x => x.ConversationId).ToHashSet();
        var messageIds = sources.Select(x => x.MessageId).ToHashSet();
        bool IncludesPrompt(MemoryRecallDispatchEvidence.ErasureRead read) => read.Binding is { } binding &&
            (read.Prompt is { } prompt ? prompt.Inputs.Any(x => messageIds.Contains(x.Id)) : conversations.Contains(binding.ConversationId));
        var initialWorks = reads.WorkIds.Concat(reads.Queued.Where(x => IncludesPrompt(x.Read)).Select(x => x.WorkId)).Distinct().ToArray();
        var initialRuntimes = reads.RuntimeIds.Concat(reads.Delivered.Where(x => IncludesPrompt(x.Read))
            .Select(x => x.RuntimeId)).Distinct().ToArray();
        var plan = await work.PrepareAsync(organization, initialWorks, initialRuntimes, token);
        var works = plan.Works.Select(x => x.Id).ToHashSet(); var runtimes = plan.Runtimes.Select(x => x.Id).ToHashSet();
        var queued = reads.Queued.Where(x => works.Contains(x.WorkId)).ToArray();
        var delivered = reads.Delivered.Where(x => runtimes.Contains(x.RuntimeId)).ToArray();
        foreach (var installation in plan.Works.Select(x => x.InstallationId).Concat(plan.Runtimes.Select(x => x.InstallationId)).Distinct())
        {
            var owners = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization &&
                x.AgentInstallationId == installation && x.EmployeeType == EmployeeType.Agent).Select(x => x.Id).Take(2).ToArrayAsync(token);
            if (owners.Length != 1 || await MemoryManagerAuthorization.RequireAsync(db, organization, owners[0], user, true, token, true) != actor)
                throw new UnauthorizedAccessException();
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, owners[0], actor,
                EmployeeMemoryNamespaces.Employee(organization.ToString("D"), owners[0].ToString("D"), "csweet").Partition, token, true);
        }
        if (delivered.Any(x => plan.Runtimes.Single(r => r.Id == x.RuntimeId).InstallationId != x.InstallationId))
            throw new UnauthorizedAccessException();
        var evidence = queued.Select(x => x.Read).Concat(delivered.Select(x => x.Read)).ToArray();
        var audiences = evidence.SelectMany(x => x.Partitions).Distinct().ToArray();
        if (audiences.Length > 64) throw new InvalidOperationException("memory_erasure_scan_limit");
        foreach (var partition in audiences)
        {
            var owner = partition == EmployeeMemoryNamespaces.Organization(organization.ToString("D"), "csweet").Partition ? employee :
                Guid.TryParse(partition.AgentId, out var id) ? id : throw new UnauthorizedAccessException();
            if (await MemoryManagerAuthorization.RequireAsync(db, organization, owner, user, true, token, true) != actor)
                throw new UnauthorizedAccessException();
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, owner, actor, partition, token, true);
        }
        foreach (var read in evidence.Where(x => x.Binding is not null))
        {
            var binding = read.Binding!;
            if (binding.OrganizationId != organization || binding.HumanId != actor ||
                await MemoryManagerAuthorization.RequireAsync(db, organization, binding.EmployeeId, user, true, token, true) != actor ||
                !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == binding.EmployeeId && x.AgentInstallationId == binding.InstallationId, token))
                throw new UnauthorizedAccessException();
            if (read.Prompt is null) throw new InvalidOperationException("memory_erasure_work_prompt_review_required");
        }
        foreach (var read in delivered)
            if (await MemoryManagerAuthorization.RequireAsync(db, organization, read.EmployeeId, user, true, token, true) != actor ||
                !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == read.EmployeeId && x.AgentInstallationId == read.InstallationId, token))
                throw new UnauthorizedAccessException();
        // Runtime expansion may discover ordinary operational work with no complete
        // input audience receipt. Do not treat employee ownership as permission to erase
        // another human's private prompt or an independently retained work artifact.
        var unknownWork = works.Except(queued.Select(x => x.WorkId)).ToArray();
        if (unknownWork.Length > 0 && await db.AgentWorkItems.AnyAsync(x => unknownWork.Contains(x.Id) && x.MemoryErasedAt == null, token))
            throw new InvalidOperationException("memory_erasure_work_audience_review_required");
        var sourceReferences = evidence.SelectMany(x => x.References).Where(x => x.Kind == MemoryErasureKind.Episode).Distinct().ToArray();
        if (sourceReferences.Length > 1024) throw new InvalidOperationException("memory_erasure_scan_limit");
        var held = captureRetention == "memory_legal_hold_prevents_deletion";
        held |= await CheckPromptRetentionAsync(organization, actor, evidence, store, token);
        foreach (var source in sourceReferences)
        {
            // Read current inherited retention, rather than treating an old held snapshot
            // as permanently held after a later authorized release. Missing live retention
            // state requires review; a stale read receipt alone cannot authorize erasure.
            var episode = await store.GetEpisodeAsync(source.Partition, source.Id, token);
            if (episode is null) throw new InvalidOperationException("memory_erasure_work_retention_review_required");
            held |= episode.LegalHold;
        }
        var runtimeIds = runtimes.ToArray();
        var active = await db.AgentRuntimeInstances.AsNoTracking().Where(x => runtimeIds.Contains(x.Id)).Select(x => x.Status).ToListAsync(token);
        return new(new(sources.Length, captures.Jobs.Count, works.Count, runtimes.Count,
            active.Count(CSweet.Domain.Setup.AgentRuntimeInstance.IsActive), held ? "memory_legal_hold_prevents_deletion" : null),
            captures, plan, evidence, audiences);
    }

    private async Task<IReadOnlyList<MemoryCaptureErasure.Source>> ReadErasureCaptureSourcesAsync(Guid organization, MemoryErasurePreview inventory, CancellationToken token)
    {
        var targets = inventory.Targets.Where(x => x.Kind == MemoryErasureKind.Episode).ToHashSet();
        var ids = targets.Select(x => x.Id).Distinct().ToArray();
        var episodes = new List<MemoryEpisode>(); long bytes = 0;
        await using (var command = Command("""
            SELECT payload::text FROM csweet_memory_episodes WHERE id=ANY(@ids)
            UNION ALL SELECT payload::text FROM csweet_memory_revisions WHERE kind=0 AND record_id=ANY(@ids)
            LIMIT 16385
            """))
        {
            command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var payload = reader.GetString(0); bytes += System.Text.Encoding.UTF8.GetByteCount(payload);
                if (episodes.Count >= 16384 || bytes > 33_554_432) throw new InvalidOperationException("memory_erasure_scan_limit");
                var episode = JsonSerializer.Deserialize<MemoryEpisode>(payload, JsonOptions) ?? throw new InvalidOperationException("memory_erasure_source_review_required");
                if (!targets.Contains(new(MemoryErasureKind.Episode, episode.Id, episode.Partition)))
                    throw new InvalidOperationException("memory_erasure_source_review_required");
                episodes.Add(episode);
            }
        }
        var result = new HashSet<MemoryCaptureErasure.Source>();
        foreach (var episode in episodes.DistinctBy(x => (x.Id, x.Partition, x.Source, x.SourceFingerprint,
                     MetadataGuid(x, "messageId"), MetadataGuid(x, "conversationId"))))
        {
            if (episode.TransferEvidence is not null && episode.Source.Type == "knowledge-transfer") continue;
            if (!Guid.TryParse(episode.Source.Id, out var sourceId)) throw new InvalidOperationException("memory_erasure_source_review_required");
            if (MetadataGuid(episode, "conversationId") is not { } conversationId || MetadataGuid(episode, "messageId") != sourceId)
            {
                // Human correction episodes are backed by immutable review operations,
                // not conversation capture jobs. Generic proposal ingestion is separate.
                if (episode.Source.Type == "user" && Guid.TryParse(episode.Source.Author, out var reviewer) &&
                    await db.MemoryReviewReceipts.AnyAsync(x => x.OrganizationId == organization && x.OperationId == sourceId &&
                        x.ActorOrganizationUserId == reviewer, token)) continue;
                throw new InvalidOperationException("memory_erasure_source_review_required");
            }
            if (episode.Source.Type is not ("user" or "assistant")) throw new InvalidOperationException("memory_erasure_source_review_required");
            var conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == conversationId, token);
            Guid employee; Guid human;
            if (conversation is not null)
            {
                if (conversation.OrganizationId != organization || conversation.AgentOrganizationUserId is not { } owner)
                    throw new UnauthorizedAccessException();
                employee = owner; human = conversation.InitiatedByOrganizationUserId;
                if (episode.Id == sourceId && episode.Partition.UserId is not null &&
                    (episode.Partition.AgentId != employee.ToString("D") || episode.Partition.UserId != human.ToString("D")))
                    throw new UnauthorizedAccessException();
            }
            else
            {
                var historical = episodes.Where(x => x.Id == sourceId && MetadataGuid(x, "messageId") == sourceId &&
                    MetadataGuid(x, "conversationId") == conversationId && MemorySourceIntegrity.IsVerified(x) &&
                    x.Partition.TenantId == organization.ToString("D") && x.Partition.ApplicationId == "csweet" && x.Partition.UserId is not null)
                    .Select(x => (x.Partition.AgentId, x.Partition.UserId)).Distinct().Take(2).ToArray();
                if (historical.Length != 1 || !Guid.TryParse(historical[0].AgentId, out employee) || !Guid.TryParse(historical[0].UserId, out human))
                    throw new InvalidOperationException("memory_erasure_source_review_required");
            }
            result.Add(new(sourceId, conversationId, employee, human));
            if (result.Count > 1024) throw new InvalidOperationException("memory_erasure_scan_limit");
        }
        return result.OrderBy(x => x.MessageId).ToArray();
    }

    private static Guid? MetadataGuid(MemoryEpisode episode, string key) => episode.Metadata?.TryGetValue(key, out var text) == true &&
        Guid.TryParse(text, out var id) && id != Guid.Empty ? id : null;
}
