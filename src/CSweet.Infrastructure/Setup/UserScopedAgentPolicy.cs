using CSweet.Contracts.PersonalAssistant;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public static class UserScopedAgentPolicy
{
    public static async Task<bool> IsAuthorizedAsync(CSweetDbContext db, Guid installationId,
        string scope, CancellationToken token)
    {
        if (!scope.StartsWith("user:", StringComparison.Ordinal) || !Guid.TryParse(scope[5..], out var userId)) return false;
        return await db.Users.AnyAsync(x => x.Id == userId, token) && await db.AgentInstallations.AsNoTracking().AnyAsync(x => x.Id == installationId &&
            x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active && x.Scope == PluginInstallationScope.User &&
            x.OwnerApplicationUserId == userId && x.BusinessId == scope &&
            x.PackageVersion!.AgentId == PersonalAssistantCapabilities.AgentId &&
            x.PackageVersion.PackageSource!.IsBundled &&
            db.PersonalAssistantProfiles.Any(p => p.ApplicationUserId == userId && p.AgentInstallationId == x.Id), token);
    }
}
