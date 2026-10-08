using System.Security.Cryptography;
using System.Text;
using CSweet.Memory;
using CSweet.Contracts.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryTransferService
{
    public async Task<IReadOnlyList<MemoryTransferAudience>> ListAudiencesAsync(Guid organizationId, Guid employeeId, Guid targetEmployeeId,
        Guid applicationUserId, CancellationToken token = default)
    {
        await RequireBackendAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var actor = await AuthorizePairAsync(organizationId, employeeId, targetEmployeeId, applicationUserId, token);
        var candidates = await MemorySharedAudienceAuthorization.ReadableAsync(db, organizationId, employeeId, actor, token);
        var result = new List<MemoryTransferAudience>();
        foreach (var candidate in candidates)
        {
            var scope = candidate.Scope == "Team" ? EmployeeMemoryNamespaces.Team(organizationId.ToString("D"), candidate.AudienceId.ToString("D"), "csweet") :
                EmployeeMemoryNamespaces.Role(organizationId.ToString("D"), candidate.AudienceId.ToString("D"), "csweet");
            if (!await MemorySharedAudienceAuthorization.CanReadAsync(db, organizationId, targetEmployeeId, actor, scope.Partition, token)) continue;
            await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, scope.Partition, token);
            await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, organizationId, targetEmployeeId, actor, scope.Partition, token);
            result.Add(candidate);
        }
        await transaction.CommitAsync(token);
        return result;
    }
    private async Task RequireInheritedAudiencesAsync(Guid organization, Guid employee, Guid target, Guid actor,
        MemoryTransferEvidence? evidence, CancellationToken token)
    {
        foreach (var partition in MemorySharedAudiences.Merge(evidence?.RequiredSharedPartitions ?? []))
        {
            await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, organization, employee, actor, partition, token);
            await MemoryEpisodeOperatorAuthorization.RequirePartitionAsync(db, organization, target, actor, partition, token);
        }
    }

    private async Task<string> ReviewTokenAsync(KnowledgeTransferPackage package, MemoryTransferEvidence? evidence, Guid actor, CancellationToken token)
    {
        var shared = MemorySharedAudiences.Merge((evidence ?? package.ApprovedEvidence)?.RequiredSharedPartitions ?? []);
        if (package.SourceNamespaces[0].Audience is MemoryAudienceType.Team or MemoryAudienceType.Role)
            shared = MemorySharedAudiences.Merge(shared.Append(package.SourceNamespaces[0].Partition));
        if (shared.Length == 0) return Hash(new { package, evidence }); // preserve native review tokens
        var people = new[] { Guid.Parse(package.SourceEmployeeId), Guid.Parse(package.TargetEmployeeId), actor };
        var audienceHash = await MemorySharedAudienceAuthorization.AuthorityHashAsync(db, Guid.Parse(package.TenantId), people, shared, token);
        return Hash(new { package, evidence, audienceHash });
    }
}
