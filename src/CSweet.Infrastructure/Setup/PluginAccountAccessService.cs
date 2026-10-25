using CSweet.Domain.Setup;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Derives human authority from current persisted identity, not callback claims or plugin arguments.</summary>
public sealed class PluginAccountAccessService(CSweetDbContext db)
{
    public Task<bool> IsActiveHumanAsync(Guid userId, CancellationToken ct) => db.Users.AsNoTracking().AnyAsync(x => x.Id == userId &&
        (!x.LockoutEnabled || x.LockoutEnd == null || x.LockoutEnd <= DateTimeOffset.UtcNow), ct);
    public async Task<bool> IsAdministratorAsync(Guid userId, CancellationToken ct)
    {
        if (!await IsActiveHumanAsync(userId, ct)) return false;
        return await (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id
            where membership.UserId == userId && role.Name == AuthenticationService.AdministratorRole select role.Id).AnyAsync(ct);
    }
    public async Task RequireAdministratorAsync(Guid userId, CancellationToken ct)
    {
        if (!await IsAdministratorAsync(userId, ct)) throw new UnauthorizedAccessException("Enterprise plugin administration is required.");
    }
    public async Task RequireOperatorAsync(Guid userId, Guid installationId, Guid? groupId, CancellationToken ct)
    {
        if (await IsAdministratorAsync(userId, ct)) return;
        if (await db.Users.AsNoTracking().AnyAsync(x => x.Id == userId && (!x.LockoutEnabled || x.LockoutEnd == null || x.LockoutEnd <= DateTimeOffset.UtcNow), ct) &&
            await db.PluginAccountOperators.AsNoTracking().AnyAsync(x => x.ApplicationUserId == userId && x.PluginInstallationId == installationId &&
                x.RevokedAt == null && (x.AccountGroupId == null || groupId != null && x.AccountGroupId == groupId), ct)) return;
        throw new UnauthorizedAccessException("Ask an enterprise administrator to authorize you to connect this account.");
    }
}
