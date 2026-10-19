using System.Collections.Concurrent;
using CSweet.Contracts.Localization;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Localization;

/// <summary>
/// Resolves the language a workspace's agents should write in.
/// <list type="bullet">
/// <item>User-scoped agents (<c>user:&lt;id&gt;</c>, e.g. the Personal Assistant) follow that user's preference.</item>
/// <item>Business agents follow the business owner's preference (highest-permission human member linked to an
/// account), falling back to the root administrator's preference, then English.</item>
/// </list>
/// Results are cached briefly because the platform LLM handler asks on every request; a preference change
/// therefore reaches agents within <see cref="CacheLifetime"/> or immediately after <see cref="Invalidate"/>.
/// </summary>
public static class WorkspaceLanguagePolicy
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, (string Code, DateTimeOffset ExpiresAt)> Cache = new(StringComparer.Ordinal);

    public static async Task<string> ResolveAsync(CSweetDbContext db, string? scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return SupportedCultures.Default;
        }

        var now = DateTimeOffset.UtcNow;
        if (Cache.TryGetValue(scope, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Code;
        }

        var code = await ResolveUncachedAsync(db, scope, cancellationToken);
        Cache[scope] = (code, now.Add(CacheLifetime));
        return code;
    }

    /// <summary>Drops cached answers after a preference changes so the next inference request sees it.</summary>
    public static void Invalidate() => Cache.Clear();

    private static async Task<string> ResolveUncachedAsync(CSweetDbContext db, string scope, CancellationToken cancellationToken)
    {
        string? preference = null;
        if (scope.StartsWith("user:", StringComparison.Ordinal) && Guid.TryParse(scope[5..], out var userId))
        {
            preference = await db.Users.AsNoTracking().Where(x => x.Id == userId)
                .Select(x => x.PreferredLanguage).SingleOrDefaultAsync(cancellationToken);
        }
        else if (Guid.TryParse(scope, out var organizationId))
        {
            var ownerUserId = await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.IsActive && x.ApplicationUserId != null &&
                            x.EmployeeType == EmployeeType.Human)
                .OrderByDescending(x => x.PermissionLevel).ThenBy(x => x.CreatedAt)
                .Select(x => x.ApplicationUserId)
                .FirstOrDefaultAsync(cancellationToken);
            if (ownerUserId is { } owner)
            {
                preference = await db.Users.AsNoTracking().Where(x => x.Id == owner)
                    .Select(x => x.PreferredLanguage).SingleOrDefaultAsync(cancellationToken);
            }
        }

        preference ??= await db.Users.AsNoTracking().Where(x => x.IsInitialAdministrator)
            .Select(x => x.PreferredLanguage).FirstOrDefaultAsync(cancellationToken);

        return SupportedCultures.Normalize(preference) ?? SupportedCultures.Default;
    }
}
