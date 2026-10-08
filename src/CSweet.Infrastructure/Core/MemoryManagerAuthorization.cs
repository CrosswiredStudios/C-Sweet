using CSweet.Domain.Core;
using CSweet.Memory;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.Infrastructure.Core;

internal static class MemoryManagerAuthorization
{
    internal static async Task<Guid> RequireAsync(CSweetDbContext db, Guid organizationId, Guid employeeId, Guid applicationUserId,
        bool lockRows, CancellationToken token, bool failOnLockContention = false)
    {
        var actors = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null)
            .Select(x => x.Id).Take(2).ToListAsync(token);
        if (actors.Count != 1) throw new UnauthorizedAccessException();
        var actorId = actors[0];
        var currentId = (Guid?)employeeId;
        var visited = new HashSet<Guid>();
        var found = false;
        // Follow only this employee's ancestry, with a finite bound and cycle detection.
        while (currentId.HasValue && visited.Count < 64)
        {
            if (!visited.Add(currentId.Value)) throw new UnauthorizedAccessException();
            if (lockRows && db.Database.IsNpgsql())
                await LockAuthorityRowAsync(db, "CoreOrganizationUsers", currentId.Value, failOnLockContention, token);
            var current = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.Id == currentId && x.IsActive && x.ArchivedAt == null, token);
            if (current is null || (current.Id == employeeId && current.EmployeeType != EmployeeType.Agent))
                throw new UnauthorizedAccessException();
            if (current.Id == actorId)
            {
                if (current.ApplicationUserId != applicationUserId || current.EmployeeType != EmployeeType.Human)
                    throw new UnauthorizedAccessException();
                found = true;
            }
            currentId = current.ReportsToOrganizationUserId;
        }
        if (!found || currentId.HasValue) throw new UnauthorizedAccessException();
        return actorId;
    }

    internal static async Task RequirePartitionAsync(CSweetDbContext db, Guid organizationId, Guid employeeId, Guid actorId, MemoryPartition partition, CancellationToken token,
        bool failOnLockContention = false)
    {
        var tenant = organizationId.ToString("D"); var employee = employeeId.ToString("D");
        var own = EmployeeMemoryNamespaces.Employee(tenant, employee, "csweet").Partition;
        var relationship = EmployeeMemoryNamespaces.UserRelationship(tenant, employee, actorId.ToString("D"), "csweet").Partition;
        var shared = EmployeeMemoryNamespaces.Organization(tenant, "csweet").Partition;
        if (partition != own && partition != relationship && partition != shared) throw new UnauthorizedAccessException();
        // Only an active human at the top of this reporting hierarchy may review shared business memory.
        if (partition == shared && await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == actorId &&
            x.ReportsToOrganizationUserId != null, token)) throw new UnauthorizedAccessException();
        var installation = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == employeeId && x.OrganizationId == organizationId)
            .Select(x => x.AgentInstallationId).SingleAsync(token);
        if (installation is null) throw new UnauthorizedAccessException();
        await LockAuthorityRowAsync(db, "AgentInstallations", installation.Value, failOnLockContention, token);
        if (!await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installation && x.IsEnabled && x.BusinessId == tenant, token))
            throw new UnauthorizedAccessException();
        if (partition == relationship)
        {
            var conversation = await db.CoreConversations.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                x.AgentOrganizationUserId == employeeId && x.InitiatedByOrganizationUserId == actorId).OrderBy(x => x.Id)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
            if (conversation is null) throw new UnauthorizedAccessException();
            // A lifecycle writer can own the parent row while waiting for the review's
            // memory barrier. Fail this review for refresh instead of waiting in reverse
            // lock order. Successful reviews still hold the relationship through commit,
            // including first-time library initialization and older empty databases.
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"CoreConversations\" WHERE \"Id\"={conversation.Value} FOR SHARE NOWAIT", token);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                throw new DbUpdateConcurrencyException("The memory audience is changing. Refresh before reviewing.", exception);
            }
            if (!await db.CoreConversations.AsNoTracking().AnyAsync(x => x.Id == conversation && x.OrganizationId == organizationId &&
                x.AgentOrganizationUserId == employeeId && x.InitiatedByOrganizationUserId == actorId, token)) throw new UnauthorizedAccessException();
        }
    }

    private static async Task LockAuthorityRowAsync(CSweetDbContext db, string table, Guid id, bool noWait, CancellationToken token)
    {
        // Identifiers are restricted to the two fixed authority tables at the call sites.
        FormattableString sql = (table, noWait) switch
        {
            ("CoreOrganizationUsers", false) => $"SELECT 1 FROM \"CoreOrganizationUsers\" WHERE \"Id\"={id} FOR SHARE",
            ("CoreOrganizationUsers", true) => $"SELECT 1 FROM \"CoreOrganizationUsers\" WHERE \"Id\"={id} FOR SHARE NOWAIT",
            ("AgentInstallations", false) => $"SELECT 1 FROM \"AgentInstallations\" WHERE \"Id\"={id} FOR SHARE",
            ("AgentInstallations", true) => $"SELECT 1 FROM \"AgentInstallations\" WHERE \"Id\"={id} FOR SHARE NOWAIT",
            _ => throw new ArgumentException("Unknown authority table.")
        };
        try { await db.Database.ExecuteSqlInterpolatedAsync(sql, token); }
        catch (PostgresException exception) when (noWait && exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new DbUpdateConcurrencyException("Memory review authority is changing. Refresh before reviewing.", exception);
        }
    }
}
