using System.Text.Json;
using CSweet.Contracts.Memory;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReextractionService
{
    private sealed record Cursor(Guid Organization, Guid Employee, Guid Actor, DateTimeOffset CreatedAt, Guid Id);
    public sealed class CandidateRow
    {
        public Guid Id { get; set; }
        public Guid Episode { get; set; }
        public int Generation { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public bool Accepted { get; set; }
        public string Audience { get; set; } = "";
    }
    public async Task<MemoryReextractionCandidatePage> ListAsync(Guid organizationId, Guid employeeId, Guid applicationUserId,
        string? cursor = null, int limit = 20, CancellationToken token = default)
    {
        await BackendAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, token, true);
        var keys = await MemoryEpisodeOperatorAuthorization.ReadableKeysAsync(db, organizationId, employeeId, actor, token);
        var position = cursor is null ? null : Decode(cursor, organizationId, employeeId, actor);
        DateTimeOffset? created = position?.CreatedAt; Guid? after = position?.Id;
        var size = Math.Clamp(limit, 1, 100);
        var rows = await db.Database.SqlQuery<CandidateRow>($"""
            SELECT j."Id" AS "Id",j."EpisodeId" AS "Episode",j."InputGeneration" AS "Generation",j."Status" AS "Status",
              j."CreatedAt" AS "CreatedAt",j."AcceptedExtractionJson" IS NOT NULL AS "Accepted",
              CASE WHEN e.payload->'partition'->>'applicationId'<>'csweet' THEN 'Installation-private'
                   WHEN e.payload->'partition'->>'customNamespace' LIKE 'team:%' THEN 'Team'
                   WHEN e.payload->'partition'->>'customNamespace' LIKE 'role:%' THEN 'Role'
                   WHEN e.payload->'partition'->>'customNamespace' LIKE 'relationship:%' THEN 'Private relationship'
                   WHEN e.payload->'partition'->>'customNamespace'='organization' THEN 'Organization' ELSE 'Employee' END AS "Audience"
            FROM "MemoryEpisodeEnrichmentJobs" j JOIN csweet_memory_episodes e ON e.id=j."EpisodeId"
            WHERE j."OrganizationId"={organizationId} AND j."EmployeeId"={employeeId} AND j."SupersededAt" IS NULL AND
              j."Status" IN('Failed','Completed') AND e.partition_key=ANY({keys}) AND
              NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q WHERE q.table_name='csweet_memory_episodes'
                AND q.record_id=e.id::text AND q.disposition='Quarantine') AND
              ({created}::timestamptz IS NULL OR j."CreatedAt"<{created}::timestamptz OR
                j."CreatedAt"={created}::timestamptz AND j."Id"<{after}::uuid)
            ORDER BY j."CreatedAt" DESC,j."Id" DESC LIMIT {size + 1}
            """).ToListAsync(token);
        await transaction.CommitAsync(token);
        var items = rows.Take(size).Select(x => new MemoryReextractionCandidate(x.Id, x.Episode, x.Generation, x.Status,
            x.CreatedAt, x.Accepted, x.Audience)).ToArray();
        return new(items, rows.Count > size ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(organizationId, employeeId, actor, items[^1].CreatedAt, items[^1].JobId))) : null);
    }
    private static Cursor Decode(string value, Guid org, Guid employee, Guid actor)
    {
        try
        {
            if (value.Length > 512) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(value));
            if (cursor is null || cursor.Organization != org || cursor.Employee != employee || cursor.Actor != actor || cursor.Id == Guid.Empty)
                throw new FormatException();
            return cursor;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        { throw new ArgumentException("Invalid replacement-extraction cursor.", nameof(value)); }
    }
}
