using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Serializes recursive source changes before acquiring individual review record locks.</summary>
internal static class MemoryReviewWriteBarrier
{
    internal static Task AcquireAsync(CSweetDbContext db, CancellationToken token) => db.Database.ExecuteSqlRawAsync("""
        LOCK TABLE csweet_memory_episodes, csweet_memory_entities, csweet_memory_claims,
            csweet_memory_edges, csweet_memory_blocks, csweet_memory_procedures, csweet_memory_transfers
            IN SHARE ROW EXCLUSIVE MODE
        """, token);
}
