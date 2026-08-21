using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// Converges every hired installation on the built package selected by its global agent definition.
/// The installation id remains stable so employee, communication, grant, and Office routing records
/// do not need to be rewritten. Revoking the old MCP sessions makes the package switch immediate at
/// the broker boundary; runtime reconciliation then stops the workload on whichever Office hosts it.
/// </summary>
internal sealed class AgentDefinitionInstallationSynchronizer(
    CSweetDbContext db,
    IAuditEventWriter auditWriter)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AgentRuntimeStatus[] ActiveRuntimeStatuses =
    [
        AgentRuntimeStatus.Queued,
        AgentRuntimeStatus.Starting,
        AgentRuntimeStatus.WaitingForMcpSession,
        AgentRuntimeStatus.Running,
        AgentRuntimeStatus.CompletionReported,
        AgentRuntimeStatus.Stopping
    ];

    public async Task<int> SynchronizeAsync(
        Guid? definitionId = null,
        CancellationToken cancellationToken = default)
    {
        var driftQuery = db.AgentInstallations.AsNoTracking()
            .Where(x => x.AgentDefinition != null &&
                        x.PackageVersionId != x.AgentDefinition.PackageVersionId &&
                        x.AgentDefinition.Status == AgentDefinitionStatus.Available &&
                        x.AgentDefinition.IsAvailableForHire &&
                        x.AgentDefinition.PackageVersion != null &&
                        x.AgentDefinition.PackageVersion.Status == AgentPackageVersionStatus.Built &&
                        x.AgentDefinition.PackageVersion.PackageDigest != null &&
                        x.AgentDefinition.PackageVersion.PackageDigest != "" &&
                        x.AgentDefinition.PackageVersion.ArtifactSignature != null &&
                        x.AgentDefinition.PackageVersion.ArtifactSignature != "");
        if (definitionId.HasValue)
            driftQuery = driftQuery.Where(x => x.AgentDefinitionId == definitionId.Value);
        var driftDefinitionIds = await driftQuery
            .Select(x => x.AgentDefinitionId!.Value)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (driftDefinitionIds.Length == 0)
            return 0;

        var definitions = await db.AgentDefinitions
            .Include(x => x.PackageVersion)
            .Include(x => x.Configuration)
            .Include(x => x.Installations).ThenInclude(x => x.Grant)
            .Include(x => x.Installations).ThenInclude(x => x.Schedule)
            .Include(x => x.Installations).ThenInclude(x => x.Configuration)
            .Include(x => x.Installations).ThenInclude(x => x.RuntimeInstances
                .Where(runtime => ActiveRuntimeStatuses.Contains(runtime.Status)))
            .AsSplitQuery()
            .Where(x => driftDefinitionIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        var deployments = definitions
            .SelectMany(definition => definition.Installations
                .Where(installation => installation.PackageVersionId != definition.PackageVersionId)
                .Select(installation => (Definition: definition, Installation: installation)))
            .ToList();
        if (deployments.Count == 0)
            return 0;

        var installationIds = deployments.Select(x => x.Installation.Id).ToArray();
        var sessions = await db.McpAgentSessions
            .Where(x => installationIds.Contains(x.AgentInstallationId) && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var bindings = await db.AgentCapabilityBindings
            .Where(x => installationIds.Contains(x.RequesterInstallationId) && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var auditEntries = new List<(Guid InstallationId, string AgentId, string FromVersion, string ToVersion, string BusinessId)>();

        foreach (var deployment in deployments)
        {
            var definition = deployment.Definition;
            var installation = deployment.Installation;
            var previousVersion = await db.AgentPackageVersions.AsNoTracking()
                .Where(x => x.Id == installation.PackageVersionId)
                .Select(x => x.Version)
                .SingleAsync(cancellationToken);
            var activeRuntime = installation.RuntimeInstances.Any(x => ActiveRuntimeStatuses.Contains(x.Status));

            installation.PackageVersionId = definition.PackageVersionId;
            installation.RevisionNumber++;
            installation.DesiredConfigurationRevision++;
            installation.ConfigurationSyncStatus = activeRuntime
                ? AgentConfigurationSyncStatus.Restarting
                : AgentConfigurationSyncStatus.PendingNextStart;
            installation.ConfigurationSyncLastAttemptAt = activeRuntime ? now : null;
            installation.ConfigurationSyncLastError = null;
            installation.UpdatedAt = now;

            var grant = installation.Grant ??= new AgentInstallationGrant
            {
                Id = Guid.NewGuid(),
                AgentInstallationId = installation.Id
            };
            grant.NetworkAccessJson = definition.DefaultNetworkAccessJson;
            grant.ProvidedCapabilitiesJson = definition.DefaultProvidedCapabilitiesJson;
            grant.RequiredCapabilitiesJson = definition.DefaultRequiredCapabilitiesJson;
            grant.EventSubscriptionsJson = definition.DefaultEventSubscriptionsJson;
            grant.ResourceLimitsJson = JsonSerializer.Serialize(new
            {
                MaxRuntimeSeconds = definition.DefaultMaxRuntimeSeconds,
                MemoryMb = definition.DefaultMemoryMb,
                CpuPercent = definition.DefaultCpuPercent
            }, JsonOptions);
            grant.GrantRevision++;
            grant.MaxRuntimeSeconds = definition.DefaultMaxRuntimeSeconds;
            grant.MemoryMb = definition.DefaultMemoryMb;
            grant.CpuPercent = definition.DefaultCpuPercent;
            grant.ApprovedAt = now;

            if (installation.Schedule is not null)
                installation.Schedule.MaxRuntimeSeconds = definition.DefaultMaxRuntimeSeconds;

            if (installation.Configuration is not null)
            {
                var configurationKeys = AgentConfigurationRules
                    .DeserializeManifest(definition.PackageVersion!.ManifestJson)
                    .Configuration
                    .Where(x => !x.Secret)
                    .Select(x => x.Key)
                    .ToHashSet(StringComparer.Ordinal);
                var compatibleOverrides = DeserializeSettings(installation.Configuration.SettingsJson)
                    .Where(x => configurationKeys.Contains(x.Key))
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
                installation.Configuration.SchemaVersion = definition.Configuration?.SchemaVersion ?? "1";
                installation.Configuration.SettingsJson = JsonSerializer.Serialize(compatibleOverrides, JsonOptions);
                installation.Configuration.Revision++;
                installation.Configuration.UpdatedAt = now;
            }

            foreach (var session in sessions.Where(x => x.AgentInstallationId == installation.Id))
            {
                session.RevokedAt = now;
                session.RevocationReason = "The global agent definition selected a new package version.";
            }

            var requiredCapabilities = DeserializeGrant(definition.DefaultRequiredCapabilitiesJson);
            foreach (var binding in bindings.Where(x => x.RequesterInstallationId == installation.Id))
            {
                if (!requiredCapabilities.Contains(binding.Capability))
                    binding.RevokedAt = now;
                else
                    binding.GrantRevision = grant.GrantRevision;
            }

            auditEntries.Add((installation.Id, definition.AgentId, previousVersion,
                definition.PackageVersion!.Version, installation.BusinessId));
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var entry in auditEntries)
        {
            await auditWriter.WriteAsync(
                "agent-definition.installation-deployed",
                nameof(AgentInstallation),
                entry.InstallationId,
                $"Deployed {entry.AgentId} {entry.ToVersion} over {entry.FromVersion} for business {entry.BusinessId}; active runtime sessions were revoked for cross-Office restart.",
                cancellationToken: cancellationToken);
        }

        return deployments.Count;
    }

    private static Dictionary<string, JsonElement> DeserializeSettings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions)
        ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static HashSet<string> DeserializeGrant(string json) =>
        (JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [])
        .ToHashSet(StringComparer.Ordinal);
}
