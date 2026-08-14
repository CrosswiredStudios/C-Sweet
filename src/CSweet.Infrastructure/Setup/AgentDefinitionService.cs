using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.AI.Providers;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentDefinitionService(
    CSweetDbContext db,
    IAuditEventWriter auditWriter,
    IAgentBuildService buildService,
    IModelCatalogClient? modelCatalog = null) : IAgentDefinitionService
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
        if (request.TickFrequencySeconds <= 0 || request.MaxRuntimeSeconds <= 0 ||
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

    private static string NormalizeSchemaVersion(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 64
            ? throw new AgentInstallationException("Configuration schema version is required and cannot exceed 64 characters.")
            : value.Trim();
}
