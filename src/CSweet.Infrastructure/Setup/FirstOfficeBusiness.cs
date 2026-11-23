using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

// Reserve a future business identifier at enrollment. The Office's local tenant
// policy and the signed dispatch verifier keep an exact business ID from day one.
public static class FirstOfficeBusiness
{
    private static readonly Guid CreationLock = new("d2fa4275-66f8-497c-b2f6-53b55fe6822a");

    public static Task LockAsync(CSweetDbContext db, CancellationToken ct) =>
        new ProjectWorkPolicy(db, TimeProvider.System).LockAsync(CreationLock, ct);

    // All callers hold CreationLock inside the surrounding transaction.
    public static async Task<Guid?> ReserveAsync(CSweetDbContext db, Guid? setupUser, CancellationToken ct)
    {
        if (await db.CoreOrganizations.AnyAsync(ct)) return null;
        var config = await db.SystemConfigurations.OrderBy(x => x.CreatedAt).FirstAsync(ct);
        if (config.FirstOfficeBusinessId.HasValue)
        {
            if (config.FirstOfficeSetupUserId == setupUser) return config.FirstOfficeBusinessId;
            return null;
        }
        config.FirstOfficeBusinessId = Guid.NewGuid();
        config.FirstOfficeSetupUserId = setupUser;
        return config.FirstOfficeBusinessId;
    }

    internal static async Task<Guid?> ConsumeAsync(CSweetDbContext db, Guid? creator, CancellationToken ct)
    {
        if (creator is null || creator == Guid.Empty) return null;
        var config = await db.SystemConfigurations.FirstOrDefaultAsync(x => x.FirstOfficeBusinessId != null, ct);
        if (config?.FirstOfficeBusinessId is not { } reserved) return null;
        if (config.FirstOfficeSetupUserId is { } owner)
        {
            if (owner != creator) return null;
        }
        else if (!await (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id
            join user in db.Users on membership.UserId equals user.Id
            where user.Id == creator && role.Name == AuthenticationService.AdministratorRole &&
                (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= DateTimeOffset.UtcNow)
            select role.Id).AnyAsync(ct)) return null;
        config.FirstOfficeBusinessId = null;
        config.FirstOfficeSetupUserId = null;
        return reserved;
    }
}
