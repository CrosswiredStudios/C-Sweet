using System.Security.Cryptography;
using System.Text;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReextractionService
{
    private async Task<string> AuthorityHashAsync(Guid org, Guid employee, Guid actor, Guid installation,
        MemoryPartition partition, CancellationToken token)
    {
        var team = partition.CustomNamespace?.StartsWith("team:", StringComparison.Ordinal) == true
            ? Guid.Parse(partition.CustomNamespace[5..]) : Guid.Empty;
        var role = partition.CustomNamespace?.StartsWith("role:", StringComparison.Ordinal) == true
            ? Guid.Parse(partition.CustomNamespace[5..]) : Guid.Empty;
        var relationship = partition.UserId is not null;
        await using var command = new NpgsqlCommand("""
            WITH RECURSIVE ancestry AS (
              SELECT t.*,0 AS depth FROM "CoreOrganizationUsers" t WHERE t."Id"=@employee AND t."OrganizationId"=@org
              UNION ALL SELECT t.*,a.depth+1 FROM "CoreOrganizationUsers" t JOIN ancestry a ON t."Id"=a."ReportsToOrganizationUserId"
                WHERE t."OrganizationId"=@org AND a.depth<63
            ), authority AS (
              SELECT 'employee' AS kind,to_jsonb(t)::text || ':' || t.xmin::text AS value FROM "CoreOrganizationUsers" t
                WHERE t."Id" IN(SELECT "Id" FROM ancestry)
              UNION ALL SELECT 'installation',to_jsonb(t)::text || ':' || t.xmin::text FROM "AgentInstallations" t WHERE t."Id"=@installation
              UNION ALL SELECT 'grant',to_jsonb(t)::text || ':' || t.xmin::text FROM "AgentInstallationGrants" t WHERE t."AgentInstallationId"=@installation
              UNION ALL SELECT 'team',to_jsonb(t)::text || ':' || t.xmin::text FROM "OrganizationTeams" t WHERE t."Id"=@team
              UNION ALL SELECT 'membership',to_jsonb(t)::text || ':' || t.xmin::text FROM "TeamMemberships" t
                WHERE t."TeamId"=@team AND t."OrganizationUserId" IN(@employee,@actor)
              UNION ALL SELECT 'role',to_jsonb(t)::text || ':' || t.xmin::text FROM "CoreRoles" t WHERE t."Id"=@role
              UNION ALL SELECT 'conversation',to_jsonb(t)::text || ':' || t.xmin::text FROM "CoreConversations" t WHERE @relationship AND t."Id" IN (
                (SELECT "Id" FROM "CoreConversations" WHERE "OrganizationId"=@org AND "AgentOrganizationUserId"=@employee
                  AND "InitiatedByOrganizationUserId"=@actor ORDER BY "Id" LIMIT 1),
                (SELECT "Id" FROM "CoreConversations" WHERE "OrganizationId"=@org AND "AgentOrganizationUserId"=@employee
                  AND "InitiatedByOrganizationUserId"=@actor AND "Kind"='DirectHumanAgent' AND "ArchivedAt" IS NULL
                  AND "MergedIntoConversationId" IS NULL ORDER BY "Id" LIMIT 1))
            )
            SELECT kind,CASE WHEN octet_length(value)<=1048576 THEN value ELSE NULL END FROM authority ORDER BY kind,value LIMIT 129
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
        command.Parameters.AddWithValue("org", org); command.Parameters.AddWithValue("employee", employee);
        command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("installation", installation);
        command.Parameters.AddWithValue("team", team); command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("relationship", relationship);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var reader = await command.ExecuteReaderAsync(token); var rows = 0; long bytes = 0;
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(1)) throw new InvalidOperationException("memory_reextraction_authority_unavailable");
            var value = Encoding.UTF8.GetBytes(reader.GetString(0) + ":" + reader.GetString(1)); bytes += value.Length;
            if (++rows > 128 || bytes > 1_048_576) throw new InvalidOperationException("memory_reextraction_authority_unavailable");
            digest.AppendData(BitConverter.GetBytes(value.Length)); digest.AppendData(value);
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
}
