using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.Infrastructure.Core;

/// <summary>Reads persistent, content-free authority generations for a server-resolved audience.</summary>
public static class MemoryAccessAuthorityEvidence
{
    public sealed record Generation(string Kind, Guid Id, long Revision);

    public static async Task<Generation[]> ReadAsync(CSweetDbContext db,
        IEnumerable<(string Kind, Guid Id)> references, CancellationToken token)
    {
        var keys = references.Distinct().OrderBy(x => x.Kind).ThenBy(x => x.Id).Take(1025).ToArray();
        if (keys.Length is < 1 or > 1024 || keys.Any(x => x.Id == Guid.Empty) || !db.Database.IsNpgsql())
            throw new UnauthorizedAccessException();
        var generations = await db.Database.SqlQueryRaw<Generation>("""
            SELECT a."Kind",a."Id",a."Revision" FROM "MemoryAccessAuthority" a
            JOIN unnest(@kinds,@ids) expected(kind,id) ON a."Kind"=expected.kind AND a."Id"=expected.id
            ORDER BY a."Kind",a."Id" LIMIT 1025
            """, new NpgsqlParameter("kinds", keys.Select(x => x.Kind).ToArray()),
                new NpgsqlParameter("ids", keys.Select(x => x.Id).ToArray())).ToArrayAsync(token);
        if (generations.Length != keys.Length || generations.Any(x => x.Revision < 1))
            throw new UnauthorizedAccessException();
        return generations;
    }
}
