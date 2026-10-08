using System.Text.Json;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CSweet.Memory.Evaluate;

// Controlled ablation, not a reconstruction or latency claim about an old release.
// Uses the same literal terms and existing indexes, changing OR to AND for episodic evidence only.
internal sealed class EpisodicAndBaseline(string provider, string location)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<MemoryCandidate>> SearchAsync(MemoryPartition partition, EvaluationScenario scenario,
        int limit, CancellationToken token)
    {
        var terms = MemoryLexicalQuery.Parse(scenario.Query).Terms;
        if (terms.Count == 0) return [];
        var results = new List<MemoryCandidate>();
        if (provider == "postgres")
        {
            await using var connection = new NpgsqlConnection(location);
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand("""
                SELECT payload::text FROM csweet_memory_episodes
                WHERE partition_key=@partition AND search_vector @@ websearch_to_tsquery('simple',@query)
                ORDER BY ts_rank_cd(search_vector,websearch_to_tsquery('simple',@query)) DESC,id LIMIT @limit
                """, connection);
            command.Parameters.AddWithValue("partition", partition.StorageKey);
            command.Parameters.AddWithValue("query", string.Join(' ', terms.Select(x => $"\"{x}\"")));
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) Add(reader.GetString(0));
        }
        else
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = location, Pooling = false }.ToString());
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT e.payload FROM memory_episodes e JOIN memory_episodes_fts f ON f.id=e.id
                WHERE e.partition_key=$partition AND f.content MATCH $query
                ORDER BY bm25(memory_episodes_fts),e.id LIMIT $limit
                """;
            command.Parameters.AddWithValue("$partition", partition.StorageKey);
            command.Parameters.AddWithValue("$query", string.Join(" AND ", terms.Select(x => $"\"{x}\"")));
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) Add(reader.GetString(0));
        }
        return results;

        void Add(string json)
        {
            var source = JsonSerializer.Deserialize<MemoryEpisode>(json, JsonOptions)!;
            if (!MemoryProvenance.IsCurrent(source, partition, source.Id, scenario.AsOf) ||
                source.Sensitivity > MemoryRecallPolicy.MaximumSensitivity(partition)) return;
            results.Add(new(source.Id, MemoryLayer.Episodic, source.Content, 1, MemoryTrustTier.UnconfirmedUser,
                MemoryConfirmationState.NotRequired, source.Sensitivity, source.OccurredAt, source.ExpiresAt, [source.Id], "episodic-and-ablation"));
        }
    }
}
