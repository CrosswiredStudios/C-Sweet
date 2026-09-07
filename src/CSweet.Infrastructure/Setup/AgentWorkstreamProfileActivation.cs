using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

internal static class AgentWorkstreamProfileActivation
{
    public static async Task<int> ActivateAsync(CSweetDbContext db, PluginManifest manifest,
        CancellationToken token)
    {
        var changes = 0;
        foreach (var contribution in manifest.WorkstreamProfiles.Provides)
        {
            // Profile versions are immutable across package releases; provenance records
            // the first importer, which need not be the release installing this version.
            var definition = await db.WorkstreamProfileDefinitions.SingleOrDefaultAsync(x =>
                x.Key == contribution.Key && x.Version == contribution.Version &&
                x.ProviderPackageId == manifest.Id, token)
                ?? throw new AgentInstallationException(
                    $"The immutable Workstream profile '{contribution.Key}' v{contribution.Version} was not imported with this provider.");
            if (definition.Status == "Previewed")
            {
                definition.Status = WorkstreamProfileStatuses.Active;
                changes++;
            }
        }
        return changes;
    }

    public static async Task<int> ReconcileAsync(CSweetDbContext db, CancellationToken token)
    {
        var providerIds = await db.WorkstreamProfileDefinitions.AsNoTracking()
            .Where(x => x.Status == "Previewed").Select(x => x.ProviderPackageId).Distinct().ToArrayAsync(token);
        if (providerIds.Length == 0) return 0;
        var packages = await db.AgentInstallations.AsNoTracking()
            .Where(x => x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active &&
                x.Grant != null && x.Grant.ApprovedAt != default &&
                x.PackageVersion != null && providerIds.Contains(x.PackageVersion.AgentId) &&
                x.PackageVersion.Status == AgentPackageVersionStatus.Built &&
                x.PackageVersion.PackageDigest != null && x.PackageVersion.PackageDigest != "" &&
                x.PackageVersion.ArtifactSignature != null && x.PackageVersion.ArtifactSignature != "")
            .Select(x => x.PackageVersion!.ManifestJson).Distinct().ToListAsync(token);
        var changes = 0;
        foreach (var json in packages)
            changes += await ActivateAsync(db, AgentConfigurationRules.DeserializeManifest(json), token);
        return changes;
    }
}