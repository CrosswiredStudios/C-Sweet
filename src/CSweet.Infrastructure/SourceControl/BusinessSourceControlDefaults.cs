using System.Text.Json;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public static class BusinessSourceControlDefaultResolver
{
    public static async Task<Guid> ResolveAsync(CSweetDbContext db, Guid business, CancellationToken ct)
    {
        var selected = await db.SourceControlBusinessSettings.AsNoTracking().Where(s => s.OrganizationId == business).Select(s => s.DefaultTemplateId).SingleOrDefaultAsync(ct);
        if (selected is { } id) return id; // Never silently fall back when the selected provider is unavailable.
        var connection = await InternalGitProvisioningDefaults.EnsureAsync(db, business, ct);
        return await db.SourceControlRepositoryTemplates.AsNoTracking().Where(t => t.OrganizationId == business && t.ConnectionId == connection.Id && t.Name == "empty")
            .Select(t => t.Id).SingleAsync(ct);
    }
    public static bool SupportsCreation(SourceControlConnection c) => c.Status == SourceControlConnectionStatus.Connected &&
        (c.Provider == SourceControlProvider.InternalGit || (c.Provider == SourceControlProvider.GitHub && c.Mode == SourceControlConnectionMode.ManagedGitHub &&
            c.AccountType.Equals("Organization", StringComparison.OrdinalIgnoreCase) && c.ProvisionerInstallationId > 0 && c.SourceAccessInstallationId > 0));
}

public sealed partial class InternalRepositoryManagementService
{
    public async Task<BusinessSourceControlDefaults> BusinessDefaultsAsync(Guid business, Guid user, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var internalConnection = await EnsureConnectionAsync(business, ct);
        var settings = await db.SourceControlBusinessSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == business, ct);
        var templates = await db.SourceControlRepositoryTemplates.AsNoTracking().Include(t => t.Connection).Where(t => t.OrganizationId == business).ToListAsync(ct);
        var policies = await db.RepositoryProvisioningPolicies.AsNoTracking().Where(p => p.OrganizationId == business).ToListAsync(ct);
        var options = new List<BusinessSourceControlDefaultOption>();
        foreach (var template in templates.OrderBy(t => t.Connection!.Provider == SourceControlProvider.InternalGit ? 0 : 1).ThenBy(t => t.DisplayName))
        {
            var connection = template.Connection!;
            if (connection.OrganizationId != business) continue;
            var builtIn = connection.Id == internalConnection.Id && template.Name == "empty";
            if (!builtIn && connection.Provider != SourceControlProvider.GitHub) continue;
            var policy = policies.SingleOrDefault(p => p.ConnectionId == connection.Id);
            var approved = policy is not null && (JsonSerializer.Deserialize<List<Guid>>(policy.ApprovedTemplatesJson) ?? []).Contains(template.Id);
            var reason = !BusinessSourceControlDefaultResolver.SupportsCreation(connection) ? (builtIn ? "Internal Git is disconnected." : "A connected Managed GitHub organization with source access and a provisioner is required for GitHub creation.")
                : !template.IsEnabled ? "Template is disabled." : policy?.IsEnabled != true ? "Repository creation is disabled." : !approved ? "Template is not approved by the business policy." : null;
            options.Add(new(builtIn ? null : template.Id, connection.Provider.ToString(), connection.Name, builtIn ? "Empty internal repository" : template.DisplayName, reason is null, reason));
        }
        return new(settings?.DefaultTemplateId, settings?.Revision ?? 0, options);
    }
    public async Task<BusinessSourceControlDefaults> UpdateBusinessDefaultsAsync(Guid business, Guid user, UpdateBusinessSourceControlDefaults request, CancellationToken ct)
    {
        var current = await BusinessDefaultsAsync(business, user, ct);
        if (request.ExpectedRevision != current.Revision) throw new DbUpdateConcurrencyException("The default provider changed; reload before saving.");
        var selected = current.Options.SingleOrDefault(o => o.TemplateId == request.DefaultTemplateId);
        if (selected is null || !selected.Available) throw new ArgumentException(selected?.UnavailableReason ?? "Choose an approved template from this business.");
        var settings = await db.SourceControlBusinessSettings.AsTracking().SingleOrDefaultAsync(s => s.OrganizationId == business, ct);
        if ((settings?.Revision ?? 0) != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The default provider changed; reload before saving.");
        await AuditAsync(business, user, business, "DefaultProvider", "Started", request, ct);
        if (settings is null) { settings = new() { OrganizationId = business, Revision = 0 }; db.SourceControlBusinessSettings.Add(settings); }
        settings.DefaultTemplateId = request.DefaultTemplateId; settings.Revision++; settings.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await AuditAsync(business, user, business, "DefaultProvider", "Completed", request, ct);
        return await BusinessDefaultsAsync(business, user, ct);
    }
}
