using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.RemoteMcp;
using Microsoft.EntityFrameworkCore;
namespace CSweet.Infrastructure.Setup;

/// <summary>Durable encrypted account storage. Construct only after current-user or runtime binding authorization.
/// The caller owns this scoped DbContext; do not share it across concurrent requests.</summary>
public sealed class RemoteMcpAccountRepository(CSweetDbContext db, AccountScope scope) : IAccountRepository
{
    private const string Kind = "remote-mcp-account.v1";
    private string ExternalKey(Guid id) => $"{scope.Owner:N}.{id:N}";
    private IQueryable<PluginOperationalState> Records(Guid id) => db.PluginOperationalStates.Where(x =>
        x.Id == id && x.Kind == Kind && x.OrganizationId == scope.Business &&
        x.AgentInstallationId == scope.ProviderInstallation && x.ExternalKey == ExternalKey(id));
    public async Task<ProtectedAccount?> ReadAsync(Guid id, CancellationToken ct)
    {
        var row = await Records(id).AsNoTracking().SingleOrDefaultAsync(ct);
        if (row is null) return null;
        try { return new(id, row.Revision, JsonSerializer.Deserialize<string>(row.PayloadJson) ?? throw new JsonException()); }
        catch (JsonException) { throw new InvalidOperationException("The account storage record is unavailable."); }
    }
    public async Task<bool> CompareExchangeAsync(Guid id, long? expectedRevision, ProtectedAccount next, CancellationToken ct)
    {
        if (id != next.Id || next.Revision != (expectedRevision ?? 0) + 1 || string.IsNullOrWhiteSpace(next.Ciphertext))
            throw new ArgumentException("Invalid account revision.");
        var row = await Records(id).SingleOrDefaultAsync(ct);
        if (row?.Revision != expectedRevision) return false;
        if (row is null)
        {
            row = new() { Id = id, Kind = Kind, OrganizationId = scope.Business, AgentInstallationId = scope.ProviderInstallation,
                ExternalKey = ExternalKey(id), CreatedAt = DateTimeOffset.UtcNow };
            db.PluginOperationalStates.Add(row);
        }
        row.Revision = next.Revision; row.UpdatedAt = DateTimeOffset.UtcNow;
        row.PayloadJson = JsonSerializer.Serialize(next.Ciphertext);
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { db.Entry(row).State = EntityState.Detached; return false; }
    }
}
