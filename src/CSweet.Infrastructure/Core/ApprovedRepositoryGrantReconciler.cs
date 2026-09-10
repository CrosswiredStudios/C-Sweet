using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

internal static class ApprovedRepositoryGrantReconciler
{
    public static async Task<int> EnsureAsync(CSweetDbContext db, Guid organizationId,
        Guid installationId, Guid managerId, CancellationToken token)
    {
        const string action = SourceControlCapabilities.ProvisionRepository;
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant)
            .Include(x => x.PackageVersion).SingleOrDefaultAsync(x => x.Id == installationId &&
                x.BusinessId == organizationId.ToString("D") && x.Scope == PluginInstallationScope.Organization &&
                x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, token);
        if (installation?.Grant is not { } approved || approved.ApprovedAt == default ||
            installation.PackageVersion?.ManifestJson is not { } manifest) return 0;
        try
        {
            if (!(JsonSerializer.Deserialize<string[]>(approved.RequiredCapabilitiesJson) ?? []).Contains(action)) return 0;
            using var document = JsonDocument.Parse(manifest);
            if (!document.RootElement.TryGetProperty("requires", out var requires) || requires.ValueKind != JsonValueKind.Array ||
                !requires.EnumerateArray().Any(x => x.TryGetProperty("name", out var name) && name.GetString() == action &&
                    x.TryGetProperty("scope", out var scope) && scope.GetString() == "organization")) return 0;
        }
        catch (JsonException) { return 0; }
        // Any prior grant, including an expired or revoked one, preserves the operator's decision.
        if (db.ScopedActionGrants.Local.Any(x => x.OrganizationId == organizationId && x.SubjectId == installationId &&
                x.SubjectKind == GrantSubjectKind.AgentInstallation && x.Action == action && x.ScopeKind == GrantScopeKind.Organization) ||
            await db.ScopedActionGrants.AnyAsync(x => x.OrganizationId == organizationId && x.SubjectId == installationId &&
                x.SubjectKind == GrantSubjectKind.AgentInstallation && x.Action == action && x.ScopeKind == GrantScopeKind.Organization, token)) return 0;
        db.ScopedActionGrants.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = installationId, Action = action, ScopeKind = GrantScopeKind.Organization, ScopeId = organizationId,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser, GrantedBySubjectId = managerId,
            GrantedAt = DateTimeOffset.UtcNow, CanDelegate = false
        });
        return 1;
    }
}
