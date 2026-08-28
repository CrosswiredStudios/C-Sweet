using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.AI.Providers;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentDefinitionService(
    CSweetDbContext db,
    IAuditEventWriter auditWriter,
    IAgentBuildService buildService,
    IModelCatalogClient? modelCatalog = null,
    ILogger<AgentDefinitionService>? logger = null,
    IAgentInstallationService? installationService = null) : IAgentDefinitionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentDefinitionResponse> ImportAsync(
        Guid importId,
        InstallAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        var package = await db.AgentPackageVersions.Include(x => x.BuildJobs)
            .SingleOrDefaultAsync(x => x.Id == importId, cancellationToken)
            ?? throw new AgentInstallationException("The import preview was not found.");
        if (package.PluginKind != PluginKind.Agent)
            throw new AgentInstallationException("System service plugins must be installed through the plugin API.");
        if (package.Status is not (AgentPackageVersionStatus.Previewed or AgentPackageVersionStatus.Approved or AgentPackageVersionStatus.Built))
            throw new AgentInstallationException("The imported package version cannot be approved.");

        var manifest = AgentConfigurationRules.DeserializeManifest(package.ManifestJson);
        ValidateSubset("provided capabilities", request.GrantedCapabilities, manifest.Provides.Select(x => x.Name));
        ValidateSubset("required capabilities", request.GrantedRequestedCapabilities,
            AgentImportPreviewService.GrantRequiredCapabilities(manifest));
        ValidateSubset("event subscriptions", request.GrantedSubscriptions, manifest.Events.Subscribes);
        ValidateSubset("network access", request.GrantedNetworkAccess, AgentImportPreviewService.WebGrantTokens(manifest));
        if (request.GrantedPublications.Count > 0 || request.GrantedPermissions.Count > 0)
            throw new AgentInstallationException("Legacy generic publication and permission grants are not supported.");

        var activationMode = ParseEnum<ActivationMode>(request.ActivationMode, "activation mode");
        var overlapPolicy = ParseEnum<OverlapPolicy>(request.OverlapPolicy, "overlap policy");
        if (request.TickFrequencySeconds is <= 0 or > 86_400 || request.MaxRuntimeSeconds <= 0 ||
            request.MemoryMb <= 0 || request.CpuPercent is <= 0 or > 100)
            throw new AgentInstallationException("Schedule and resource defaults must be positive and CPU cannot exceed 100 percent.");

        var definition = await db.AgentDefinitions.Include(x => x.Configuration)
            .SingleOrDefaultAsync(x => x.PackageSourceId == package.PackageSourceId && x.AgentId == package.AgentId,
                cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (definition is null)
        {
            definition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                PackageSourceId = package.PackageSourceId,
                AgentId = package.AgentId,
                CreatedAt = now
            };
            db.AgentDefinitions.Add(definition);
        }

        var settings = AgentConfigurationRules.GetManifestDefaults(manifest);
        if (definition.Configuration is not null)
        {
            var old = DeserializeSettings(definition.Configuration.SettingsJson);
            var compatibleKeys = manifest.Configuration.Where(x => !x.Secret).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var pair in old.Where(x => compatibleKeys.Contains(x.Key)))
                settings[pair.Key] = pair.Value.Clone();
        }
        foreach (var pair in request.ConfigurationSettings)
            settings[pair.Key] = pair.Value.Clone();
        await AgentConfigurationRules.ValidateAsync(db, manifest, settings, requireRequired: false,
            cancellationToken, modelCatalog, validateSupportedModels: true);

        definition.PackageVersionId = package.Id;
        definition.DefaultActivationMode = activationMode;
        definition.DefaultTickFrequencySeconds = request.TickFrequencySeconds;
        definition.DefaultOverlapPolicy = overlapPolicy;
        definition.DefaultMaxRuntimeSeconds = request.MaxRuntimeSeconds;
        definition.DefaultMemoryMb = request.MemoryMb;
        definition.DefaultCpuPercent = request.CpuPercent;
        definition.DefaultProvidedCapabilitiesJson = Serialize(request.GrantedCapabilities);
        definition.DefaultRequiredCapabilitiesJson = Serialize(request.GrantedRequestedCapabilities);
        definition.DefaultEventSubscriptionsJson = Serialize(request.GrantedSubscriptions);
        definition.DefaultNetworkAccessJson = Serialize(request.GrantedNetworkAccess);
        definition.DefaultCapabilityBindingsJson = JsonSerializer.Serialize(request.CapabilityBindings, JsonOptions);
        definition.UpdatedAt = now;

        if (definition.Configuration is null)
        {
            definition.Configuration = new AgentDefinitionConfiguration
            {
                Id = Guid.NewGuid(), AgentDefinitionId = definition.Id, CreatedAt = now, Revision = 1
            };
        }
        else
        {
            definition.Configuration.Revision++;
        }
        definition.Configuration.SchemaVersion = NormalizeSchemaVersion(request.ConfigurationSchemaVersion);
        definition.Configuration.SettingsJson = JsonSerializer.Serialize(settings, JsonOptions);
        definition.Configuration.UpdatedAt = now;

        var configurationComplete = AgentConfigurationRules.HasAllRequired(manifest, settings);
        var builtAndSigned = package.Status == AgentPackageVersionStatus.Built &&
                             !string.IsNullOrWhiteSpace(package.PackageDigest) &&
                             !string.IsNullOrWhiteSpace(package.ArtifactSignature);
        definition.IsAvailableForHire = builtAndSigned && configurationComplete;
        definition.Status = builtAndSigned
            ? configurationComplete ? AgentDefinitionStatus.Available : AgentDefinitionStatus.NeedsConfiguration
            : AgentDefinitionStatus.Building;

        if (package.Status != AgentPackageVersionStatus.Built)
        {
            package.Status = AgentPackageVersionStatus.Approved;
            if (package.BuildJobs.Count == 0)
            {
                var job = new AgentBuildJob
                {
                    Id = Guid.NewGuid(), PackageVersionId = package.Id, Attempt = 1, QueuedAt = now
                };
                job.StepsJson = AgentBuildStepStore.CreateInitialJson(now);
                db.AgentBuildJobs.Add(job);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            "agent-definition.imported",
            nameof(AgentDefinition),
            definition.Id,
            $"Imported {package.AgentId} {package.Version} as a global definition; no runtime installation was created.",
            cancellationToken: cancellationToken);
        return ToResponse(definition, package);
    }

    public async Task<IReadOnlyList<AgentDefinitionResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var definitions = await Query().OrderBy(x => x.PackageVersion!.AgentName).ToListAsync(cancellationToken);
        return definitions.Select(x => ToResponse(x, x.PackageVersion!)).ToArray();
    }

    public async Task<AgentDefinitionResponse?> GetAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await Query().SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken);
        return definition is null ? null : ToResponse(definition, definition.PackageVersion!);
    }

    public async Task<AgentDefinitionResponse> UpdateAsync(
        Guid definitionId,
        UpdateAgentDefinitionRequest request,
        CancellationToken cancellationToken = default)
    {
        var definition = await db.AgentDefinitions
            .Include(x => x.Configuration)
            .Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken)
            ?? throw new AgentInstallationException("The agent definition was not found.");
        var currentPackage = definition.PackageVersion
            ?? throw new AgentInstallationException("The agent definition package was not found.");
        var nextPackage = await db.AgentPackageVersions
            .Include(x => x.BuildJobs)
            .SingleOrDefaultAsync(x => x.Id == request.PackageVersionId, cancellationToken)
            ?? throw new AgentInstallationException("The selected agent update is no longer available.");

        if (nextPackage.PackageSourceId != definition.PackageSourceId ||
            !string.Equals(nextPackage.AgentId, definition.AgentId, StringComparison.Ordinal))
        {
            throw new AgentInstallationException("The selected package is not an update for this agent definition.");
        }

        if (SemanticVersionComparer.Compare(nextPackage.Version, currentPackage.Version) <= 0)
            throw new AgentInstallationException("The selected package version is not newer than the installed definition.");

        if (nextPackage.Status is not (
                AgentPackageVersionStatus.Previewed or
                AgentPackageVersionStatus.Approved or
                AgentPackageVersionStatus.Built or
                AgentPackageVersionStatus.Failed))
        {
            throw new AgentInstallationException("The selected agent update is not available for installation.");
        }

        var manifest = AgentConfigurationRules.DeserializeManifest(nextPackage.ManifestJson);
        var settings = AgentConfigurationRules.GetManifestDefaults(manifest);
        var compatibleKeys = manifest.Configuration
            .Where(x => !x.Secret)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pair in DeserializeSettings(definition.Configuration?.SettingsJson ?? "{}")
                     .Where(x => compatibleKeys.Contains(x.Key)))
        {
            settings[pair.Key] = pair.Value.Clone();
        }
        await AgentConfigurationRules.ValidateAsync(db, manifest, settings, requireRequired: false,
            cancellationToken, modelCatalog, validateSupportedModels: true);

        var provided = manifest.Provides.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var required = AgentImportPreviewService.GrantRequiredCapabilities(manifest).ToHashSet(StringComparer.Ordinal);
        var subscriptions = manifest.Events.Subscribes.ToHashSet(StringComparer.Ordinal);
        var networkAccess = AgentImportPreviewService.WebGrantTokens(manifest).ToHashSet(StringComparer.Ordinal);

        if (nextPackage.Status != AgentPackageVersionStatus.Built)
        {
            nextPackage.Status = AgentPackageVersionStatus.Approved;
            await db.SaveChangesAsync(cancellationToken);
            await buildService.QueueAsync(nextPackage.Id, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        definition.PackageVersionId = nextPackage.Id;
        definition.PackageVersion = nextPackage;
        definition.UpdatedAt = now;
        // Selecting a new global agent definition is the approval boundary for that version's
        // declared capability and subscription contract. Existing scope and network policy remain
        // bounded separately; keeping only the old intersection would activate code whose required
        // contract can never run (for example a newly declared sprint-read capability).
        definition.DefaultProvidedCapabilitiesJson = Serialize(provided);
        definition.DefaultRequiredCapabilitiesJson = Serialize(required);
        definition.DefaultEventSubscriptionsJson = Serialize(subscriptions);
        definition.DefaultNetworkAccessJson = KeepGranted(definition.DefaultNetworkAccessJson, networkAccess);
        definition.DefaultCapabilityBindingsJson = MigrateBindings(
            definition.DefaultCapabilityBindingsJson, required);
        if (definition.Configuration is null)
        {
            definition.Configuration = new AgentDefinitionConfiguration
            {
                Id = Guid.NewGuid(), AgentDefinitionId = definition.Id, SchemaVersion = "1",
                Revision = 1, CreatedAt = now
            };
        }
        else
        {
            definition.Configuration.Revision++;
        }
        definition.Configuration.SettingsJson = JsonSerializer.Serialize(settings, JsonOptions);
        definition.Configuration.UpdatedAt = now;

        var configurationComplete = AgentConfigurationRules.HasAllRequired(manifest, settings);
        var builtAndSigned = nextPackage.Status == AgentPackageVersionStatus.Built &&
                             !string.IsNullOrWhiteSpace(nextPackage.PackageDigest) &&
                             !string.IsNullOrWhiteSpace(nextPackage.ArtifactSignature);
        definition.IsAvailableForHire = builtAndSigned && configurationComplete;
        definition.Status = builtAndSigned
            ? configurationComplete ? AgentDefinitionStatus.Available : AgentDefinitionStatus.NeedsConfiguration
            : AgentDefinitionStatus.Building;

        await db.SaveChangesAsync(cancellationToken);
        if (definition.Status == AgentDefinitionStatus.Available && definition.IsAvailableForHire)
        {
            try
            {
                await new AgentDefinitionInstallationSynchronizer(db, auditWriter)
                    .SynchronizeAsync(definition.Id, cancellationToken);
            }
            catch (Exception exception)
            {
                // The definition is the durable desired state. Runtime reconciliation retries every
                // hired installation, including those hosted by an Office that is currently offline.
                logger?.LogError(exception,
                    "Global definition {AgentDefinitionId} was updated, but existing hire deployment will be retried by runtime reconciliation.",
                    definition.Id);
            }
        }
        await auditWriter.WriteAsync(
            "agent-definition.update-requested",
            nameof(AgentDefinition),
            definition.Id,
            $"Updated global definition {definition.AgentId} from {currentPackage.Version} to {nextPackage.Version}; existing hires converge through durable deployment reconciliation.",
            cancellationToken: cancellationToken);
        return ToResponse(definition, nextPackage);
    }

    public async Task<RemoveAgentDefinitionResponse> RemoveAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await db.AgentDefinitions
            .Include(x => x.Configuration)
            .Include(x => x.PackageVersion)!.ThenInclude(x => x!.BuildJobs)
            .SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken)
            ?? throw new AgentInstallationException("The agent definition was not found.");
        var package = definition.PackageVersion
            ?? throw new AgentInstallationException("The agent definition package was not found.");
        var assignedEmployees = await db.CoreOrganizationUsers
            .AsNoTracking()
            .Where(x => x.AgentInstallation != null && x.AgentInstallation.AgentDefinitionId == definition.Id)
            .OrderBy(x => x.DisplayName)
            .Select(x => x.DisplayName)
            .ToListAsync(cancellationToken);
        if (assignedEmployees.Count > 0)
        {
            var names = string.Join(", ", assignedEmployees.Take(3));
            var remainder = assignedEmployees.Count > 3 ? $" and {assignedEmployees.Count - 3} more" : string.Empty;
            throw new AgentInstallationException(
                $"This agent definition is used by {assignedEmployees.Count} employee(s): {names}{remainder}. " +
                "Remove those employees from the Employees page before removing the agent definition.");
        }

        if (package.BuildJobs.Any(x => x.Status is AgentBuildStatus.Cloning or AgentBuildStatus.Building))
            throw new AgentInstallationException("The agent is currently building. Wait for the build to finish before removing it.");

        var hireOperations = await db.AgentHireOperations
            .Where(x => x.AgentDefinitionId == definition.Id)
            .ToListAsync(cancellationToken);
        var activeHire = hireOperations.FirstOrDefault(x => x.Status is
            AgentHireOperationStatus.Starting or
            AgentHireOperationStatus.Queued or
            AgentHireOperationStatus.Building or
            AgentHireOperationStatus.CompletingHire or
            AgentHireOperationStatus.AwaitingConfirmation);
        if (activeHire is not null)
        {
            throw new AgentInstallationException(
                "This agent definition still has an active hire operation. Wait for the hire to finish or cancel its pending review before removing the definition.");
        }

        var installationIds = await db.AgentInstallations
            .Where(x => x.AgentDefinitionId == definition.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (installationIds.Count > 0 && installationService is null)
        {
            throw new AgentInstallationException(
                "This agent definition is still used by an installation. Remove the related agent employee before removing the definition.");
        }
        foreach (var installationId in installationIds)
            await installationService!.RemoveAsync(installationId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var completedHire in hireOperations)
        {
            // Terminal hire operations are durable history, not live usages of the definition.
            // Once no employee remains assigned, detach them so the definition can be removed
            // and imported cleanly while preserving the operation record.
            completedHire.AgentDefinitionId = null;
            completedHire.DismissedAt ??= now;
            completedHire.UpdatedAt = now;
        }

        var removePackage = !await db.AgentInstallations.AnyAsync(
                                x => x.PackageVersionId == package.Id, cancellationToken) &&
                            !await db.AgentDefinitions.AnyAsync(
                                x => x.Id != definition.Id && x.PackageVersionId == package.Id, cancellationToken);
        var sourceId = package.PackageSourceId;
        var removeSource = removePackage && !await db.AgentPackageVersions.AnyAsync(
            x => x.PackageSourceId == sourceId && x.Id != package.Id, cancellationToken);

        foreach (var queuedJob in package.BuildJobs.Where(x => x.Status == AgentBuildStatus.Queued))
            queuedJob.TransitionTo(AgentBuildStatus.Cancelled, DateTimeOffset.UtcNow);

        db.AgentDefinitions.Remove(definition);
        if (removePackage)
            db.AgentPackageVersions.Remove(package);
        if (removeSource)
        {
            var source = await db.AgentPackageSources.SingleOrDefaultAsync(x => x.Id == sourceId, cancellationToken);
            if (source is not null)
                db.AgentPackageSources.Remove(source);
        }
        await db.SaveChangesAsync(cancellationToken);

        const int cleanupWarnings = 0;
        await auditWriter.WriteAsync(
            "agent-definition.removed",
            nameof(AgentDefinition),
            definition.Id,
            $"Removed global definition {package.AgentId} {package.Version}. Package removed: {removePackage}; source removed: {removeSource}.",
            cancellationToken: cancellationToken);
        return new RemoveAgentDefinitionResponse(definition.Id, removePackage, removeSource, cleanupWarnings);
    }

    public async Task<AgentDefinitionResponse> RetryBuildAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default)
    {
        var definition = await db.AgentDefinitions
            .Include(x => x.PackageVersion).ThenInclude(x => x!.BuildJobs)
            .SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken)
            ?? throw new AgentInstallationException("The agent definition was not found.");
        var package = definition.PackageVersion
            ?? throw new AgentInstallationException("The agent definition package was not found.");
        await buildService.QueueAsync(package.Id, cancellationToken);
        definition.Status = AgentDefinitionStatus.Building;
        definition.IsAvailableForHire = false;
        definition.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            "agent-definition.build-retry-requested",
            nameof(AgentDefinition),
            definition.Id,
            $"Queued another build for global definition {package.AgentId} {package.Version}.",
            cancellationToken: cancellationToken);
        return ToResponse(definition, package);
    }

    private IQueryable<AgentDefinition> Query() => db.AgentDefinitions.AsNoTracking()
        .Include(x => x.Configuration)
        .Include(x => x.PackageVersion).ThenInclude(x => x!.BuildJobs);

    private static AgentDefinitionResponse ToResponse(AgentDefinition definition, AgentPackageVersion package)
    {
        var build = package.BuildJobs.OrderByDescending(x => x.Attempt).FirstOrDefault();
        return new AgentDefinitionResponse(
            definition.Id, package.Id, package.AgentId, package.AgentName, package.Version, package.PublisherName,
            package.CommitSha, definition.Status.ToString(), definition.IsAvailableForHire,
            definition.DefaultActivationMode.ToString(), definition.DefaultTickFrequencySeconds,
            definition.DefaultOverlapPolicy.ToString(), definition.DefaultMaxRuntimeSeconds,
            definition.DefaultMemoryMb, definition.DefaultCpuPercent, definition.Configuration?.Revision ?? 0,
            definition.CreatedAt, definition.UpdatedAt,
            build is null ? null : new AgentBuildSummaryResponse(
                build.Id, build.Status.ToString(), build.Attempt, build.QueuedAt, build.StartedAt, build.CompletedAt,
                !string.IsNullOrWhiteSpace(build.LogPath), build.FailureMessage, AgentBuildStepStore.Read(build)));
    }

    private static void ValidateSubset(string name, IEnumerable<string> values, IEnumerable<string> permitted)
    {
        var allowed = permitted.ToHashSet(StringComparer.Ordinal);
        var invalid = values.FirstOrDefault(x => string.IsNullOrWhiteSpace(x) || !allowed.Contains(x));
        if (invalid is not null)
            throw new AgentInstallationException($"The {name} grant contains '{invalid}', which was not requested by the signed manifest.");
    }

    private static T ParseEnum<T>(string value, string label) where T : struct, Enum =>
        Enum.TryParse<T>(value, false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new AgentInstallationException($"The {label} is invalid.");

    private static Dictionary<string, JsonElement> DeserializeSettings(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions)
        ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static string Serialize(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values.Distinct(StringComparer.Ordinal).ToArray(), JsonOptions);

    private static string KeepGranted(string json, IReadOnlySet<string> allowed) =>
        Serialize((JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? []).Where(allowed.Contains));

    private static string MigrateBindings(string json, IReadOnlySet<string> allowed)
    {
        var bindings = JsonSerializer.Deserialize<Dictionary<string, Guid>>(json, JsonOptions)
                       ?? new Dictionary<string, Guid>(StringComparer.Ordinal);
        var migrated = bindings
            .Where(x => allowed.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        foreach (var capability in allowed.Where(x => !migrated.ContainsKey(x)))
        {
            var predecessor = bindings.FirstOrDefault(x =>
                string.Equals(CapabilityFamily(x.Key), CapabilityFamily(capability), StringComparison.Ordinal));
            if (!predecessor.Equals(default(KeyValuePair<string, Guid>)))
                migrated[capability] = predecessor.Value;
        }
        return JsonSerializer.Serialize(migrated, JsonOptions);
    }

    private static string CapabilityFamily(string capability)
    {
        var marker = capability.LastIndexOf(".v", StringComparison.Ordinal);
        return marker > 0 && int.TryParse(capability[(marker + 2)..], out _)
            ? capability[..marker]
            : capability;
    }

    private static string NormalizeSchemaVersion(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 64
            ? throw new AgentInstallationException("Configuration schema version is required and cannot exceed 64 characters.")
            : value.Trim();
}
