using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeEligibilityService(
    CSweetDbContext db,
    IAgentConfigurationService configurations) : IAgentRuntimeEligibilityService
{
    public async Task<AgentRuntimeEligibility> EvaluateAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await db.AgentInstallations.AsNoTracking()
            .Include(x => x.PackageVersion)
            .Include(x => x.Schedule)
            .Include(x => x.Grant)
            .Include(x => x.AgentDefinition)!.ThenInclude(x => x!.Configuration)
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        if (installation is null)
            return Denied("The installation does not exist.");
        if (!installation.IsEnabled || installation.Schedule?.IsEnabled != true || installation.Grant is null)
            return Denied("The installation, schedule, or approved grant is disabled or unavailable.");
        var package = installation.PackageVersion!;
        if (package.Status != AgentPackageVersionStatus.Built || string.IsNullOrWhiteSpace(package.PackageDigest) ||
            string.IsNullOrWhiteSpace(package.ArtifactSignature))
            return Denied("The package is not built and signed.");

        var systemService = package.PluginKind == PluginKind.Service && installation.Scope == PluginInstallationScope.System;
        if (systemService)
            return new AgentRuntimeEligibility(true, null, true);
        if (package.PluginKind != PluginKind.Agent)
            return Denied("Only system-scoped service plugins may run without an employee assignment.");
        if (installation.AgentDefinitionId is null || installation.AgentDefinition?.IsAvailableForHire != true)
            return Denied("The installation is not linked to an available agent definition.");

        var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AgentInstallationId == installation.Id && x.IsActive, cancellationToken);
        if (employee is null)
        {
            AgentRuntimeMetrics.UnassignedRuntimePrevented();
            return Denied("Agent runtimes require an active hired employee.");
        }
        if (!Guid.TryParse(installation.BusinessId, out var businessId) ||
            businessId != employee.OrganizationId)
            return Denied("The employee organization does not match the installation business.");
        if (installation.SetupState != PluginSetupState.Ready)
            return Denied("Agent setup is incomplete.");

        try
        {
            var effective = await configurations.ResolveInstallationAsync(installation.Id, cancellationToken);
            var manifest = AgentConfigurationRules.DeserializeManifest(package.ManifestJson);
            await AgentConfigurationRules.ValidateAsync(db, manifest, effective.Settings, requireRequired: true, cancellationToken);
        }
        catch (AgentInstallationException exception)
        {
            return Denied($"The effective configuration is invalid: {exception.Message}");
        }
        return new AgentRuntimeEligibility(true, null);
    }

    private static AgentRuntimeEligibility Denied(string reason)
    {
        AgentRuntimeMetrics.RuntimeRequestDenied(reason);
        return new AgentRuntimeEligibility(false, reason);
    }
}
