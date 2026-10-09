using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed record AgentMemoryIdentity(string TenantId, string EmployeeId);

public interface IAgentMemoryIdentityResolver
{
    Task<AgentMemoryIdentity?> ResolveAsync(AgentSession session, CancellationToken cancellationToken);
    Task AuthorizeAsync(AgentSession session, MemoryPartition partition, PlatformMemoryAction action,
        CancellationToken cancellationToken);
}

public sealed class AgentMemoryIdentityResolver(CSweetDbContext db) : IAgentMemoryIdentityResolver
{
    public async Task<AgentMemoryIdentity?> ResolveAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(session.InstallationId, out var installationId) ||
            !Guid.TryParse(session.BusinessId, out var organizationId)) return null;
        var matches = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AgentInstallationId == installationId &&
                x.EmployeeType == EmployeeType.Agent && x.IsActive && x.ArchivedAt == null &&
                x.AgentInstallation != null && x.AgentInstallation.IsEnabled)
            .Select(x => new AgentMemoryIdentity(x.OrganizationId.ToString(), x.Id.ToString()))
            .Take(2)
            .ToListAsync(cancellationToken);
        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task AuthorizeAsync(AgentSession session, MemoryPartition partition, PlatformMemoryAction action,
        CancellationToken cancellationToken)
    {
        var scope = PlatformMemoryNamespacePolicy.Resolve(session, partition, action);
        var organizationId = Guid.Parse(session.MemoryTenantId!);
        var employeeId = Guid.Parse(session.MemoryEmployeeId!);
        var now = DateTimeOffset.UtcNow;
        if (scope.Audience is MemoryAudienceType.Case or MemoryAudienceType.Conversation)
        {
            await MemoryScopedAudienceAuthorization.RequireAsync(db, organizationId, employeeId, null, partition,
                cancellationToken, lockAuthority: db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null);
            return;
        }
        var allowed = scope.Audience switch
        {
            MemoryAudienceType.Organization or MemoryAudienceType.Employee => true,
            MemoryAudienceType.UserRelationship => await HasRelationshipAsync(Guid.Parse(scope.AudienceId)),
            MemoryAudienceType.Team => await db.TeamMemberships.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.OrganizationUserId == employeeId &&
                x.TeamId == Guid.Parse(scope.AudienceId) && x.JoinedAt <= now &&
                (x.EndedAt == null || x.EndedAt > now) && x.Team != null &&
                x.Team.OrganizationId == organizationId && x.Team.ArchivedAt == null, cancellationToken),
            MemoryAudienceType.Role => await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.Id == employeeId && x.OrganizationId == organizationId &&
                x.RoleId == Guid.Parse(scope.AudienceId) && x.Role != null &&
                x.Role.OrganizationId == organizationId, cancellationToken),
            _ => false
        };
        if (!allowed) throw PlatformMemoryNamespacePolicy.Denied();

        async Task<bool> HasRelationshipAsync(Guid userId) =>
            await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == userId &&
                x.OrganizationId == organizationId && x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, cancellationToken) &&
            await db.CoreConversations.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId &&
                x.AgentOrganizationUserId == employeeId && x.InitiatedByOrganizationUserId == userId &&
                x.Kind == ConversationKind.DirectHumanAgent && x.ArchivedAt == null && x.MergedIntoConversationId == null, cancellationToken);
    }
}
