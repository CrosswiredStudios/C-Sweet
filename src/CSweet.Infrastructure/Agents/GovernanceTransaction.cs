using System.Security.Cryptography;
using System.Text;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CSweet.Infrastructure.Agents;

/// <summary>Database-wide admission lock in PostgreSQL; bounded striped locks for in-memory fixtures.</summary>
internal sealed class GovernanceTransaction : IAsyncDisposable
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private IDbContextTransaction? _transaction;
    private bool _ownsTransaction;
    private SemaphoreSlim? _gate;
    public static async Task<GovernanceTransaction> OpenAsync(CSweetDbContext db, string scope, CancellationToken token)
    {
        var lease = new GovernanceTransaction();
        var hash = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("agent-governance:" + scope)), 0);
        try
        {
            if (db.Database.IsRelational())
            {
                if (db.Database.CurrentTransaction is null)
                {
                    lease._transaction = await db.Database.BeginTransactionAsync(token);
                    lease._ownsTransaction = true;
                }
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({hash})", token);
            }
            else
            {
                var gate = Gates[(int)((ulong)hash % (uint)Gates.Length)];
                await gate.WaitAsync(token);
                lease._gate = gate;
            }
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }
    // Only commit a transaction this lease opened; an ambient one belongs to the caller.
    public async Task CommitAsync(CancellationToken token) { if (_ownsTransaction && _transaction is not null) await _transaction.CommitAsync(token); }
    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null) await _transaction.DisposeAsync();
        _gate?.Release();
    }
}
