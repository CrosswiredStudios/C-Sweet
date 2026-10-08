using System.Security.Cryptography;
using System.Text;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    // Preserve the existing legacy review policy and wire tokens for native audiences.
    // Newly supported shared/private-installation proposals require verified attribution.
    private async Task<string?> RequireSourceOperatorAudienceAsync(Guid organization, Guid employee, Guid actor,
        MemoryEpisode episode, CancellationToken token)
    {
        var partition = episode.Partition;
        var tenant = organization.ToString("D");
        var native = partition == EmployeeMemoryNamespaces.Employee(tenant, employee.ToString("D"), "csweet").Partition ||
            partition == EmployeeMemoryNamespaces.UserRelationship(tenant, employee.ToString("D"), actor.ToString("D"), "csweet").Partition ||
            partition == EmployeeMemoryNamespaces.Organization(tenant, "csweet").Partition;
        if (native)
        {
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, employee, actor, partition, token);
            if (episode.CorrectionEvidence is not null || MemorySharedAudiences.Required(episode) is not null)
            {
                var user = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == actor && x.OrganizationId == organization)
                    .Select(x => x.ApplicationUserId).SingleAsync(token) ?? throw new UnauthorizedAccessException();
                var retained = await ReadTransferRetentionAsync(organization, employee, user, actor, episode, token);
                if (retained.Blocker == "memory_transfer_retention_review_required") throw new InvalidOperationException("memory_transfer_source_unavailable");
                return retained.EvidenceHash;
            }
            return null;
        }

        await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, organization, employee, actor, partition, token,
            requireActiveRelationship: false);
        if (episode.Source.Type != "agent-proposal" || episode.Source.Author != employee.ToString("D"))
            throw new UnauthorizedAccessException();
        if (MetadataGuid(episode, "installationId") is not { } installation ||
            !AgentMemoryService.IsVerifiedProposalForOperatorReview(episode, organization, employee, installation) ||
            !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == employee && x.OrganizationId == organization &&
                x.AgentInstallationId == installation && x.EmployeeType == EmployeeType.Agent && x.IsActive && x.ArchivedAt == null, token) ||
            await db.CoreOrganizationUsers.AsNoTracking().CountAsync(x => x.OrganizationId == organization &&
                x.AgentInstallationId == installation && x.EmployeeType == EmployeeType.Agent && x.IsActive && x.ArchivedAt == null, token) != 1)
            throw new InvalidOperationException("memory_review_source_unavailable");

        var team = Guid.Empty; var role = Guid.Empty;
        if (partition.CustomNamespace?.StartsWith("team:", StringComparison.Ordinal) == true) team = Guid.Parse(partition.CustomNamespace[5..]);
        if (partition.CustomNamespace?.StartsWith("role:", StringComparison.Ordinal) == true) role = Guid.Parse(partition.CustomNamespace[5..]);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Encoding.UTF8.GetBytes(Hash(new { organization, employee, actor, partition })));
        // xmin detects a changed-and-restored authority row without retaining personal
        // values in an operation receipt. Rows are already locked by audience validation.
        using var command = Command("""
            SELECT kind,CASE WHEN octet_length(value)<=1048576 THEN value ELSE NULL END FROM (
            SELECT 'employee' AS kind,to_jsonb(t)::text || ':' || t.xmin::text AS value FROM "CoreOrganizationUsers" t
                WHERE t."OrganizationId"=@organization AND (t."Id"=@employee OR t."Id"=@actor)
            UNION ALL SELECT 'installation',to_jsonb(t)::text || ':' || t.xmin::text FROM "AgentInstallations" t WHERE t."Id"=@installation
            UNION ALL SELECT 'team',to_jsonb(t)::text || ':' || t.xmin::text FROM "OrganizationTeams" t WHERE t."Id"=@team
            UNION ALL SELECT 'membership',to_jsonb(t)::text || ':' || t.xmin::text FROM "TeamMemberships" t
                WHERE t."TeamId"=@team AND (t."OrganizationUserId"=@employee OR t."OrganizationUserId"=@actor)
            UNION ALL SELECT 'role',to_jsonb(t)::text || ':' || t.xmin::text FROM "CoreRoles" t WHERE t."Id"=@role
            ) authority
            ORDER BY 1,2 LIMIT 129
            """);
        command.Parameters.AddWithValue("organization", organization); command.Parameters.AddWithValue("employee", employee);
        command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("installation", installation);
        command.Parameters.AddWithValue("team", team); command.Parameters.AddWithValue("role", role);
        using var reader = await command.ExecuteReaderAsync(token); var rows = 0; long bytes = 0;
        while (await reader.ReadAsync(token))
        {
            if (reader.IsDBNull(1)) throw new InvalidOperationException("memory_review_source_unavailable");
            var value = Encoding.UTF8.GetBytes(reader.GetString(0) + ":" + reader.GetString(1)); bytes += value.Length;
            if (++rows > 128 || bytes > 1_048_576) throw new InvalidOperationException("memory_review_source_unavailable");
            digest.AppendData(BitConverter.GetBytes(value.Length)); digest.AppendData(value);
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }

    private static string SourceOperatorToken(string sourceToken, string? audienceHash) => audienceHash is null ? sourceToken :
        Hash(new { SourceToken = sourceToken, AudienceHash = audienceHash });

    private async Task<string?> RequireSharedReviewSourcesAsync(Guid organization, Guid employee, Guid user, Guid actor,
        IEnumerable<MemoryEpisode> sources, CancellationToken token)
    {
        var hashes = new List<string>();
        foreach (var source in sources.OrderBy(x => x.Id))
        {
            if ((source.TransferEvidence is not null || source.CorrectionEvidence is not null || source.SourceFingerprint?.StartsWith("sha256-v3:", StringComparison.Ordinal) == true || source.Source.Type == "knowledge-transfer") && !MemorySourceIntegrity.IsVerified(source))
                throw new InvalidOperationException("memory_transfer_source_unavailable");
            if (source.CorrectionEvidence is null && MemorySharedAudiences.Required(source) is null) continue;
            var retained = await ReadTransferRetentionAsync(organization, employee, user, actor, source, token);
            if (!retained.IsTransferred || retained.Blocker == "memory_transfer_retention_review_required" || retained.EvidenceHash is null)
                throw new InvalidOperationException("memory_transfer_source_unavailable");
            hashes.Add(retained.EvidenceHash);
        }
        return hashes.Count == 0 ? null : Hash(hashes);
    }
}
