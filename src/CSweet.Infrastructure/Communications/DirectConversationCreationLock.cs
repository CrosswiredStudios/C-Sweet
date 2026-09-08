using System.Security.Cryptography;
using System.Text;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CSweet.Infrastructure.Communications;

// Serialize participant-pair creation both within a host and across PostgreSQL hosts.
internal sealed class DirectConversationCreationLock(SemaphoreSlim gate, IDbContextTransaction? transaction) : IAsyncDisposable
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public static async Task<DirectConversationCreationLock> AcquireAsync(CSweetDbContext db, Guid organizationId,
        IEnumerable<Guid> participants, CancellationToken token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{organizationId:N}:" + string.Join(":", participants.Order().Select(x => x.ToString("N")))));
        var gate = Gates[hash[0]];
        await gate.WaitAsync(token);
        IDbContextTransaction? transaction = null;
        try
        {
            if (db.Database.IsNpgsql())
            {
                if (db.Database.CurrentTransaction is null)
                    transaction = await db.Database.BeginTransactionAsync(token);
                var key = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(hash);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", token);
            }
            return new(gate, transaction);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync();
            gate.Release();
            throw;
        }
    }
    public async Task CommitAsync(CancellationToken token)
    {
        if (transaction is not null) await transaction.CommitAsync(token);
    }
    public async ValueTask DisposeAsync()
    {
        try { if (transaction is not null) await transaction.DisposeAsync(); }
        finally { gate.Release(); }
    }
}
