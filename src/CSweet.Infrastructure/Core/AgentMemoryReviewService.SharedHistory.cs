using System.Text.Json;
using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private async Task RequireSharedHistorySourcesAsync(PostgreSqlMemoryStore store, Guid organization, Guid employee, Guid user,
        Guid actor, MemoryPartition partition, IReadOnlyList<MemoryRevision> revisions, CancellationToken token)
    {
        var episodes = new Dictionary<Guid, MemoryEpisode>();
        foreach (var revision in revisions.Where(x => x.Kind == MemoryRecordKind.Episode))
        {
            var episode = JsonSerializer.Deserialize<MemoryEpisode>(revision.PayloadJson, JsonOptions) ?? throw new JsonException();
            await RequireSharedReviewSourcesAsync(organization, employee, user, actor, [episode], token);
        }
        var ids = revisions.Where(x => x.Kind != MemoryRecordKind.Episode).SelectMany(x => ProjectHistory(x).Sources)
            .Select(x => x.Id).Distinct().Order().ToArray();
        if (ids.Length > 512) throw new InvalidOperationException("memory_history_source_capacity");
        foreach (var id in ids)
        {
            // A retained first revision preserves audience attribution after deletion. Never
            // infer that an unavailable contributor was unrestricted.
            var source = await store.GetEpisodeAsync(partition, id, token);
            if (source is null)
            {
                var history = await store.ReadRevisionsAsync(partition, MemoryRecordKind.Episode, id, limit: 1, cancellationToken: token);
                if (history.Items.Count == 0) throw new InvalidOperationException("memory_history_source_unavailable");
                source = JsonSerializer.Deserialize<MemoryEpisode>(history.Items[0].PayloadJson, JsonOptions) ?? throw new JsonException();
            }
            if (source.Id != id || source.Partition != partition) throw new InvalidOperationException("memory_history_source_unavailable");
            episodes.Add(id, source);
        }
        await RequireSharedReviewSourcesAsync(organization, employee, user, actor, episodes.Values, token);
    }
}
