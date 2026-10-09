using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.Infrastructure.Core;

// Operator review preserves the retained audience. It cannot migrate or widen it.
internal static class MemoryEpisodeOperatorAuthorization
{
    internal static string Label(MemoryPartition partition) => partition.ApplicationId != "csweet" ? "Installation-private" :
        partition.CustomNamespace?.StartsWith("team:", StringComparison.Ordinal) == true ? "Team" :
        partition.CustomNamespace?.StartsWith("role:", StringComparison.Ordinal) == true ? "Role" :
        partition.CustomNamespace?.StartsWith("relationship:", StringComparison.Ordinal) == true ? "Private relationship" :
        partition.CustomNamespace == "organization" ? "Organization" :
        MemoryScopedAudienceAuthorization.Resolve(partition)?.Audience.ToString() ?? "Employee";

    internal static async Task RequirePartitionAsync(CSweetDbContext db, Guid organization, Guid employee,
        Guid actor, MemoryPartition partition, CancellationToken token, bool requireActiveRelationship = true, bool retainedScoped = false)
    {
        var tenant = organization.ToString("D");
        var employeeKey = employee.ToString("D");
        var installation = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == employee && x.OrganizationId == organization)
            .Select(x => x.AgentInstallationId).SingleAsync(token) ?? throw new UnauthorizedAccessException();
        var canonical = partition with { ApplicationId = "csweet" };
        var own = EmployeeMemoryNamespaces.Employee(tenant, employeeKey, "csweet").Partition;
        var relationship = EmployeeMemoryNamespaces.UserRelationship(tenant, employeeKey, actor.ToString("D"), "csweet").Partition;
        var privateAudience = canonical == own || canonical == relationship;
        if (partition.TenantId != tenant || partition.ApplicationId != "csweet" &&
            (!privateAudience || partition.ApplicationId != installation.ToString("D"))) throw new UnauthorizedAccessException();
        // The existing manager helper locks and validates the current installation too.
        await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, employee, actor, own, token, true);
        if (MemoryScopedAudienceAuthorization.Resolve(partition) is not null)
        {
            await MemoryScopedAudienceAuthorization.RequireAsync(db, organization, employee, actor, partition, token,
                lockAuthority: true, retained: retainedScoped);
            return;
        }
        if (privateAudience)
        {
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, employee, actor, canonical, token, true);
            if (canonical == relationship && requireActiveRelationship)
            {
                var conversation = await db.CoreConversations.AsNoTracking().Where(x => x.OrganizationId == organization &&
                    x.AgentOrganizationUserId == employee && x.InitiatedByOrganizationUserId == actor &&
                    x.Kind == CSweet.Domain.Core.ConversationKind.DirectHumanAgent && x.ArchivedAt == null && x.MergedIntoConversationId == null)
                    .OrderBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
                if (conversation is null) throw new UnauthorizedAccessException();
                await LockAsync(db, $"SELECT 1 FROM \"CoreConversations\" WHERE \"Id\"={conversation.Value} FOR SHARE NOWAIT", token);
                if (!await db.CoreConversations.AsNoTracking().AnyAsync(x => x.Id == conversation && x.ArchivedAt == null &&
                    x.MergedIntoConversationId == null && x.OrganizationId == organization &&
                    x.AgentOrganizationUserId == employee && x.InitiatedByOrganizationUserId == actor, token)) throw new UnauthorizedAccessException();
            }
            return;
        }
        if (partition == EmployeeMemoryNamespaces.Organization(tenant, "csweet").Partition)
        {
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organization, employee, actor, partition, token, true);
            return;
        }
        if (partition.CustomNamespace?.StartsWith("team:", StringComparison.Ordinal) == true &&
            Guid.TryParseExact(partition.CustomNamespace[5..], "D", out var team) &&
            partition == EmployeeMemoryNamespaces.Team(tenant, team.ToString("D"), "csweet").Partition)
        {
            await LockAsync(db, $"SELECT 1 FROM \"OrganizationTeams\" WHERE \"Id\"={team} FOR SHARE NOWAIT", token);
            await LockAsync(db, $"SELECT 1 FROM \"TeamMemberships\" WHERE \"TeamId\"={team} AND (\"OrganizationUserId\"={employee} OR \"OrganizationUserId\"={actor}) ORDER BY \"Id\" FOR SHARE NOWAIT", token);
            var now = DateTimeOffset.UtcNow;
            var members = await db.TeamMemberships.AsNoTracking().Where(x => x.OrganizationId == organization &&
                x.TeamId == team && (x.OrganizationUserId == employee || x.OrganizationUserId == actor) &&
                x.JoinedAt <= now && (x.EndedAt == null || x.EndedAt > now) &&
                x.Team!.OrganizationId == organization && x.Team.ArchivedAt == null)
                .Select(x => x.OrganizationUserId).Distinct().ToListAsync(token);
            if (!members.Contains(employee) || !members.Contains(actor)) throw new UnauthorizedAccessException();
            return;
        }
        if (partition.CustomNamespace?.StartsWith("role:", StringComparison.Ordinal) == true &&
            Guid.TryParseExact(partition.CustomNamespace[5..], "D", out var role) &&
            partition == EmployeeMemoryNamespaces.Role(tenant, role.ToString("D"), "csweet").Partition)
        {
            await LockAsync(db, $"SELECT 1 FROM \"CoreRoles\" WHERE \"Id\"={role} FOR SHARE NOWAIT", token);
            if (await db.CoreOrganizationUsers.AsNoTracking().CountAsync(x => x.OrganizationId == organization &&
                (x.Id == employee || x.Id == actor) && x.RoleId == role && x.Role!.OrganizationId == organization, token) != 2)
                throw new UnauthorizedAccessException();
            return;
        }
        throw new UnauthorizedAccessException();
    }

    internal static async Task<string[]> ReadableKeysAsync(CSweetDbContext db, Guid organization, Guid employee,
        Guid actor, CancellationToken token)
    {
        var tenant = organization.ToString("D"); var id = employee.ToString("D");
        var own = EmployeeMemoryNamespaces.Employee(tenant, id, "csweet").Partition;
        await RequirePartitionAsync(db, organization, employee, actor, own, token);
        var installation = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == employee).Select(x => x.AgentInstallationId).SingleAsync(token);
        var candidates = new List<MemoryPartition> { own, own with { ApplicationId = installation!.Value.ToString("D") } };
        if (await db.CoreConversations.AsNoTracking().AnyAsync(x => x.OrganizationId == organization &&
            x.AgentOrganizationUserId == employee && x.InitiatedByOrganizationUserId == actor &&
            x.Kind == CSweet.Domain.Core.ConversationKind.DirectHumanAgent && x.ArchivedAt == null && x.MergedIntoConversationId == null, token))
        {
            var relationship = EmployeeMemoryNamespaces.UserRelationship(tenant, id, actor.ToString("D"), "csweet").Partition;
            candidates.Add(relationship); candidates.Add(relationship with { ApplicationId = installation.Value.ToString("D") });
        }
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == actor && x.ReportsToOrganizationUserId != null, token))
            candidates.Add(EmployeeMemoryNamespaces.Organization(tenant, "csweet").Partition);
        var role = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == employee && x.RoleId != null &&
            x.Role!.OrganizationId == organization && db.CoreOrganizationUsers.Any(a => a.Id == actor && a.RoleId == x.RoleId))
            .Select(x => x.RoleId).SingleOrDefaultAsync(token);
        if (role.HasValue) candidates.Add(EmployeeMemoryNamespaces.Role(tenant, role.Value.ToString("D"), "csweet").Partition);
        var now = DateTimeOffset.UtcNow;
        var teams = await db.TeamMemberships.AsNoTracking().Where(x => x.OrganizationId == organization && x.OrganizationUserId == employee &&
            x.JoinedAt <= now && (x.EndedAt == null || x.EndedAt > now) && x.Team!.OrganizationId == organization && x.Team.ArchivedAt == null &&
            db.TeamMemberships.Any(a => a.OrganizationId == organization && a.TeamId == x.TeamId && a.OrganizationUserId == actor &&
                a.JoinedAt <= now && (a.EndedAt == null || a.EndedAt > now)))
            .Select(x => x.TeamId).Distinct().OrderBy(x => x).Take(129).ToListAsync(token);
        if (teams.Count > 128) throw new InvalidOperationException("memory_ingestion_audience_capacity");
        candidates.AddRange(teams.Select(x => EmployeeMemoryNamespaces.Team(tenant, x.ToString("D"), "csweet").Partition));
        candidates.AddRange(await MemoryScopedAudienceAuthorization.ReadableAsync(db, organization, employee, actor, token));
        foreach (var candidate in candidates) await RequirePartitionAsync(db, organization, employee, actor, candidate, token);
        return candidates.Select(x => x.StorageKey).ToArray();
    }

    private static async Task LockAsync(CSweetDbContext db, FormattableString sql, CancellationToken token)
    {
        try { await db.Database.ExecuteSqlInterpolatedAsync(sql, token); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("The recovery audience is changing. Refresh before reviewing.", error); }
    }
}
