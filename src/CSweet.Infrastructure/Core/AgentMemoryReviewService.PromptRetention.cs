using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private async Task<bool> CheckPromptRetentionAsync(Guid organization, Guid actor,
        IReadOnlyList<MemoryRecallDispatchEvidence.ErasureRead> reads, PostgreSqlMemoryStore store, CancellationToken token)
    {
        var inputs = reads.Where(x => x.Binding is not null).SelectMany(x => (x.Prompt ??
            throw new InvalidOperationException("memory_erasure_work_prompt_review_required")).Inputs.Select(i => (Read: x, Input: i)))
            .DistinctBy(x => (x.Read.Binding, x.Input)).ToArray();
        if (inputs.Length > 1024) throw new InvalidOperationException("memory_erasure_scan_limit");
        var held = false; var count = 0; long bytes = 0;
        foreach (var (read, input) in inputs)
        {
            var binding = read.Binding!; var prompt = read.Prompt!;
            if (prompt.Auxiliary is null) throw new InvalidOperationException("memory_erasure_work_prompt_review_required");
            // Attached media has independent resource provenance/retention. A message
            // hold does not establish permission to remove its copied descriptors or bytes.
            if (prompt.Auxiliary.Attachments.Length > 0) throw new InvalidOperationException("memory_erasure_work_attachment_review_required");
            // Frozen direct audience bindings survive conversation deletion. The
            // coordinator separately authorizes the current human relationship.
            if (prompt.Kind != ConversationKind.DirectHumanAgent || prompt.ConversationEmployeeId != binding.EmployeeId ||
                prompt.InitiatorId != actor || input.Role == ConversationRole.User && (input.SenderId ?? prompt.InitiatorId) != actor ||
                input.Role == ConversationRole.Assistant && (input.SenderId ?? prompt.ConversationEmployeeId) != binding.EmployeeId)
                throw new UnauthorizedAccessException();
            var partition = EmployeeMemoryNamespaces.UserRelationship(organization.ToString("D"), binding.EmployeeId.ToString("D"), actor.ToString("D"), "csweet").Partition;
            var current = await store.GetEpisodeAsync(partition, input.Id, token);
            if (current is null) throw new InvalidOperationException("memory_erasure_work_retention_review_required");
            bool Matches(MemoryEpisode? version) => version is not null && version.Content is not null && version.Partition == partition && version.Id == input.Id &&
                version.Source?.Id == input.Id.ToString("D") && version.Source.Type == (input.Role == ConversationRole.User ? "user" : "assistant") &&
                version.OccurredAt == input.CreatedAt && version.Metadata?.GetValueOrDefault("conversationId") == binding.ConversationId.ToString("D") &&
                version.Metadata?.GetValueOrDefault("messageId") == input.Id.ToString("D") &&
                version.Metadata?.GetValueOrDefault("installationId") == binding.InstallationId.ToString("D") &&
                MemoryRecallDispatchEvidence.Hash(version.Content) == input.SourceHash && version.Content.Length >= input.RenderedLength &&
                MemoryRecallDispatchEvidence.Hash(version.Content[..input.RenderedLength]) == input.RenderedHash;
            var matched = Matches(current); long after = 0;
            while (!matched)
            {
                var page = await store.ReadRevisionsAsync(partition, MemoryRecordKind.Episode, input.Id, after, 200, token);
                foreach (var snapshot in page.Items)
                {
                    bytes += System.Text.Encoding.UTF8.GetByteCount(snapshot.PayloadJson);
                    if (++count > 16384 || bytes > 33_554_432) throw new InvalidOperationException("memory_erasure_scan_limit");
                    MemoryEpisode? version;
                    try { version = System.Text.Json.JsonSerializer.Deserialize<MemoryEpisode>(snapshot.PayloadJson, JsonOptions); }
                    catch (System.Text.Json.JsonException)
                    { throw new InvalidOperationException("memory_erasure_work_prompt_review_required"); }
                    if (Matches(version)) { matched = true; break; }
                }
                if (matched || page.NextAfterRevision is null) break;
                if (page.NextAfterRevision <= after) throw new InvalidOperationException("memory_erasure_scan_limit");
                after = page.NextAfterRevision.Value;
            }
            if (!matched) throw new InvalidOperationException("memory_erasure_work_prompt_review_required");
            held |= current.LegalHold;
        }
        return held;
    }
}
