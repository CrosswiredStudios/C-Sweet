using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Host-owned generation references keep failed or late vault writes out of the active credential slot.</summary>
public static class PluginOAuthCredentialKeys
{
    public const string ActiveKind = "host-oauth-active-credential";
    public const string GenerationKind = "host-oauth-credential-generation";
    public static string Legacy(Guid connectionId) => $"oauth.connection.{connectionId:N}.token";
    public static string Generation(Guid connectionId, Guid attemptId) => $"oauth.connection.{connectionId:N}.generation.{attemptId:N}.token";

    public static async Task<string> ResolveAsync(CSweetDbContext db, Guid installationId, Guid connectionId, CancellationToken ct)
    {
        var reference = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installationId && x.Kind == ActiveKind && x.ExternalKey == connectionId.ToString("N"), ct);
        return reference is null ? Legacy(connectionId) : ReadKey(reference, connectionId);
    }

    public static string ReadKey(PluginOperationalState reference, Guid connectionId)
    {
        using var payload = JsonDocument.Parse(reference.PayloadJson);
        var attempt = payload.RootElement.GetProperty("attemptId").GetGuid();
        if (payload.RootElement.GetProperty("connectionId").GetGuid() != connectionId || attempt == Guid.Empty)
            throw new InvalidOperationException("The credential generation reference is invalid.");
        return Generation(connectionId, attempt);
    }

    public static PluginOperationalState Register(CSweetDbContext db, Guid organizationId, Guid installationId, Guid connectionId, Guid attemptId)
    {
        var record = new PluginOperationalState { Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentInstallationId = installationId, Kind = GenerationKind, ExternalKey = attemptId.ToString("N"),
            PayloadJson = JsonSerializer.Serialize(new { connectionId, attemptId }), Revision = 1,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.PluginOperationalStates.Add(record);
        return record;
    }

    public static async Task SelectAsync(CSweetDbContext db, Guid organizationId, Guid installationId, Guid connectionId, Guid attemptId, CancellationToken ct)
    {
        var reference = await db.PluginOperationalStates.SingleOrDefaultAsync(x => x.AgentInstallationId == installationId &&
            x.Kind == ActiveKind && x.ExternalKey == connectionId.ToString("N"), ct);
        if (reference is null)
        {
            reference = new PluginOperationalState { Id = Guid.NewGuid(), OrganizationId = organizationId,
                AgentInstallationId = installationId, Kind = ActiveKind, ExternalKey = connectionId.ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
            db.PluginOperationalStates.Add(reference);
        }
        reference.PayloadJson = JsonSerializer.Serialize(new { connectionId, attemptId });
        reference.Revision++; reference.UpdatedAt = DateTimeOffset.UtcNow;
        // The caller commits this reference in the same DB transaction as Connected status.
    }
}
