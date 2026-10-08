using CSweet.Domain.Core;
using CSweet.Contracts.Memory;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace CSweet.Infrastructure.Core;

internal static class MemorySharedAudienceAuthorization
{
    internal static async Task<MemoryTransferAudience[]> ReadableAsync(CSweetDbContext db, Guid organization, Guid employee, Guid human, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var teams = await db.OrganizationTeams.AsNoTracking().Where(x => x.OrganizationId == organization && x.ArchivedAt == null &&
            db.TeamMemberships.Any(m => m.OrganizationId == organization && m.TeamId == x.Id && m.OrganizationUserId == employee && m.JoinedAt <= now && (m.EndedAt == null || m.EndedAt > now)) &&
            db.TeamMemberships.Any(m => m.OrganizationId == organization && m.TeamId == x.Id && m.OrganizationUserId == human && m.JoinedAt <= now && (m.EndedAt == null || m.EndedAt > now)))
            .OrderBy(x => x.Id).Select(x => new MemoryTransferAudience("Team", x.Id, x.Name)).Take(129).ToArrayAsync(token);
        if (teams.Length > 128) throw new InvalidOperationException("memory_transfer_audience_capacity");
        var roles = await db.CoreRoles.AsNoTracking().Where(x => x.OrganizationId == organization &&
            db.CoreOrganizationUsers.Any(u => u.Id == employee && u.OrganizationId == organization && u.RoleId == x.Id) &&
            db.CoreOrganizationUsers.Any(u => u.Id == human && u.OrganizationId == organization && u.RoleId == x.Id))
            .Select(x => new MemoryTransferAudience("Role", x.Id, x.Name)).Take(2).ToArrayAsync(token);
        if (roles.Length > 1) throw new InvalidOperationException("memory_transfer_audience_capacity");
        return teams.Concat(roles).ToArray();
    }
    internal static async Task<bool> CanReadAsync(CSweetDbContext db, Guid organization, Guid employee, Guid? human,
        MemoryPartition partition, CancellationToken token)
    {
        if (!MemorySharedAudiences.IsCanonical(partition) || partition.TenantId != organization.ToString("D") ||
            partition.ApplicationId != "csweet") return false;
        var people = human.HasValue ? new[] { employee, human.Value }.Distinct().ToArray() : [employee];
        if (human == employee || await db.CoreOrganizationUsers.AsNoTracking().CountAsync(x => x.OrganizationId == organization &&
            people.Contains(x.Id) && x.IsActive && x.ArchivedAt == null &&
            (x.Id == employee ? x.EmployeeType == EmployeeType.Agent && x.AgentInstallation!.IsEnabled &&
                x.AgentInstallation.BusinessId == organization.ToString("D") : x.EmployeeType == EmployeeType.Human), token) != people.Length) return false;
        var id = Guid.Parse(partition.CustomNamespace![5..]); var now = DateTimeOffset.UtcNow;
        if (partition.CustomNamespace.StartsWith("team:", StringComparison.Ordinal))
            return await db.TeamMemberships.AsNoTracking().Where(x => x.OrganizationId == organization && x.TeamId == id &&
                people.Contains(x.OrganizationUserId) && x.JoinedAt <= now && (x.EndedAt == null || x.EndedAt > now) &&
                x.Team!.OrganizationId == organization && x.Team.ArchivedAt == null).Select(x => x.OrganizationUserId).Distinct().CountAsync(token) == people.Length;
        return await db.CoreOrganizationUsers.AsNoTracking().CountAsync(x => x.OrganizationId == organization && people.Contains(x.Id) &&
            x.RoleId == id && x.Role!.OrganizationId == organization, token) == people.Length;
    }

    internal static async Task RequireAsync(CSweetDbContext db, Guid organization, Guid employee, Guid? human,
        IEnumerable<MemoryPartition> partitions, CancellationToken token)
    {
        var required = MemorySharedAudiences.Merge(partitions);
        foreach (var partition in required)
            if (!await CanReadAsync(db, organization, employee, human, partition, token)) throw new UnauthorizedAccessException();
    }
    internal static async Task<string?> AuthorityHashAsync(CSweetDbContext db, Guid organization, IEnumerable<Guid> people,
        IEnumerable<MemoryPartition> partitions, CancellationToken token)
    {
        var shared = MemorySharedAudiences.Merge(partitions);
        if (shared.Length == 0) return null;
        var owners = people.Distinct().Order().ToArray();
        if (owners.Length is < 1 or > 65) throw new InvalidOperationException("memory_transfer_audience_capacity");
        var ids = shared.Select(x => Guid.Parse(x.CustomNamespace![5..])).Distinct().ToArray();
        await using var command = new NpgsqlCommand("""
            SELECT kind,CASE WHEN octet_length(value)<=1048576 THEN value ELSE NULL END FROM (
              SELECT 'person' AS kind,to_jsonb(t)::text || ':' || t.xmin::text AS value FROM "CoreOrganizationUsers" t
                WHERE t."OrganizationId"=@organization AND t."Id"=ANY(@people)
              UNION ALL SELECT 'installation',to_jsonb(t)::text || ':' || t.xmin::text FROM "AgentInstallations" t
                WHERE t."Id" IN (SELECT u."AgentInstallationId" FROM "CoreOrganizationUsers" u WHERE u."OrganizationId"=@organization AND u."Id"=ANY(@people))
              UNION ALL SELECT 'team',to_jsonb(t)::text || ':' || t.xmin::text FROM "OrganizationTeams" t WHERE t."Id"=ANY(@ids)
              UNION ALL SELECT 'role',to_jsonb(t)::text || ':' || t.xmin::text FROM "CoreRoles" t WHERE t."Id"=ANY(@ids)
              UNION ALL SELECT 'member',to_jsonb(t)::text || ':' || t.xmin::text FROM "TeamMemberships" t
                WHERE t."TeamId"=ANY(@ids) AND t."OrganizationId"=@organization AND t."OrganizationUserId"=ANY(@people)
            ) authority ORDER BY 1,2 LIMIT 257
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        command.Parameters.AddWithValue("organization", organization); command.Parameters.AddWithValue("people", owners);
        command.Parameters.AddWithValue("ids", ids);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var reader = await command.ExecuteReaderAsync(token); var rows = 0; long bytes = 0;
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(1)) throw new InvalidOperationException("memory_transfer_audience_capacity");
            var value = Encoding.UTF8.GetBytes(reader.GetString(0) + ":" + reader.GetString(1)); bytes += value.Length;
            if (++rows > 256 || bytes > 1_048_576) throw new InvalidOperationException("memory_transfer_audience_capacity");
            digest.AppendData(BitConverter.GetBytes(value.Length)); digest.AppendData(value);
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
}
