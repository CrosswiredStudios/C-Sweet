using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    private async Task AuthorizeEpisodeJobAsync(MemoryEpisodeEnrichmentJob job, MemoryEpisode episode, CancellationToken token)
    {
        var tenant = job.OrganizationId.ToString("D"); var employee = job.EmployeeId.ToString("D");
        var app = episode.Partition.ApplicationId;
        if (episode.Partition.TenantId != tenant || app != ApplicationId && app != job.InstallationId.ToString("D"))
            throw new UnauthorizedAccessException();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreOrganizationUsers\" WHERE \"Id\"={job.EmployeeId} FOR SHARE NOWAIT", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AgentInstallations\" WHERE \"Id\"={job.InstallationId} FOR SHARE NOWAIT", token);
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == job.EmployeeId && x.OrganizationId == job.OrganizationId &&
            x.EmployeeType == EmployeeType.Agent && x.IsActive && x.ArchivedAt == null && x.AgentInstallationId == job.InstallationId &&
            x.AgentInstallation!.IsEnabled && x.AgentInstallation.BusinessId == tenant, token)) throw new UnauthorizedAccessException();
        if (await db.CoreOrganizationUsers.AsNoTracking().CountAsync(x => x.OrganizationId == job.OrganizationId &&
            x.AgentInstallationId == job.InstallationId && x.EmployeeType == EmployeeType.Agent && x.IsActive && x.ArchivedAt == null, token) != 1)
            throw new UnauthorizedAccessException();
        if (episode.Source.Type == "agent-proposal")
        {
            if (ReadGuid(episode.Metadata, "installationId") != job.InstallationId || episode.TransferEvidence is not null ||
                episode.Source.Author != employee || episode.Source.Id != episode.Id.ToString("D") ||
                job.ReviewerApplicationUserId is not null) throw new UnauthorizedAccessException();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AgentInstallationGrants\" WHERE \"AgentInstallationId\"={job.InstallationId} FOR SHARE NOWAIT", token);
            var grant = await db.AgentInstallationGrants.AsNoTracking().Where(x => x.AgentInstallationId == job.InstallationId)
                .Select(x => x.RequiredCapabilitiesJson).SingleOrDefaultAsync(token);
            if (grant is null || JsonSerializer.Deserialize<string[]>(grant)?.Contains("platform.memory.write.v1") != true)
                throw new UnauthorizedAccessException();
        }
        else if (episode.Source.Type != "knowledge-transfer" || episode.TransferEvidence is null || job.ReviewerApplicationUserId is null)
            throw new UnauthorizedAccessException();

        MemoryNamespace audience;
        var partition = episode.Partition;
        if (partition.CustomNamespace == "organization") audience = EmployeeMemoryNamespaces.Organization(tenant, app);
        else if (partition.CustomNamespace == "employee:" + employee) audience = EmployeeMemoryNamespaces.Employee(tenant, employee, app);
        else if (Guid.TryParseExact(partition.UserId, "D", out var user) && partition.CustomNamespace == $"relationship:{employee}:{user:D}")
        {
            audience = EmployeeMemoryNamespaces.UserRelationship(tenant, employee, user.ToString("D"), app);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreOrganizationUsers\" WHERE \"Id\"={user} FOR SHARE NOWAIT", token);
            if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == user && x.OrganizationId == job.OrganizationId &&
                x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, token)) throw new UnauthorizedAccessException();
            var conversation = await db.CoreConversations.AsNoTracking().Where(x => x.OrganizationId == job.OrganizationId &&
                x.AgentOrganizationUserId == job.EmployeeId && x.InitiatedByOrganizationUserId == user &&
                x.Kind == ConversationKind.DirectHumanAgent && x.ArchivedAt == null && x.MergedIntoConversationId == null)
                .OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
            if (conversation is null) throw new UnauthorizedAccessException();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreConversations\" WHERE \"Id\"={conversation.Value} FOR SHARE NOWAIT", token);
            if (!await db.CoreConversations.AsNoTracking().AnyAsync(x => x.Id == conversation && x.ArchivedAt == null &&
                x.MergedIntoConversationId == null && x.AgentOrganizationUserId == job.EmployeeId && x.InitiatedByOrganizationUserId == user, token))
                throw new UnauthorizedAccessException();
        }
        else if (partition.CustomNamespace?.StartsWith("team:", StringComparison.Ordinal) == true &&
            Guid.TryParseExact(partition.CustomNamespace[5..], "D", out var team))
        {
            audience = EmployeeMemoryNamespaces.Team(tenant, team.ToString("D"), app);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"OrganizationTeams\" WHERE \"Id\"={team} FOR SHARE NOWAIT", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"TeamMemberships\" WHERE \"TeamId\"={team} AND \"OrganizationUserId\"={job.EmployeeId} FOR SHARE NOWAIT", token);
            var now = DateTimeOffset.UtcNow;
            if (!await db.TeamMemberships.AsNoTracking().AnyAsync(x => x.OrganizationId == job.OrganizationId && x.TeamId == team &&
                x.OrganizationUserId == job.EmployeeId && x.JoinedAt <= now && (x.EndedAt == null || x.EndedAt > now) &&
                x.Team!.OrganizationId == job.OrganizationId && x.Team.ArchivedAt == null, token)) throw new UnauthorizedAccessException();
        }
        else if (partition.CustomNamespace?.StartsWith("role:", StringComparison.Ordinal) == true &&
            Guid.TryParseExact(partition.CustomNamespace[5..], "D", out var role))
        {
            audience = EmployeeMemoryNamespaces.Role(tenant, role.ToString("D"), app);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"CoreRoles\" WHERE \"Id\"={role} FOR SHARE NOWAIT", token);
            if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == job.EmployeeId && x.RoleId == role &&
                x.Role!.OrganizationId == job.OrganizationId, token)) throw new UnauthorizedAccessException();
        }
        else if (MemoryScopedAudienceAuthorization.Resolve(partition) is { } scoped)
        {
            audience = scoped;
            await MemoryScopedAudienceAuthorization.RequireAsync(db, job.OrganizationId, job.EmployeeId, null,
                partition, token, lockAuthority: true);
        }
        else throw new UnauthorizedAccessException();
        if (partition != audience.Partition || episode.Scope != audience.Scope) throw new UnauthorizedAccessException();
    }
}
