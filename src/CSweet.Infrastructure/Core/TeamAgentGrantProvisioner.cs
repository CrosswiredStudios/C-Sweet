using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CSweet.Agent.SDK;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

internal static class TeamAgentGrantProvisioner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> SafeTeamActions = new HashSet<string>(
        WorkManagementCapabilityNames.All.Append(GitRepositoryCapabilities.TeamOptions),
        StringComparer.Ordinal);

    public static async Task<IReadOnlyList<string>> EnsureAsync(
        CSweetDbContext db,
        Guid organizationId,
        Guid installationId,
        Guid teamId,
        Guid grantedByOrganizationUserId,
        DateTimeOffset grantedAt,
        CancellationToken cancellationToken)
    {
        var metadata = await (
            from installation in db.AgentInstallations.AsNoTracking()
            join grant in db.AgentInstallationGrants.AsNoTracking()
                on installation.Id equals grant.AgentInstallationId
            join package in db.AgentPackageVersions.AsNoTracking()
                on installation.PackageVersionId equals package.Id into packages
            from package in packages.DefaultIfEmpty()
            where installation.Id == installationId &&
                  installation.BusinessId == organizationId.ToString("D") &&
                  installation.Scope == PluginInstallationScope.Organization &&
                  installation.IsEnabled &&
                  installation.RevisionStatus == PluginRevisionStatus.Active
            select new
            {
                grant.RequiredCapabilitiesJson,
                ManifestJson = package == null ? null : package.ManifestJson
            }).SingleOrDefaultAsync(cancellationToken);
        if (metadata is null) return [];

        var approved = ReadStringSet(metadata.RequiredCapabilitiesJson);
        var declared = ReadTeamScopedRequirements(metadata.ManifestJson);
        declared.IntersectWith(approved);
        declared.IntersectWith(SafeTeamActions);
        if (declared.Count == 0) return [];

        var now = DateTimeOffset.UtcNow;
        var existing = await db.ScopedActionGrants.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId &&
                x.SubjectKind == GrantSubjectKind.AgentInstallation &&
                x.SubjectId == installationId &&
                x.ScopeKind == GrantScopeKind.Team &&
                x.ScopeId == teamId &&
                x.RevokedAt == null &&
                (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .Select(x => x.Action)
            .ToListAsync(cancellationToken);
        var created = declared.Except(existing, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        foreach (var action in created)
        {
            db.ScopedActionGrants.Add(new ScopedActionGrant
            {
                Id = GeneratedGrantId(installationId, teamId, action),
                OrganizationId = organizationId,
                SubjectKind = GrantSubjectKind.AgentInstallation,
                SubjectId = installationId,
                Action = action,
                ScopeKind = GrantScopeKind.Team,
                ScopeId = teamId,
                CanDelegate = false,
                GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
                GrantedBySubjectId = grantedByOrganizationUserId,
                GrantedAt = grantedAt
            });
        }
        return created;
    }

    public static async Task RevokeAsync(
        CSweetDbContext db,
        Guid organizationId,
        Guid installationId,
        Guid teamId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        var generatedIds = SafeTeamActions
            .Select(action => GeneratedGrantId(installationId, teamId, action))
            .ToHashSet();
        var grants = await db.ScopedActionGrants.Where(x =>
            x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == installationId &&
            x.ScopeKind == GrantScopeKind.Team &&
            x.ScopeId == teamId &&
            generatedIds.Contains(x.Id) &&
            x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var grant in grants) grant.RevokedAt = revokedAt;
    }

    private static Guid GeneratedGrantId(Guid installationId, Guid teamId, string action)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{installationId}:{teamId}:{action}"));
        return Guid.Parse(Convert.ToHexString(hash));
    }

    private static HashSet<string> ReadStringSet(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            return (JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static HashSet<string> ReadTeamScopedRequirements(string? manifestJson)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(manifestJson)) return result;
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (!document.RootElement.TryGetProperty("requires", out var requires) ||
                requires.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var requirement in requires.EnumerateArray())
            {
                if (!requirement.TryGetProperty("name", out var name) ||
                    !requirement.TryGetProperty("scope", out var scope))
                    continue;
                var scopeValue = scope.GetString();
                var nameValue = name.GetString();
                if (nameValue is not null &&
                    scopeValue is "team" or "board")
                    result.Add(nameValue);
            }
        }
        catch (JsonException)
        {
            return result;
        }
        return result;
    }
}
