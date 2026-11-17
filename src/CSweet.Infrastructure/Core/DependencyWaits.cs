using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

// Host-owned durable dependency state. A due time permits reconsideration, never an effect.
// Workflow handlers must re-read their authority and current inputs before resuming.
internal sealed record DependencyWait(string Operation, Guid OperationId, string Status,
    string Code, string Message, DateTimeOffset? RetryAt, int Attempts);

internal static class DependencyWaits
{
    internal const string Kind = "host-dependency-wait.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static DependencyWait Read(PluginOperationalState row) => JsonSerializer.Deserialize<DependencyWait>(row.PayloadJson, Json)!;
    internal static Task<PluginOperationalState?> FindAsync(CSweetDbContext db, Guid org, Guid installation,
        string operation, Guid id, CancellationToken ct) => db.PluginOperationalStates.SingleOrDefaultAsync(x =>
            x.OrganizationId == org && x.AgentInstallationId == installation && x.Kind == Kind &&
            x.ExternalKey == operation + ":" + id.ToString("N"), ct);

    internal static async Task<PluginOperationalState> SaveAsync(CSweetDbContext db, Guid org, Guid installation,
        DependencyWait value, DateTimeOffset now, CancellationToken ct)
    {
        var row = await FindAsync(db, org, installation, value.Operation, value.OperationId, ct);
        if (row is null)
        {
            row = new() { Id = Guid.NewGuid(), OrganizationId = org, AgentInstallationId = installation,
                Kind = Kind, ExternalKey = value.Operation + ":" + value.OperationId.ToString("N"), CreatedAt = now };
            db.PluginOperationalStates.Add(row);
        }
        row.PayloadJson = JsonSerializer.Serialize(value, Json);
        row.AvailableAt = value.Status == "WaitingDependency" ? value.RetryAt : null;
        row.UpdatedAt = now; row.Revision++;
        return row;
    }
}
