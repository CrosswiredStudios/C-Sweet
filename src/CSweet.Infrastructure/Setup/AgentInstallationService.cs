using System.Text.Json;
using System.Text;
using CSweet.Application.Setup;
using CSweet.SatelliteOffice.Contracts.Workloads;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentInstallationService : IAgentInstallationService, IPluginInstallationService
{
    private const int FirstPartyMinimumRuntimeMemoryMb = 1024;
    private static readonly TimeSpan RuntimeWorkloadCleanupTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CSweetDbContext _dbContext;
    private readonly IAuditEventWriter _auditWriter;
    private readonly IAgentBuildService _buildService;
    private readonly IAgentWorkloadRunner _workloads;
    private readonly AgentRuntimeManagerOptions _runtimeOptions;
    private readonly ILogger<AgentInstallationService> _logger;

    public AgentInstallationService(
        CSweetDbContext dbContext,
        IAuditEventWriter auditWriter,
        IAgentBuildService buildService,
        IAgentWorkloadRunner workloads,
        IOptions<AgentRuntimeManagerOptions> runtimeOptions,
        ILogger<AgentInstallationService> logger)
    {
        _dbContext = dbContext;
        _auditWriter = auditWriter;
        _buildService = buildService;
        _workloads = workloads;
        _runtimeOptions = runtimeOptions.Value;
        _logger = logger;
    }

    public async Task<AgentInstallationResponse> InstallAsync(
        Guid importId,
        InstallAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        var packageVersion = await _dbContext.AgentPackageVersions
            .SingleOrDefaultAsync(x => x.Id == importId, cancellationToken)
            ?? throw new AgentInstallationException("The import preview was not found.");
        if (packageVersion.PluginKind != PluginKind.Agent)
            throw new AgentInstallationException("Communication providers must be installed through the plugin API.");
        return await InstallCoreAsync(packageVersion, request, cancellationToken);
    }

    async Task<AgentInstallationResponse> IPluginInstallationService.InstallAsync(
        Guid importId,
        InstallAgentRequest request,
        CancellationToken cancellationToken)
    {
        var packageVersion = await _dbContext.AgentPackageVersions
            .SingleOrDefaultAsync(x => x.Id == importId, cancellationToken)
            ?? throw new AgentInstallationException("The import preview was not found.");
        if (packageVersion.PluginKind != PluginKind.Service)
            throw new AgentInstallationException(
                "Agent packages must be imported as global agent definitions through the agent API and hired before a runtime installation is created.");
        return await InstallCoreAsync(packageVersion, request, cancellationToken);
    }

    private async Task<AgentInstallationResponse> InstallCoreAsync(
        AgentPackageVersion packageVersion,
        InstallAgentRequest request,
        CancellationToken cancellationToken)
    {
        ValidateBusinessId(request.BusinessId);
        var businessId = request.BusinessId.Trim();
        var settings = await GetSettingsAsync(cancellationToken);

        if (!settings.EnableImportedAgents)
        {
            throw new AgentInstallationException("Imported agents are disabled in global runtime settings.");
        }

            if (packageVersion.Status is not (
                AgentPackageVersionStatus.Previewed or
                AgentPackageVersionStatus.Approved or
                AgentPackageVersionStatus.Built))
            {
                throw new AgentInstallationException("The imported agent version is not available for installation.");
            }

        var manifest = DeserializeManifest(packageVersion.ManifestJson);
        if (!manifest.Runtime.SupportsMultipleInstallations && await _dbContext.AgentInstallations.AnyAsync(
                x => x.BusinessId == businessId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active &&
                    x.PackageVersion!.AgentId == packageVersion.AgentId,
                cancellationToken))
        {
            throw new AgentInstallationException("This agent does not support multiple installations for the business.");
        }

        var activationMode = ParseActivationMode(request.ActivationMode);
        var scope = ParsePluginScope(request.PluginScope);
        if (packageVersion.PluginKind == PluginKind.Service &&
            (scope != PluginInstallationScope.System || activationMode != ActivationMode.AlwaysOn))
            throw new AgentInstallationException("Communication providers must be system-scoped and always-on.");
        var overlapPolicy = ParseOverlapPolicy(request.OverlapPolicy);
        var maxRuntimeSeconds = NormalizeMaxRuntimeSeconds(
            request.MaxRuntimeSeconds,
            settings,
            packageVersion.AgentId,
            businessId);
        ValidateSchedule(request.TickFrequencySeconds, maxRuntimeSeconds, activationMode, settings);
        ValidateResources(request.MemoryMb, request.CpuPercent, settings, packageVersion);
        ValidateGrant("provided capabilities", request.GrantedCapabilities, manifest.Provides.Select(x => x.Name).ToArray());
        ValidateGrant("required capabilities", request.GrantedRequestedCapabilities,
            AgentImportPreviewService.GrantRequiredCapabilities(manifest));
        ValidateGrant("subscriptions", request.GrantedSubscriptions, manifest.Events.Subscribes);
        if (request.GrantedPublications.Count > 0)
            throw new AgentInstallationException("Generic event publication grants are not supported in protocol v2.");
        if (request.GrantedPermissions.Count > 0)
            throw new AgentInstallationException("Legacy permission grants are not supported; grant typed required capabilities instead.");
        ValidateGrant("web access", request.GrantedNetworkAccess, AgentImportPreviewService.WebGrantTokens(manifest));
        if (manifest.WebAccess.Mode == PluginWebAccessMode.AllPublic &&
            request.GrantedNetworkAccess.Contains("all-public", StringComparer.Ordinal) &&
            !request.AllPublicWebAccessAcknowledged)
            throw new AgentInstallationException("All-public web access requires a separate explicit acknowledgement.");
        var configurationSettings = await ValidateConfigurationAsync(
            manifest,
            request.ConfigurationSettings,
            allowUnknownSettings: false,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var needsSetup = manifest.Setup?.Required == true;
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            PackageVersionId = packageVersion.Id,
            BusinessId = businessId,
            Scope = scope,
            IsEnabled = true,
            SetupState = needsSetup ? PluginSetupState.NeedsSetup : PluginSetupState.Ready,
            SetupFlowId = needsSetup ? manifest.Setup!.EntryFlow : null,
            SetupStepId = needsSetup ? manifest.Setup!.Flows.First(x => x.Id == manifest.Setup.EntryFlow).Steps.First().Id : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        installation.InstallationKey = installation.Id;
        var grant = new AgentInstallationGrant
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            NetworkAccessJson = SerializeGrant(request.GrantedNetworkAccess),
            MaxRuntimeSeconds = maxRuntimeSeconds,
            MemoryMb = request.MemoryMb,
            CpuPercent = request.CpuPercent,
            ProvidedCapabilitiesJson = SerializeGrant(request.GrantedCapabilities),
            RequiredCapabilitiesJson = SerializeGrant(request.GrantedRequestedCapabilities),
            EventSubscriptionsJson = SerializeGrant(request.GrantedSubscriptions),
            ResourceLimitsJson = JsonSerializer.Serialize(new
            {
                MaxRuntimeSeconds = maxRuntimeSeconds,
                request.MemoryMb,
                request.CpuPercent
            }),
            GrantRevision = 1,
            ApprovedAt = now
        };
        var schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ActivationMode = activationMode,
            TickFrequencySeconds = request.TickFrequencySeconds,
            NextTickAt = ComputeNextTick(activationMode, request.TickFrequencySeconds, now),
            MaxRuntimeSeconds = maxRuntimeSeconds,
            MaxRetriesPerTick = 0,
            OverlapPolicy = overlapPolicy,
            IsEnabled = true
        };

        var shouldQueueBuild = packageVersion.Status != AgentPackageVersionStatus.Built &&
            !await _dbContext.AgentBuildJobs.AnyAsync(
                x => x.PackageVersionId == packageVersion.Id,
                cancellationToken);
        if (packageVersion.Status != AgentPackageVersionStatus.Built)
        {
            packageVersion.Status = AgentPackageVersionStatus.Approved;
        }
        if (shouldQueueBuild)
        {
            var buildJob = new AgentBuildJob
            {
                Id = Guid.NewGuid(),
                PackageVersionId = packageVersion.Id,
                Attempt = 1,
                QueuedAt = now
            };
            buildJob.StepsJson = AgentBuildStepStore.CreateInitialJson(buildJob.QueuedAt);
            _dbContext.AgentBuildJobs.Add(buildJob);
        }
        installation.PackageVersion = packageVersion;
        installation.Grant = grant;
        installation.Schedule = schedule;
        if (manifest.Configuration.Any(field => !field.Secret))
        {
            installation.Configuration = CreateConfiguration(
                installation.Id,
                request.ConfigurationSchemaVersion,
                configurationSettings,
                now);
        }
        _dbContext.AgentInstallations.Add(installation);
        _dbContext.AgentCapabilityBindings.AddRange(await CreateCapabilityBindingsAsync(
            installation,
            request.GrantedRequestedCapabilities,
            request.CapabilityBindings,
            grant.GrantRevision,
            now,
            cancellationToken));
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditWriter.WriteAsync(
            "agent-installation.approved",
            nameof(AgentInstallation),
            installation.Id,
            $"Installed {packageVersion.AgentId} {packageVersion.Version} for business {businessId}.",
            null,
            cancellationToken);

        return ToResponse(installation);
    }

    public async Task<IReadOnlyList<AgentInstallationResponse>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var installations = await InstallationQuery()
            .Where(x => x.PackageVersion!.PluginKind == PluginKind.Agent)
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.PackageVersion!.AgentName)
            .ToListAsync(cancellationToken);
        return installations.Select(ToResponse).ToList();
    }

    async Task<IReadOnlyList<AgentInstallationResponse>> IPluginInstallationService.ListAsync(
        CancellationToken cancellationToken)
    {
        var installations = await InstallationQuery()
            .OrderBy(x => x.PackageVersion!.AgentName)
            .ThenBy(x => x.BusinessId)
            .ToListAsync(cancellationToken);
        return installations.Select(ToResponse).ToList();
    }

    public async Task<AgentInstallationResponse?> GetAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await InstallationQuery()
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        return installation is null ? null : ToResponse(installation);
    }

    async Task<AgentInstallationResponse?> IPluginInstallationService.GetAsync(
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var installation = await InstallationQuery()
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        return installation is null ? null : ToResponse(installation);
    }

    public async Task<AgentInstallationResponse> UpdateScheduleAsync(
        Guid installationId,
        UpdateAgentScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        var installation = await GetInstallationAsync(installationId, cancellationToken);
        var settings = await GetSettingsAsync(cancellationToken);
        var activationMode = ParseActivationMode(request.ActivationMode);
        var overlapPolicy = ParseOverlapPolicy(request.OverlapPolicy);
        var maxRuntimeSeconds = NormalizeMaxRuntimeSeconds(
            request.MaxRuntimeSeconds,
            settings,
            installation.PackageVersion!.AgentId,
            installation.BusinessId);
        ValidateSchedule(request.TickFrequencySeconds, maxRuntimeSeconds, activationMode, settings);

        if (maxRuntimeSeconds > installation.Grant!.MaxRuntimeSeconds)
        {
            throw new AgentInstallationException("Schedule max runtime cannot exceed the approved installation grant.");
        }

        var now = DateTimeOffset.UtcNow;
        installation.Schedule!.ActivationMode = activationMode;
        installation.Schedule.TickFrequencySeconds = request.TickFrequencySeconds;
        installation.Schedule.OverlapPolicy = overlapPolicy;
        installation.Schedule.MaxRuntimeSeconds = maxRuntimeSeconds;
        installation.Schedule.IsEnabled = request.IsEnabled;
        ResetAutomaticStartupFailures(installation.Schedule);
        installation.Schedule.NextTickAt = request.IsEnabled
            ? ComputeNextTick(activationMode, request.TickFrequencySeconds, now)
            : null;
        installation.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await WriteScheduleAuditAsync(installation, "agent-installation.schedule.updated", cancellationToken);
        return ToResponse(installation);
    }

    public async Task<AgentInstallationResponse> UpdateAsync(
        Guid installationId,
        UpdateAgentInstallationRequest request,
        CancellationToken cancellationToken = default)
    {
        var installation = await GetInstallationAsync(installationId, cancellationToken);
        var currentPackage = installation.PackageVersion!;
        var nextPackage = await _dbContext.AgentPackageVersions
            .Include(x => x.BuildJobs)
            .SingleOrDefaultAsync(x => x.Id == request.PackageVersionId, cancellationToken)
            ?? throw new AgentInstallationException("The selected agent update is no longer available.");

        if (nextPackage.PackageSourceId != currentPackage.PackageSourceId ||
            !string.Equals(nextPackage.AgentId, currentPackage.AgentId, StringComparison.Ordinal))
        {
            throw new AgentInstallationException("The selected package is not an update for this agent.");
        }

        if (await _dbContext.AgentInstallations.AnyAsync(
                x => x.Id != installation.Id &&
                     x.PackageVersionId == nextPackage.Id &&
                     x.BusinessId == installation.BusinessId,
                cancellationToken))
        {
            throw new AgentInstallationException(
                $"Agent version {nextPackage.Version} is already installed for business {installation.BusinessId}. Refresh the Agents page before trying again.");
        }

        if (SemanticVersionComparer.Compare(nextPackage.Version, currentPackage.Version) <= 0)
        {
            throw new AgentInstallationException("The selected package version is not newer than the installed version.");
        }

        if (nextPackage.Status is not (
                AgentPackageVersionStatus.Previewed or
                AgentPackageVersionStatus.Approved or
                AgentPackageVersionStatus.Built or
                AgentPackageVersionStatus.Failed))
        {
            throw new AgentInstallationException("The selected agent update is not available for installation.");
        }

        var nextManifest = DeserializeManifest(nextPackage.ManifestJson);
        var now = DateTimeOffset.UtcNow;
        var latestBuild = nextPackage.BuildJobs.OrderByDescending(x => x.Attempt).FirstOrDefault();
        var shouldQueueBuild = nextPackage.Status != AgentPackageVersionStatus.Built &&
            latestBuild?.Status is not (
                AgentBuildStatus.Queued or
                AgentBuildStatus.Cloning or
                AgentBuildStatus.Building);
        if (nextPackage.Status != AgentPackageVersionStatus.Built)
        {
            nextPackage.Status = AgentPackageVersionStatus.Approved;
        }
        if (shouldQueueBuild)
        {
            var buildJob = new AgentBuildJob
            {
                Id = Guid.NewGuid(),
                PackageVersionId = nextPackage.Id,
                Attempt = (latestBuild?.Attempt ?? 0) + 1,
                QueuedAt = now
            };
            buildJob.StepsJson = AgentBuildStepStore.CreateInitialJson(buildJob.QueuedAt);
            _dbContext.AgentBuildJobs.Add(buildJob);
        }

        var installationKey = installation.InstallationKey == Guid.Empty ? installation.Id : installation.InstallationKey;
        var nextRevisionNumber = await _dbContext.AgentInstallations
            .Where(x => x.InstallationKey == installationKey || x.Id == installationKey)
            .MaxAsync(x => (int?)x.RevisionNumber, cancellationToken) ?? installation.RevisionNumber;
        var staged = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = installationKey,
            RevisionNumber = nextRevisionNumber + 1,
            RevisionStatus = PluginRevisionStatus.Staged,
            SupersedesInstallationId = installation.Id,
            PackageVersionId = nextPackage.Id,
            PackageVersion = nextPackage,
            BusinessId = installation.BusinessId,
            Scope = installation.Scope,
            IsEnabled = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        staged.Grant = new AgentInstallationGrant
        {
            Id = Guid.NewGuid(), AgentInstallationId = staged.Id,
            NetworkAccessJson = "[]",
            ProvidedCapabilitiesJson = "[]",
            RequiredCapabilitiesJson = "[]",
            EventSubscriptionsJson = "[]",
            ResourceLimitsJson = "{}",
            MaxRuntimeSeconds = installation.Grant!.MaxRuntimeSeconds,
            MemoryMb = installation.Grant.MemoryMb, CpuPercent = installation.Grant.CpuPercent
        };
        staged.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(), AgentInstallationId = staged.Id,
            ActivationMode = installation.Schedule!.ActivationMode,
            TickFrequencySeconds = installation.Schedule.TickFrequencySeconds,
            MaxRuntimeSeconds = installation.Schedule.MaxRuntimeSeconds,
            MaxRetriesPerTick = installation.Schedule.MaxRetriesPerTick,
            OverlapPolicy = installation.Schedule.OverlapPolicy,
            IsEnabled = false,
            NextTickAt = null
        };
        var previousConfiguration = await _dbContext.AgentInstallationConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.AgentInstallationId == installation.Id, cancellationToken);
        if (nextManifest.Configuration.Any(field => !field.Secret))
        {
            var nextConfigurationKeys = nextManifest.Configuration
                .Where(field => !field.Secret)
                .Select(field => field.Key)
                .ToHashSet(StringComparer.Ordinal);
            var compatibleSettings = DeserializeConfigurationSettings(previousConfiguration?.SettingsJson)
                .Where(pair => nextConfigurationKeys.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            staged.Configuration = CreateConfiguration(
                staged.Id,
                previousConfiguration?.SchemaVersion ?? "1",
                compatibleSettings,
                now);
        }
        _dbContext.AgentInstallations.Add(staged);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            _logger.LogError(
                exception,
                "Could not update agent installation {AgentInstallationId} to package {PackageVersionId}.",
                staged.Id,
                nextPackage.Id);
            throw new AgentInstallationException(
                "The agent update could not be saved. Refresh the Agents page and try again; the installed version was not changed.",
                exception);
        }

        await _auditWriter.WriteAsync(
            "plugin-update.staged",
            nameof(AgentInstallation),
            staged.Id,
            $"Staged {currentPackage.AgentId} revision {staged.RevisionNumber} for business {installation.BusinessId}; all grants are empty pending approval.",
            null,
            cancellationToken);

        return ToResponse(staged);
    }

    public async Task<AgentInstallationResponse> ApproveUpdateAsync(
        Guid stagedRevisionId,
        InstallAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        var staged = await GetInstallationAsync(stagedRevisionId, cancellationToken);
        if (staged.RevisionStatus != PluginRevisionStatus.Staged || staged.SupersedesInstallationId is null)
            throw new AgentInstallationException("Only a staged plugin revision can be approved.");
        if (staged.PackageVersion!.Status != AgentPackageVersionStatus.Built)
            throw new AgentInstallationException("The staged package must finish verification and build before approval.");
        if (!string.Equals(request.BusinessId.Trim(), staged.BusinessId, StringComparison.Ordinal))
            throw new AgentInstallationException("The approval business must match the staged revision.");

        var manifest = DeserializeManifest(staged.PackageVersion.ManifestJson);
        var settings = await GetSettingsAsync(cancellationToken);
        var activation = ParseActivationMode(request.ActivationMode);
        var overlap = ParseOverlapPolicy(request.OverlapPolicy);
        var maxRuntimeSeconds = NormalizeMaxRuntimeSeconds(
            request.MaxRuntimeSeconds,
            settings,
            staged.PackageVersion.AgentId,
            staged.BusinessId);
        ValidateSchedule(request.TickFrequencySeconds, maxRuntimeSeconds, activation, settings);
        ValidateResources(request.MemoryMb, request.CpuPercent, settings, staged.PackageVersion);
        ValidateGrant("provided capabilities", request.GrantedCapabilities, manifest.Provides.Select(x => x.Name).ToArray());
        ValidateGrant("required capabilities", request.GrantedRequestedCapabilities,
            AgentImportPreviewService.GrantRequiredCapabilities(manifest));
        ValidateGrant("subscriptions", request.GrantedSubscriptions, manifest.Events.Subscribes);
        if (request.GrantedPublications.Count > 0)
            throw new AgentInstallationException("Generic event publication grants are not supported in protocol v2.");
        if (request.GrantedPermissions.Count > 0)
            throw new AgentInstallationException("Legacy permission grants are not supported.");
        ValidateGrant("web access", request.GrantedNetworkAccess, AgentImportPreviewService.WebGrantTokens(manifest));
        if (request.GrantedNetworkAccess.Contains("all-public", StringComparer.Ordinal) && !request.AllPublicWebAccessAcknowledged)
            throw new AgentInstallationException("All-public web access requires a separate explicit acknowledgement.");
        var inheritedSettings = DeserializeConfigurationSettings(staged.Configuration?.SettingsJson);
        var requestedSettings = request.ConfigurationSettings.Count == 0
            ? inheritedSettings
            : inheritedSettings
                .Concat(request.ConfigurationSettings)
                .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var configurationSettings = await ValidateConfigurationAsync(
            manifest,
            requestedSettings,
            allowUnknownSettings: true,
            cancellationToken);

        var previous = await GetInstallationAsync(staged.SupersedesInstallationId.Value, cancellationToken);
        await RemoveRuntimeWorkloadsAsync(previous, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        previous.IsEnabled = false;
        previous.RevisionStatus = PluginRevisionStatus.Retired;
        previous.Schedule!.IsEnabled = false;
        previous.Schedule.NextTickAt = null;
        previous.UpdatedAt = now;
        await RevokeInstallationSessionsAsync(
            previous.Id,
            "The approved package revision changed.",
            now,
            cancellationToken);

        var grant = staged.Grant!;
        grant.NetworkAccessJson = SerializeGrant(request.GrantedNetworkAccess);
        grant.ProvidedCapabilitiesJson = SerializeGrant(request.GrantedCapabilities);
        grant.RequiredCapabilitiesJson = SerializeGrant(request.GrantedRequestedCapabilities);
        grant.EventSubscriptionsJson = SerializeGrant(request.GrantedSubscriptions);
        grant.ResourceLimitsJson = JsonSerializer.Serialize(new
        {
            MaxRuntimeSeconds = maxRuntimeSeconds,
            request.MemoryMb,
            request.CpuPercent
        });
        grant.GrantRevision++;
        grant.MaxRuntimeSeconds = maxRuntimeSeconds;
        grant.MemoryMb = request.MemoryMb;
        grant.CpuPercent = request.CpuPercent;
        grant.ApprovedAt = now;
        staged.Schedule!.ActivationMode = activation;
        staged.Schedule.TickFrequencySeconds = request.TickFrequencySeconds;
        staged.Schedule.MaxRuntimeSeconds = maxRuntimeSeconds;
        staged.Schedule.OverlapPolicy = overlap;
        staged.Schedule.IsEnabled = true;
        staged.Schedule.NextTickAt = ComputeNextTick(activation, request.TickFrequencySeconds, now);
        staged.IsEnabled = true;
        staged.RevisionStatus = PluginRevisionStatus.Active;
        staged.UpdatedAt = now;
        if (manifest.Configuration.Any(field => !field.Secret))
        {
            staged.Configuration ??= CreateConfiguration(
                staged.Id,
                request.ConfigurationSchemaVersion,
                configurationSettings,
                now);
            staged.Configuration.SchemaVersion = NormalizeConfigurationSchemaVersion(
                request.ConfigurationSchemaVersion,
                staged.Configuration.SchemaVersion);
            staged.Configuration.SettingsJson = JsonSerializer.Serialize(configurationSettings, SerializerOptions);
            staged.Configuration.UpdatedAt = now;
        }
        var previousBindings = await _dbContext.AgentCapabilityBindings
            .Where(x => x.RequesterInstallationId == previous.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var binding in previousBindings)
            binding.RevokedAt = now;
        _dbContext.AgentCapabilityBindings.AddRange(await CreateCapabilityBindingsAsync(
            staged,
            request.GrantedRequestedCapabilities,
            request.CapabilityBindings,
            grant.GrantRevision,
            now,
            cancellationToken));
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _auditWriter.WriteAsync("plugin-update.approved", nameof(AgentInstallation), staged.Id,
            $"Activated plugin revision {staged.RevisionNumber} after complete grant reapproval.", null, cancellationToken);
        return ToResponse(staged);
    }

    private async Task<IReadOnlyList<AgentCapabilityBinding>> CreateCapabilityBindingsAsync(
        AgentInstallation requester,
        IReadOnlyList<string> grantedRequiredCapabilities,
        IReadOnlyDictionary<string, Guid> selections,
        long grantRevision,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken)
    {
        var candidates = await _dbContext.AgentInstallations.AsNoTracking()
            .Where(x => x.BusinessId == requester.BusinessId &&
                        x.IsEnabled &&
                        x.RevisionStatus == PluginRevisionStatus.Active)
            .Include(x => x.PackageVersion)
            .ToListAsync(cancellationToken);
        var providedByInstallation = candidates.ToDictionary(
            x => x.Id,
            x => DeserializeManifest(x.PackageVersion!.ManifestJson).Provides
                .Select(capability => capability.Name)
                .ToHashSet(StringComparer.Ordinal));
        var bindings = new List<AgentCapabilityBinding>();
        foreach (var capability in grantedRequiredCapabilities.Distinct(StringComparer.Ordinal))
        {
            var providers = candidates
                .Where(x => providedByInstallation[x.Id].Contains(capability))
                .ToList();
            if (providers.Count == 0)
            {
                if (selections.ContainsKey(capability))
                    throw new AgentInstallationException(
                        $"Capability binding '{capability}' does not identify an active provider in this organization.");
                continue;
            }

            AgentInstallation provider;
            if (selections.TryGetValue(capability, out var selectedId))
            {
                provider = providers.SingleOrDefault(x => x.Id == selectedId)
                    ?? throw new AgentInstallationException(
                        $"Selected provider for '{capability}' is not active in this organization or does not provide it.");
            }
            else if (providers.Count == 1)
            {
                provider = providers[0];
            }
            else
            {
                throw new AgentInstallationException(
                    $"Capability '{capability}' has multiple providers. Select one installation explicitly.");
            }

            bindings.Add(new AgentCapabilityBinding
            {
                Id = Guid.NewGuid(),
                OrganizationId = requester.BusinessId,
                RequesterInstallationId = requester.Id,
                Capability = capability,
                ProviderInstallationId = provider.Id,
                GrantRevision = grantRevision,
                ApprovedAt = approvedAt
            });
        }

        var unknownSelections = selections.Keys
            .Except(grantedRequiredCapabilities, StringComparer.Ordinal)
            .ToList();
        if (unknownSelections.Count > 0)
            throw new AgentInstallationException(
                $"Capability binding selections are not granted: {string.Join(", ", unknownSelections)}.");
        return bindings;
    }

    public async Task<AgentInstallationResponse> RunNowAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await GetInstallationAsync(installationId, cancellationToken);
        if (!installation.IsEnabled || !installation.Schedule!.IsEnabled)
        {
            throw new AgentInstallationException("The agent installation and schedule must be enabled to run now.");
        }
        if (installation.Schedule.ActivationMode == ActivationMode.AlwaysOn)
        {
            throw new AgentInstallationException("Run Now is unavailable for always-on agents because they start automatically.");
        }

        var now = DateTimeOffset.UtcNow;
        installation.Schedule.RunRequestedAt = now;
        installation.Schedule.NextTickAt = now;
        installation.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await WriteScheduleAuditAsync(installation, "agent-installation.run-requested", cancellationToken);
        return ToResponse(installation);
    }

    public async Task<AgentInstallationResponse> RetryBuildAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await GetInstallationAsync(installationId, cancellationToken);
        var package = installation.PackageVersion!;
        if (package.Status is not (
                AgentPackageVersionStatus.Approved or
                AgentPackageVersionStatus.Failed))
        {
            throw new AgentInstallationException(
                package.Status == AgentPackageVersionStatus.Built
                    ? "The agent package is already built."
                    : $"A package in status {package.Status} cannot be retried.");
        }

        var buildJobId = await _buildService.QueueAsync(package.Id, cancellationToken);
        if (package.BuildJobs.All(x => x.Id != buildJobId))
        {
            package.BuildJobs.Add(await _dbContext.AgentBuildJobs
                .SingleAsync(x => x.Id == buildJobId, cancellationToken));
        }

        var now = DateTimeOffset.UtcNow;
        var schedule = installation.Schedule!;
        ResetAutomaticStartupFailures(schedule);
        if (installation.IsEnabled &&
            schedule.IsEnabled &&
            schedule.ActivationMode == ActivationMode.AlwaysOn)
        {
            schedule.NextTickAt = now;
        }
        installation.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _auditWriter.WriteAsync(
            "agent-build.retry-requested",
            nameof(AgentInstallation),
            installation.Id,
            $"Queued another build for {package.AgentId} {package.Version} and cleared automatic startup suppression.",
            cancellationToken: cancellationToken);
        return ToResponse(installation);
    }

    public async Task<AgentInstallationResponse> RetryStartupAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await GetInstallationAsync(installationId, cancellationToken);
        var schedule = installation.Schedule!;
        if (!installation.IsEnabled || !schedule.IsEnabled)
            throw new AgentInstallationException("The agent installation and schedule must be enabled to try startup again.");
        if (installation.PackageVersion!.Status != AgentPackageVersionStatus.Built)
            throw new AgentInstallationException("The agent package must be built before startup can be retried.");
        if (schedule.ActivationMode != ActivationMode.AlwaysOn)
            throw new AgentInstallationException("Startup retry is only available for always-on agents.");

        var now = DateTimeOffset.UtcNow;
        ResetAutomaticStartupFailures(schedule);
        schedule.NextTickAt = now;
        installation.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _auditWriter.WriteAsync(
            "agent-runtime.startup-retry-requested",
            nameof(AgentInstallation),
            installation.Id,
            $"Cleared automatic startup suppression and queued another startup attempt for {installation.PackageVersion.AgentId}.",
            cancellationToken: cancellationToken);
        return ToResponse(installation);
    }

    public async Task<AgentInstallationResponse> DisableAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await GetInstallationAsync(installationId, cancellationToken);
        installation.IsEnabled = false;
        installation.Schedule!.IsEnabled = false;
        installation.Schedule.NextTickAt = null;
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        await RevokeInstallationSessionsAsync(
            installation.Id,
            "The installation was disabled.",
            installation.UpdatedAt,
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await WriteScheduleAuditAsync(installation, "agent-installation.disabled", cancellationToken);
        return ToResponse(installation);
    }

    public async Task<AgentInstallationResponse> EnableAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await GetInstallationAsync(installationId, cancellationToken);
        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.EnableImportedAgents)
            throw new AgentInstallationException("Imported agents are disabled in global runtime settings.");
        var manifest = DeserializeManifest(installation.PackageVersion!.ManifestJson);
        await ValidateConfigurationAsync(
            manifest,
            DeserializeConfigurationSettings(installation.Configuration?.SettingsJson),
            allowUnknownSettings: true,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        installation.IsEnabled = true;
        installation.Schedule!.IsEnabled = true;
        ResetAutomaticStartupFailures(installation.Schedule);
        installation.Schedule.NextTickAt = ComputeNextTick(
            installation.Schedule.ActivationMode,
            installation.Schedule.TickFrequencySeconds,
            now);
        installation.UpdatedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await WriteScheduleAuditAsync(installation, "agent-installation.enabled", cancellationToken);
        return ToResponse(installation);
    }

    public async Task<RemoveAgentInstallationResponse> RemoveAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var installation = await InstallationQuery()
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken)
            ?? throw new AgentInstallationException("The agent installation was not found.");
        var package = installation.PackageVersion!;
        var assignedEmployees = await _dbContext.CoreOrganizationUsers
            .AsNoTracking()
            .Where(x => x.AgentInstallationId == installation.Id)
            .OrderBy(x => x.DisplayName)
            .Select(x => x.DisplayName)
            .ToListAsync(cancellationToken);
        if (assignedEmployees.Count > 0)
        {
            var names = string.Join(", ", assignedEmployees.Take(3));
            var remainder = assignedEmployees.Count > 3
                ? $" and {assignedEmployees.Count - 3} more"
                : string.Empty;
            throw new AgentInstallationException(
                $"This agent is assigned to {assignedEmployees.Count} employee(s): {names}{remainder}. " +
                "Remove those employees from the Employees page before removing the agent installation.");
        }
        var removePackage = !await _dbContext.AgentInstallations.AnyAsync(
            x => x.PackageVersionId == package.Id && x.Id != installation.Id,
            cancellationToken);

        if (removePackage && package.BuildJobs.Any(
                x => x.Status is AgentBuildStatus.Cloning or AgentBuildStatus.Building))
        {
            throw new AgentInstallationException(
                "The agent is currently building. Wait for the build to finish before removing it.");
        }

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        installation.IsEnabled = false;
        if (installation.Schedule is not null)
        {
            installation.Schedule.IsEnabled = false;
            installation.Schedule.NextTickAt = null;
        }
        installation.UpdatedAt = DateTimeOffset.UtcNow;
        await RevokeInstallationSessionsAsync(
            installation.Id,
            "The installation was removed.",
            installation.UpdatedAt,
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await RemoveRuntimeWorkloadsAsync(installation, cancellationToken);
        await RemoveAgentWorkHistoryAsync(installation.Id, cancellationToken);

        var sourceId = package.PackageSourceId;
        var removeSource = removePackage && !await _dbContext.AgentPackageVersions.AnyAsync(
            x => x.PackageSourceId == sourceId && x.Id != package.Id,
            cancellationToken);

        if (removePackage)
        {
            foreach (var queuedJob in package.BuildJobs.Where(x => x.Status == AgentBuildStatus.Queued))
            {
                queuedJob.TransitionTo(AgentBuildStatus.Cancelled, DateTimeOffset.UtcNow);
            }
        }

        _dbContext.AgentInstallations.Remove(installation);
        if (removePackage)
        {
            _dbContext.AgentPackageVersions.Remove(package);
            if (removeSource)
            {
                var source = await _dbContext.AgentPackageSources
                    .SingleOrDefaultAsync(x => x.Id == sourceId, cancellationToken);
                if (source is not null)
                {
                    _dbContext.AgentPackageSources.Remove(source);
                }
            }
        }
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception)
        {
            _logger.LogError(
                exception,
                "Could not remove agent installation {AgentInstallationId} because it is still referenced.",
                installation.Id);
            throw new AgentInstallationException(
                "The agent could not be removed because related records still reference it. Refresh Agents and try again. If the problem continues, check the server log for the blocking record.",
                exception);
        }

        // Artifact and source locators are content-addressed broker references. They
        // are never interpreted as host paths; store-level garbage collection owns
        // physical deletion once no persisted reference remains.
        const int cleanupWarnings = 0;
        await _auditWriter.WriteAsync(
            "agent-installation.removed",
            nameof(AgentInstallation),
            installation.Id,
            $"Removed {package.AgentId} {package.Version} from business {installation.BusinessId}. " +
            $"Package removed: {removePackage}; source removed: {removeSource}; cleanup warnings: {cleanupWarnings}.",
            null,
            cancellationToken);

        return new RemoveAgentInstallationResponse(
            installation.Id,
            removePackage,
            removeSource,
            cleanupWarnings);
    }

    public async Task<IReadOnlyList<AgentRuntimeRunResponse>> ListRunsAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        if (!await _dbContext.AgentInstallations.AnyAsync(x => x.Id == installationId, cancellationToken))
            throw new AgentInstallationException("The agent installation was not found.");
        var runs = await _dbContext.AgentRuntimeInstances.AsNoTracking()
            .Include(x => x.Events)
            .Where(x => x.AgentInstallationId == installationId)
            .OrderByDescending(x => x.QueuedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
        var settings = await GetSettingsAsync(cancellationToken);
        var maximumLogBytes = Math.Min(settings.DefaultWorkloadLogLimitMb * 1024 * 1024, 64 * 1024);
        foreach (var run in runs.Where(run => !string.IsNullOrWhiteSpace(run.ProviderInstanceId)))
        {
            try
            {
                run.LogExcerpt = await _workloads.GetLogsAsync(
                    RuntimeHandle(run),
                    maximumLogBytes,
                    cancellationToken);
            }
            catch (AgentWorkloadException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Could not read live isolated-workload output for runtime {RuntimeInstanceId}.",
                    run.Id);
            }
        }
        return runs.Select(ToRunResponse).ToList();
    }

    public async Task<AgentBuildLogResponse?> GetBuildLogAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        var job = await _dbContext.AgentInstallations.AsNoTracking()
            .Where(x => x.Id == installationId)
            .SelectMany(x => x.PackageVersion!.BuildJobs)
            .OrderByDescending(x => x.Attempt)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null) return null;
        if (string.IsNullOrWhiteSpace(job.LogPath) || !File.Exists(job.LogPath))
            return new AgentBuildLogResponse(
                job.Id,
                job.Status.ToString(),
                FormatPersistedBuildDiagnostics(job),
                false);
        var settings = await GetSettingsAsync(cancellationToken);
        var maximumBytes = checked(settings.MaximumBuildLogMb * 1024 * 1024);
        await using var stream = new FileStream(job.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
        var length = (int)Math.Min(stream.Length, maximumBytes);
        var bytes = new byte[length];
        var read = await stream.ReadAtLeastAsync(bytes, length, throwOnEndOfStream: false, cancellationToken: cancellationToken);
        return new AgentBuildLogResponse(job.Id, job.Status.ToString(), System.Text.Encoding.UTF8.GetString(bytes, 0, read), stream.Length > maximumBytes);
    }

    private static string FormatPersistedBuildDiagnostics(AgentBuildJob job)
    {
        var output = new StringBuilder();
        output.AppendLine($"Build job: {job.Id:D}");
        output.AppendLine($"Status: {job.Status}");
        output.AppendLine($"Attempt: {job.Attempt}");
        output.AppendLine($"Queued: {job.QueuedAt:O}");
        output.AppendLine($"Started: {job.StartedAt?.ToString("O") ?? "not started"}");
        output.AppendLine($"Completed: {job.CompletedAt?.ToString("O") ?? "not completed"}");
        if (!string.IsNullOrWhiteSpace(job.LogPath))
            output.AppendLine($"Guest log locator: {job.LogPath}");
        if (!string.IsNullOrWhiteSpace(job.FailureMessage))
            output.AppendLine($"Failure: {job.FailureMessage}");
        output.AppendLine();
        output.AppendLine("Build steps:");
        foreach (var step in AgentBuildStepStore.Read(job))
        {
            output.Append("- ").Append(step.Label).Append(": ").AppendLine(step.Status);
            if (!string.IsNullOrWhiteSpace(step.Detail))
                output.Append("  Detail: ").AppendLine(step.Detail);
            if (!string.IsNullOrWhiteSpace(step.Error))
                output.Append("  Error: ").AppendLine(step.Error);
            if (step.StartedAt is not null)
                output.Append("  Started: ").AppendLine(step.StartedAt.Value.ToString("O"));
            if (step.CompletedAt is not null)
                output.Append("  Completed: ").AppendLine(step.CompletedAt.Value.ToString("O"));
        }
        output.AppendLine();
        output.AppendLine(
            "RuntimeHost request IDs in failures correlate with the Windows Application event log source CSweet.SatelliteOffice.RuntimeHost.");
        return output.ToString();
    }

    private IQueryable<AgentInstallation> InstallationQuery() =>
        _dbContext.AgentInstallations
            .Include(x => x.PackageVersion)!.ThenInclude(x => x!.BuildJobs)
            .Include(x => x.Grant)
            .Include(x => x.Schedule)
            .Include(x => x.Configuration)
            .Include(x => x.RuntimeInstances).ThenInclude(x => x.Events);

    private async Task<AgentInstallation> GetInstallationAsync(
        Guid installationId,
        CancellationToken cancellationToken) =>
        await InstallationQuery().SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken)
            ?? throw new AgentInstallationException("The agent installation was not found.");

    private async Task<AgentRuntimeGlobalSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        await _dbContext.AgentRuntimeGlobalSettings.SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgentInstallationException("Agent runtime settings have not been seeded.");

    private async Task RemoveRuntimeWorkloadsAsync(
        AgentInstallation installation,
        CancellationToken cancellationToken)
    {
        using var cleanupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanupCancellation.CancelAfter(RuntimeWorkloadCleanupTimeout);

        foreach (var runtime in installation.RuntimeInstances)
        {
            if (string.IsNullOrWhiteSpace(runtime.IsolationProviderId) ||
                string.IsNullOrWhiteSpace(runtime.ProviderInstanceId))
                continue;
            var handle = RuntimeHandle(runtime);
            try
            {
                if (await _workloads.InspectAsync(handle, cleanupCancellation.Token) is not null)
                {
                    await _workloads.DestroyAsync(handle, cleanupCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AgentInstallationException(
                    "Timed out while destroying an isolated agent workload. Check RuntimeHost and try again.");
            }
            catch (AgentWorkloadException exception)
            {
                throw new AgentInstallationException(
                    $"The isolated runtime workload could not be destroyed. The installation was disabled and can be removed again: {exception.Message}");
            }
        }
    }

    private async Task RemoveAgentWorkHistoryAsync(
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var workItemIds = _dbContext.AgentWorkItems
            .Where(x => x.AgentInstallationId == installationId)
            .Select(x => x.Id);

        if (_dbContext.Database.IsRelational())
        {
            await _dbContext.AgentWorkProgress
                .Where(x => workItemIds.Contains(x.AgentWorkItemId))
                .ExecuteDeleteAsync(cancellationToken);
            await _dbContext.AgentWorkAttempts
                .Where(x => workItemIds.Contains(x.AgentWorkItemId))
                .ExecuteDeleteAsync(cancellationToken);
            await _dbContext.AgentWorkItems
                .Where(x => x.AgentInstallationId == installationId)
                .ExecuteDeleteAsync(cancellationToken);
            return;
        }

        _dbContext.AgentWorkProgress.RemoveRange(await _dbContext.AgentWorkProgress
            .Where(x => workItemIds.Contains(x.AgentWorkItemId))
            .ToListAsync(cancellationToken));
        _dbContext.AgentWorkAttempts.RemoveRange(await _dbContext.AgentWorkAttempts
            .Where(x => workItemIds.Contains(x.AgentWorkItemId))
            .ToListAsync(cancellationToken));
        _dbContext.AgentWorkItems.RemoveRange(await _dbContext.AgentWorkItems
            .Where(x => x.AgentInstallationId == installationId)
            .ToListAsync(cancellationToken));
    }

    private async Task WriteScheduleAuditAsync(
        AgentInstallation installation,
        string eventType,
        CancellationToken cancellationToken) =>
        await _auditWriter.WriteAsync(
            eventType,
            nameof(AgentInstallation),
            installation.Id,
            $"Updated {installation.PackageVersion!.AgentId} for business {installation.BusinessId}.",
            null,
            cancellationToken);

    private static PluginManifest DeserializeManifest(string manifestJson)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(manifestJson, SerializerOptions)
                ?? throw new AgentInstallationException("The stored plugin manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new AgentInstallationException($"The stored plugin manifest is invalid: {exception.Message}");
        }
    }

    private static IsolationWorkloadHandle RuntimeHandle(AgentRuntimeInstance runtime) => new(
        runtime.IsolationProviderId ?? throw new InvalidOperationException("The runtime isolation provider is missing."),
        runtime.Id,
        runtime.ProviderInstanceId ?? throw new InvalidOperationException("The runtime provider instance is missing."),
        WorkloadKind.Runtime);

    private async Task<IReadOnlyDictionary<string, JsonElement>> ValidateConfigurationAsync(
        PluginManifest manifest,
        IReadOnlyDictionary<string, JsonElement> settings,
        bool allowUnknownSettings,
        CancellationToken cancellationToken)
    {
        var publicFields = manifest.Configuration
            .Where(field => !field.Secret)
            .ToDictionary(field => field.Key, StringComparer.Ordinal);
        var unknownKeys = settings.Keys
            .Where(key => !publicFields.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!allowUnknownSettings && unknownKeys.Length > 0)
        {
            throw new AgentInstallationException(
                $"Agent configuration contains unsupported setting(s): {string.Join(", ", unknownKeys)}.");
        }

        foreach (var field in publicFields.Values)
        {
            var hasValue = settings.TryGetValue(field.Key, out var value) && HasConfigurationValue(value);
            if (field.Required && !hasValue)
            {
                throw new AgentInstallationException(
                    $"'{field.Label}' ({field.Key}) is required before this agent can be installed or enabled.");
            }
            if (!hasValue)
                continue;

            var type = field.Type.Trim();
            if (type.Equals("boolean", StringComparison.OrdinalIgnoreCase) &&
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new AgentInstallationException($"'{field.Label}' must be true or false.");
            if (type.Equals("number", StringComparison.OrdinalIgnoreCase) &&
                value.ValueKind != JsonValueKind.Number)
                throw new AgentInstallationException($"'{field.Label}' must be a number.");
            if (type.Equals(AgentConfigurationFieldTypes.Select, StringComparison.OrdinalIgnoreCase) &&
                field.Options is { Count: > 0 } &&
                (value.ValueKind != JsonValueKind.String ||
                 !field.Options.Any(option =>
                     string.Equals(option.Value, value.GetString(), StringComparison.Ordinal))))
            {
                throw new AgentInstallationException(
                    $"'{field.Label}' must be one of the values declared by the agent.");
            }
            if (IsProviderConfigurationType(type))
            {
                if (value.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(value.GetString(), out var providerId) ||
                    !await _dbContext.LlmProviderProfiles.AsNoTracking().AnyAsync(
                        provider => provider.Id == providerId && provider.IsEnabled,
                        cancellationToken))
                {
                    throw new AgentInstallationException(
                        $"'{field.Label}' must reference an enabled LLM provider.");
                }
            }
            else if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                throw new AgentInstallationException(
                    $"'{field.Label}' must be a scalar configuration value.");
            }

            ValidateCapabilitySchemaConstraints(manifest, field, value);
        }

        return settings
            .Where(pair => publicFields.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
    }

    private static void ValidateCapabilitySchemaConstraints(
        PluginManifest manifest,
        PluginConfigurationField field,
        JsonElement value)
    {
        var update = manifest.Provides.SingleOrDefault(capability =>
            capability.Name.Equals(AgentConfigurationCapabilities.Update, StringComparison.Ordinal));
        if (update is null ||
            update.InputSchema.ValueKind != JsonValueKind.Object ||
            !update.InputSchema.TryGetProperty("properties", out var inputProperties) ||
            !inputProperties.TryGetProperty("settings", out var settingsSchema) ||
            settingsSchema.ValueKind != JsonValueKind.Object ||
            !settingsSchema.TryGetProperty("properties", out var settingsProperties) ||
            !settingsProperties.TryGetProperty(field.Key, out var fieldSchema) ||
            fieldSchema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (fieldSchema.TryGetProperty("type", out var typeSchema) &&
            !SchemaAllowsValue(typeSchema, value))
        {
            throw new AgentInstallationException(
                $"'{field.Label}' does not match the type declared by the agent configuration schema.");
        }

        if (fieldSchema.TryGetProperty("enum", out var allowedValues) &&
            allowedValues.ValueKind == JsonValueKind.Array &&
            !allowedValues.EnumerateArray().Any(allowed =>
                string.Equals(allowed.GetRawText(), value.GetRawText(), StringComparison.Ordinal)))
        {
            throw new AgentInstallationException(
                $"'{field.Label}' must be one of the values declared by the agent.");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            if (fieldSchema.TryGetProperty("minimum", out var minimum) &&
                minimum.TryGetDecimal(out var minimumValue) &&
                number < minimumValue)
            {
                throw new AgentInstallationException(
                    $"'{field.Label}' must be greater than or equal to {minimumValue}.");
            }
            if (fieldSchema.TryGetProperty("maximum", out var maximum) &&
                maximum.TryGetDecimal(out var maximumValue) &&
                number > maximumValue)
            {
                throw new AgentInstallationException(
                    $"'{field.Label}' must be less than or equal to {maximumValue}.");
            }
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
        {
            if (fieldSchema.TryGetProperty("minLength", out var minimumLength) &&
                minimumLength.TryGetInt32(out var minimumLengthValue) &&
                text.Length < minimumLengthValue)
            {
                throw new AgentInstallationException(
                    $"'{field.Label}' must contain at least {minimumLengthValue} characters.");
            }
            if (fieldSchema.TryGetProperty("maxLength", out var maximumLength) &&
                maximumLength.TryGetInt32(out var maximumLengthValue) &&
                text.Length > maximumLengthValue)
            {
                throw new AgentInstallationException(
                    $"'{field.Label}' cannot exceed {maximumLengthValue} characters.");
            }
        }
    }

    private static bool SchemaAllowsValue(JsonElement typeSchema, JsonElement value)
    {
        if (typeSchema.ValueKind == JsonValueKind.String)
            return MatchesSchemaType(typeSchema.GetString(), value);
        return typeSchema.ValueKind == JsonValueKind.Array &&
               typeSchema.EnumerateArray().Any(type =>
                   type.ValueKind == JsonValueKind.String &&
                   MatchesSchemaType(type.GetString(), value));
    }

    private static bool MatchesSchemaType(string? type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number &&
                     value.TryGetDecimal(out var number) &&
                     decimal.Truncate(number) == number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static AgentInstallationConfiguration CreateConfiguration(
        Guid installationId,
        string schemaVersion,
        IReadOnlyDictionary<string, JsonElement> settings,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installationId,
            SchemaVersion = NormalizeConfigurationSchemaVersion(schemaVersion, "1"),
            SettingsJson = JsonSerializer.Serialize(settings, SerializerOptions),
            CreatedAt = now,
            UpdatedAt = now
        };

    private static string NormalizeConfigurationSchemaVersion(string? requested, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        if (value.Length > 64)
            throw new AgentInstallationException(
                "Agent configuration schema version cannot exceed 64 characters.");
        return value;
    }

    private static IReadOnlyDictionary<string, JsonElement> DeserializeConfigurationSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyDictionary<string, JsonElement>>(
                       settingsJson,
                       SerializerOptions)
                   ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new AgentInstallationException(
                $"The persisted agent configuration is invalid: {exception.Message}");
        }
    }

    private static bool HasConfigurationValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            _ => true
        };

    private static bool IsProviderConfigurationType(string type) =>
        type.Equals("provider", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("llmProvider", StringComparison.OrdinalIgnoreCase);

    private static void ValidateBusinessId(string businessId)
    {
        if (string.IsNullOrWhiteSpace(businessId) || businessId.Length > 200)
        {
            throw new AgentInstallationException("Business ID is required and cannot exceed 200 characters.");
        }
    }

    private int NormalizeMaxRuntimeSeconds(
        int requestedMaxRuntimeSeconds,
        AgentRuntimeGlobalSettings settings,
        string agentId,
        string businessId)
    {
        if (requestedMaxRuntimeSeconds <= 0)
        {
            throw new AgentInstallationException(
                $"Max runtime must be greater than zero and no more than {settings.DefaultMaxRuntimeSeconds} seconds.");
        }

        if (requestedMaxRuntimeSeconds <= settings.DefaultMaxRuntimeSeconds)
            return requestedMaxRuntimeSeconds;

        _logger.LogWarning(
            "Agent {AgentId} requested a maximum runtime of {RequestedMaxRuntimeSeconds} seconds for business {BusinessId}, " +
            "which exceeds the system maximum of {SystemMaxRuntimeSeconds} seconds. Clamping the approved runtime to the system maximum.",
            agentId,
            requestedMaxRuntimeSeconds,
            businessId,
            settings.DefaultMaxRuntimeSeconds);
        return settings.DefaultMaxRuntimeSeconds;
    }

    private static void ValidateSchedule(
        int tickFrequencySeconds,
        int maxRuntimeSeconds,
        ActivationMode activationMode,
        AgentRuntimeGlobalSettings settings)
    {
        if (tickFrequencySeconds < settings.MinimumTickFrequencySeconds)
        {
            throw new AgentInstallationException(
                $"Tick frequency must be at least {settings.MinimumTickFrequencySeconds} seconds.");
        }

        if (maxRuntimeSeconds <= 0)
        {
            throw new AgentInstallationException(
                $"Max runtime must be greater than zero and no more than {settings.DefaultMaxRuntimeSeconds} seconds.");
        }

        if (activationMode == ActivationMode.AlwaysOn && !settings.AllowAlwaysOnCommunityAgents)
        {
            throw new AgentInstallationException("Always-on community agents are disabled by global policy.");
        }
    }

    private static void ValidateResources(
        int memoryMb,
        int cpuPercent,
        AgentRuntimeGlobalSettings settings,
        AgentPackageVersion packageVersion)
    {
        if (string.Equals(packageVersion.PublisherId, "com.csweet", StringComparison.Ordinal) &&
            memoryMb < FirstPartyMinimumRuntimeMemoryMb)
        {
            throw new AgentInstallationException(
                $"C-Sweet agents require at least {FirstPartyMinimumRuntimeMemoryMb} MB of runtime memory.");
        }

        if (memoryMb <= 0 || memoryMb > settings.MaximumWorkloadMemoryMb)
        {
            throw new AgentInstallationException(
                $"Memory must be between 1 and {settings.MaximumWorkloadMemoryMb} MB.");
        }

        if (cpuPercent <= 0 || cpuPercent > settings.MaximumWorkloadCpuPercent)
        {
            throw new AgentInstallationException(
                $"CPU must be between 1 and {settings.MaximumWorkloadCpuPercent} percent.");
        }
    }

    private static void ValidateGrant(
        string grantName,
        IReadOnlyList<string>? granted,
        IReadOnlyList<string> requested)
    {
        if (granted is null || granted.Any(string.IsNullOrWhiteSpace))
        {
            throw new AgentInstallationException($"Granted {grantName} must contain only non-empty values.");
        }

        var requestedSet = requested.ToHashSet(StringComparer.Ordinal);
        var broaderValue = granted.FirstOrDefault(value => !requestedSet.Contains(value));
        if (broaderValue is not null)
        {
            throw new AgentInstallationException(
                $"Granted {grantName} cannot include '{broaderValue}' because the manifest did not request it.");
        }
    }

    private static ActivationMode ParseActivationMode(string value) =>
        Enum.TryParse<ActivationMode>(value, ignoreCase: false, out var activationMode) &&
        Enum.IsDefined(activationMode)
            ? activationMode
            : throw new AgentInstallationException("Activation mode must be AlwaysOn, Periodic, or Manual.");

    private static PluginInstallationScope ParsePluginScope(string value) =>
        Enum.TryParse<PluginInstallationScope>(value, ignoreCase: true, out var scope) && Enum.IsDefined(scope)
            ? scope
            : throw new AgentInstallationException("Plugin scope must be Organization or System.");

    private static OverlapPolicy ParseOverlapPolicy(string value) =>
        Enum.TryParse<OverlapPolicy>(value, ignoreCase: false, out var overlapPolicy) &&
        Enum.IsDefined(overlapPolicy)
            ? overlapPolicy
            : throw new AgentInstallationException("Overlap policy must be Skip, Queue, or CancelPrevious.");

    private static DateTimeOffset? ComputeNextTick(
        ActivationMode activationMode,
        int tickFrequencySeconds,
        DateTimeOffset now) => activationMode switch
        {
            ActivationMode.AlwaysOn => now,
            ActivationMode.Periodic => now.AddSeconds(tickFrequencySeconds),
            _ => null
        };

    private static string SerializeGrant(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values.Distinct(StringComparer.Ordinal).ToList(), SerializerOptions);

    private static string RetainRequestedGrants(string grantedJson, IReadOnlyList<string> requested)
    {
        var requestedSet = requested.ToHashSet(StringComparer.Ordinal);
        return SerializeGrant(DeserializeGrant(grantedJson).Where(requestedSet.Contains).ToList());
    }

    private async Task RevokeInstallationSessionsAsync(
        Guid installationId,
        string reason,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        var sessions = await _dbContext.McpAgentSessions
            .Where(x => x.AgentInstallationId == installationId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAt = revokedAt;
            session.RevocationReason = reason;
        }
    }

    private static IReadOnlyList<string> DeserializeGrant(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<string>>(json, SerializerOptions) ?? [];

    private static AgentInstallationResponse ToResponse(AgentInstallation installation)
    {
        var package = installation.PackageVersion!;
        var grant = installation.Grant!;
        var schedule = installation.Schedule!;
        var build = package.BuildJobs.OrderByDescending(x => x.Attempt).FirstOrDefault();
        var runtime = installation.RuntimeInstances.OrderByDescending(x => x.QueuedAt).FirstOrDefault();
        return new AgentInstallationResponse(
            installation.Id,
            installation.PackageVersionId,
            installation.BusinessId,
            package.AgentId,
            package.AgentName,
            package.Version,
            package.PublisherName,
            package.CommitSha,
            installation.IsEnabled,
            DeserializeGrant(grant.ProvidedCapabilitiesJson),
            DeserializeGrant(grant.EventSubscriptionsJson),
            [],
            [],
            DeserializeGrant(grant.NetworkAccessJson),
            grant.MemoryMb,
            grant.CpuPercent,
            new AgentScheduleResponse(
                schedule.Id,
                schedule.ActivationMode.ToString(),
                schedule.TickFrequencySeconds,
                schedule.NextTickAt,
                schedule.LastTickAt,
                schedule.LastCompletedAt,
                schedule.RunRequestedAt,
                schedule.MaxRuntimeSeconds,
                schedule.MaxRetriesPerTick,
                schedule.ConsecutiveStartupFailures,
                schedule.AutomaticStartSuppressedAt,
                schedule.OverlapPolicy.ToString(),
                schedule.IsEnabled),
            installation.CreatedAt,
            installation.UpdatedAt,
            build is null ? null : new AgentBuildSummaryResponse(
                build.Id, build.Status.ToString(), build.Attempt, build.QueuedAt, build.StartedAt,
                build.CompletedAt, !string.IsNullOrWhiteSpace(build.LogPath), build.FailureMessage,
                AgentBuildStepStore.Read(build)),
            runtime is null ? null : ToRunResponse(runtime))
        {
            PluginKind = package.PluginKind.ToString(),
            InstallationScope = installation.Scope.ToString(),
            InstallationKey = installation.InstallationKey == Guid.Empty ? installation.Id : installation.InstallationKey,
            RevisionNumber = installation.RevisionNumber,
            RevisionStatus = installation.RevisionStatus.ToString(),
            SetupState = installation.SetupState.ToString(),
            SetupFlowId = installation.SetupFlowId,
            SetupStepId = installation.SetupStepId
        };
    }

    private static void ResetAutomaticStartupFailures(AgentSchedule schedule)
    {
        schedule.ConsecutiveStartupFailures = 0;
        schedule.AutomaticStartSuppressedAt = null;
    }

    private static AgentRuntimeRunResponse ToRunResponse(AgentRuntimeInstance runtime) => new(
        runtime.Id,
        runtime.TickId,
        runtime.Status.ToString(),
        runtime.Reason,
        runtime.QueuedAt,
        runtime.StartedAt,
        runtime.McpSessionEstablishedAt,
        runtime.CompletionReportedAt,
        runtime.CompletedAt,
        runtime.Events.OrderBy(x => x.OccurredAt)
            .Select(x => new AgentRuntimeEventResponse(x.Status.ToString(), x.Reason, x.OccurredAt))
            .ToList(),
        runtime.LogExcerpt);
}
