using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.AI.Providers;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// Owns all agent configuration in the control plane. This service never starts a workload or invokes agent code.
/// </summary>
public sealed class AgentInstallationConfigurationService(
    CSweetDbContext db,
    IAuditEventWriter auditWriter,
    AgentWorkInbox? inbox = null,
    IModelCatalogClient? modelCatalog = null) : IAgentInstallationConfigurationService, IAgentConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentConfigurationView> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await DefinitionQuery().SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken)
            ?? throw new AgentInstallationException("The agent definition was not found.");
        return ToView(definition, null, new Dictionary<string, JsonElement>(), null);
    }

    public async Task<AgentConfigurationView> SaveDefinitionAsync(
        Guid definitionId,
        PutAgentDefinitionConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var definition = await db.AgentDefinitions.Include(x => x.Configuration).Include(x => x.PackageVersion)
            .Include(x => x.Installations).ThenInclude(x => x.Configuration)
            .Include(x => x.Installations).ThenInclude(x => x.RuntimeInstances)
            .SingleOrDefaultAsync(x => x.Id == definitionId, cancellationToken)
            ?? throw new AgentInstallationException("The agent definition was not found.");
        var configuration = definition.Configuration
            ?? throw new AgentInstallationException("The agent definition does not have a configuration record.");
        if (configuration.Revision != request.ExpectedRevision)
            throw new AgentConfigurationConflictException(configuration.Revision);

        var manifest = AgentConfigurationRules.DeserializeManifest(definition.PackageVersion!.ManifestJson);
        await AgentConfigurationRules.ValidateAsync(db, manifest, request.Settings, requireRequired: true,
            cancellationToken, modelCatalog, validateSupportedModels: true);
        var oldDefaults = Deserialize(configuration.SettingsJson);
        if (SettingsEqual(oldDefaults, request.Settings))
        {
            var canActivate = definition.PackageVersion!.Status == AgentPackageVersionStatus.Built &&
                              !string.IsNullOrWhiteSpace(definition.PackageVersion.PackageDigest) &&
                              !string.IsNullOrWhiteSpace(definition.PackageVersion.ArtifactSignature);
            if (canActivate && (!definition.IsAvailableForHire || definition.Status != AgentDefinitionStatus.Available))
            {
                definition.Status = AgentDefinitionStatus.Available;
                definition.IsAvailableForHire = true;
                definition.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await auditWriter.WriteAsync("agent-definition.configuration.reviewed", nameof(AgentDefinition), definition.Id,
                    $"Validated existing global defaults at revision {configuration.Revision} and made the definition available for hire.",
                    cancellationToken: cancellationToken);
            }
            return ToView(definition, null, new Dictionary<string, JsonElement>(), null);
        }
        var oldEffective = definition.Installations.ToDictionary(
            x => x.Id,
            x => Merge(oldDefaults, Deserialize(x.Configuration?.SettingsJson)));

        configuration.SchemaVersion = NormalizeSchemaVersion(request.SchemaVersion);
        configuration.SettingsJson = Serialize(request.Settings);
        configuration.Revision++;
        configuration.UpdatedAt = DateTimeOffset.UtcNow;
        var builtAndSigned = definition.PackageVersion.Status == AgentPackageVersionStatus.Built &&
                             !string.IsNullOrWhiteSpace(definition.PackageVersion.PackageDigest) &&
                             !string.IsNullOrWhiteSpace(definition.PackageVersion.ArtifactSignature);
        definition.Status = builtAndSigned
            ? AgentDefinitionStatus.Available : AgentDefinitionStatus.Building;
        definition.IsAvailableForHire = builtAndSigned;
        definition.UpdatedAt = configuration.UpdatedAt;

        var refreshes = new List<(Guid InstallationId, IReadOnlyList<string> ChangedKeys)>();
        foreach (var installation in definition.Installations)
        {
            var next = Merge(request.Settings, Deserialize(installation.Configuration?.SettingsJson));
            if (SettingsEqual(oldEffective[installation.Id], next))
                continue;
            var changedKeys = ChangedKeys(oldEffective[installation.Id], next);
            MarkConfigurationChanged(installation);
            if (HasActiveRuntime(installation)) refreshes.Add((installation.Id, changedKeys));
        }

        await SaveWithRevisionConflictAsync(configuration, cancellationToken);
        await auditWriter.WriteAsync("agent-definition.configuration.updated", nameof(AgentDefinition), definition.Id,
            $"Updated global defaults to revision {configuration.Revision}.", cancellationToken: cancellationToken);
        foreach (var refresh in refreshes)
            await QueueRefreshAsync(refresh.InstallationId, refresh.ChangedKeys, cancellationToken);
        return ToView(definition, null, new Dictionary<string, JsonElement>(), null);
    }

    public async Task<AgentConfigurationView> GetEmployeeAsync(
        Guid organizationId, Guid employeeId, CancellationToken cancellationToken = default)
    {
        var employee = await EmployeeQuery(organizationId, employeeId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgentInstallationException("The agent employee was not found.");
        return ToEmployeeView(employee);
    }

    public async Task<AgentConfigurationView> SaveEmployeeOverridesAsync(
        Guid organizationId,
        Guid employeeId,
        PutAgentConfigurationOverridesRequest request,
        CancellationToken cancellationToken = default)
    {
        var employee = await EmployeeQuery(organizationId, employeeId, tracking: true).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgentInstallationException("The agent employee was not found.");
        var installation = employee.AgentInstallation!;
        var definition = installation.AgentDefinition!;
        var configuration = installation.Configuration;
        var currentRevision = configuration?.Revision ?? 0;
        if (currentRevision != request.ExpectedRevision)
            throw new AgentConfigurationConflictException(currentRevision);

        var manifest = AgentConfigurationRules.DeserializeManifest(definition.PackageVersion!.ManifestJson);
        var defaults = Deserialize(definition.Configuration!.SettingsJson);
        var previousOverrides = Deserialize(configuration?.SettingsJson);
        var previousEffective = Merge(defaults, previousOverrides);
        var effective = Merge(defaults, request.Overrides);
        await AgentConfigurationRules.ValidateAsync(db, manifest, effective, requireRequired: true,
            cancellationToken, modelCatalog, validateSupportedModels: true);

        var sparse = request.Overrides
            .Where(pair => !defaults.TryGetValue(pair.Key, out var defaultValue) || !JsonEqual(pair.Value, defaultValue))
            .ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
        if (SettingsEqual(previousOverrides, sparse))
            return ToEmployeeView(employee);
        var effectiveChanged = !SettingsEqual(previousEffective, effective);
        var now = DateTimeOffset.UtcNow;
        if (configuration is null)
        {
            configuration = new AgentInstallationConfiguration
            {
                Id = Guid.NewGuid(), AgentInstallationId = installation.Id, CreatedAt = now, Revision = 1
            };
            installation.Configuration = configuration;
        }
        else
        {
            configuration.Revision++;
        }
        configuration.SchemaVersion = definition.Configuration.SchemaVersion;
        configuration.SettingsJson = Serialize(sparse);
        configuration.UpdatedAt = now;
        if (effectiveChanged)
            MarkConfigurationChanged(installation);
        await SaveWithRevisionConflictAsync(configuration, cancellationToken);
        await auditWriter.WriteAsync("agent-installation.configuration.overrides-updated", nameof(AgentInstallation), installation.Id,
            $"Updated employee overrides to revision {configuration.Revision}.", cancellationToken: cancellationToken);
        if (effectiveChanged && HasActiveRuntime(installation))
            await QueueRefreshAsync(installation.Id, ChangedKeys(previousEffective, effective), cancellationToken);
        return ToEmployeeView(employee);
    }

    public async Task<AgentConfigurationView> RestoreEmployeeOverrideAsync(
        Guid organizationId, Guid employeeId, string key, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var employee = await EmployeeQuery(organizationId, employeeId, tracking: true).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgentInstallationException("The agent employee was not found.");
        var overrides = Deserialize(employee.AgentInstallation!.Configuration?.SettingsJson);
        var currentRevision = employee.AgentInstallation.Configuration?.Revision ?? 0;
        if (currentRevision != expectedRevision)
            throw new AgentConfigurationConflictException(currentRevision);
        var defaults = Deserialize(employee.AgentInstallation.AgentDefinition!.Configuration?.SettingsJson);
        var previousEffective = Merge(defaults, overrides);
        if (overrides.Remove(key))
        {
            employee.AgentInstallation.Configuration!.SettingsJson = Serialize(overrides);
            employee.AgentInstallation.Configuration.Revision++;
            employee.AgentInstallation.Configuration.UpdatedAt = DateTimeOffset.UtcNow;
            MarkConfigurationChanged(employee.AgentInstallation);
            await SaveWithRevisionConflictAsync(employee.AgentInstallation.Configuration, cancellationToken);
            await auditWriter.WriteAsync("agent-installation.configuration.override-restored", nameof(AgentInstallation),
                employee.AgentInstallation.Id, $"Restored '{key}' to its global default.", cancellationToken: cancellationToken);
            if (HasActiveRuntime(employee.AgentInstallation))
                await QueueRefreshAsync(employee.AgentInstallation.Id,
                    ChangedKeys(previousEffective, Merge(defaults, overrides)), cancellationToken);
        }
        return ToEmployeeView(employee);
    }

    public async Task<AgentConfigurationView> RestoreAllEmployeeOverridesAsync(
        Guid organizationId, Guid employeeId, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var employee = await EmployeeQuery(organizationId, employeeId, tracking: true).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AgentInstallationException("The agent employee was not found.");
        var configuration = employee.AgentInstallation!.Configuration;
        var currentRevision = configuration?.Revision ?? 0;
        if (currentRevision != expectedRevision)
            throw new AgentConfigurationConflictException(currentRevision);
        var defaults = Deserialize(employee.AgentInstallation.AgentDefinition!.Configuration?.SettingsJson);
        var previousEffective = Merge(defaults, Deserialize(configuration?.SettingsJson));
        if (configuration is not null && Deserialize(configuration.SettingsJson).Count > 0)
        {
            configuration.SettingsJson = "{}";
            configuration.Revision++;
            configuration.UpdatedAt = DateTimeOffset.UtcNow;
            MarkConfigurationChanged(employee.AgentInstallation);
            await SaveWithRevisionConflictAsync(configuration, cancellationToken);
            await auditWriter.WriteAsync("agent-installation.configuration.overrides-restored", nameof(AgentInstallation),
                employee.AgentInstallation.Id, "Restored all employee settings to global defaults.", cancellationToken: cancellationToken);
            if (HasActiveRuntime(employee.AgentInstallation))
                await QueueRefreshAsync(employee.AgentInstallation.Id,
                    ChangedKeys(previousEffective, defaults), cancellationToken);
        }
        return ToEmployeeView(employee);
    }

    public async Task<EffectiveAgentConfiguration> ResolveInstallationAsync(
        Guid installationId, CancellationToken cancellationToken = default)
    {
        var installation = await db.AgentInstallations.AsNoTracking()
            .Include(x => x.Configuration)
            .Include(x => x.AgentDefinition)!.ThenInclude(x => x!.Configuration)
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken)
            ?? throw new AgentInstallationException("The agent installation was not found.");
        var settings = installation.AgentDefinition is null
            ? Deserialize(installation.Configuration?.SettingsJson)
            : Merge(Deserialize(installation.AgentDefinition.Configuration?.SettingsJson),
                Deserialize(installation.Configuration?.SettingsJson));
        var schemaVersion = installation.AgentDefinition?.Configuration?.SchemaVersion
                            ?? installation.Configuration?.SchemaVersion ?? "1";
        return new EffectiveAgentConfiguration(installation.Id, schemaVersion, settings,
            installation.DesiredConfigurationRevision, AgentConfigurationRules.Digest(settings));
    }

    // Legacy installation facade. It remains control-plane-only and converts full snapshots into sparse overrides.
    public async Task<AgentInstallationConfigurationSnapshot?> GetAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Configuration)
            .Include(x => x.AgentDefinition)!.ThenInclude(x => x!.Configuration)
            .SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        if (installation is null) return null;
        var effective = await ResolveInstallationAsync(installationId, cancellationToken);
        return new AgentInstallationConfigurationSnapshot(installationId, effective.SchemaVersion, effective.Settings,
            installation.Configuration?.CreatedAt ?? installation.CreatedAt,
            installation.Configuration?.UpdatedAt ?? installation.UpdatedAt);
    }

    public async Task<AgentInstallationConfigurationSnapshot> SaveAsync(Guid installationId, string schemaVersion,
        IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken = default)
    {
        var employee = await db.CoreOrganizationUsers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AgentInstallationId == installationId && x.IsActive, cancellationToken)
            ?? throw new AgentInstallationException("Configuration overrides require an active hired employee.");
        var installationRevision = await db.AgentInstallationConfigurations.AsNoTracking()
            .Where(x => x.AgentInstallationId == installationId).Select(x => (long?)x.Revision)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        var view = await SaveEmployeeOverridesAsync(employee.OrganizationId, employee.Id,
            new PutAgentConfigurationOverridesRequest(settings, installationRevision), cancellationToken);
        var timestamps = await db.AgentInstallationConfigurations.AsNoTracking()
            .Where(x => x.AgentInstallationId == installationId)
            .Select(x => new { x.CreatedAt, x.UpdatedAt })
            .SingleAsync(cancellationToken);
        return new AgentInstallationConfigurationSnapshot(installationId, view.SchemaVersion, view.EffectiveValues,
            timestamps.CreatedAt, timestamps.UpdatedAt);
    }

    private IQueryable<AgentDefinition> DefinitionQuery() => db.AgentDefinitions.AsNoTracking()
        .Include(x => x.Configuration).Include(x => x.PackageVersion);

    private IQueryable<Domain.Core.OrganizationUser> EmployeeQuery(Guid organizationId, Guid employeeId, bool tracking = false)
    {
        var query = tracking ? db.CoreOrganizationUsers.AsQueryable() : db.CoreOrganizationUsers.AsNoTracking();
        return query.Where(x => x.Id == employeeId && x.OrganizationId == organizationId && x.IsActive && x.AgentInstallationId != null)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Configuration)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.RuntimeInstances)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.AgentDefinition)!.ThenInclude(x => x!.Configuration)
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.AgentDefinition)!.ThenInclude(x => x!.PackageVersion);
    }

    private static AgentConfigurationView ToEmployeeView(Domain.Core.OrganizationUser employee)
    {
        var installation = employee.AgentInstallation!;
        var definition = installation.AgentDefinition!;
        return ToView(definition, installation, Deserialize(installation.Configuration?.SettingsJson),
            installation.Configuration?.Revision ?? 0);
    }

    private static AgentConfigurationView ToView(AgentDefinition definition, AgentInstallation? installation,
        IReadOnlyDictionary<string, JsonElement> overrides, long? expectedRevision)
    {
        var manifest = AgentConfigurationRules.DeserializeManifest(definition.PackageVersion!.ManifestJson);
        var defaults = Deserialize(definition.Configuration?.SettingsJson);
        var effective = Merge(defaults, overrides);
        return new AgentConfigurationView(
            definition.AgentId, definition.PackageVersion.Version, definition.Configuration?.SchemaVersion ?? "1",
            AgentConfigurationRules.ToFields(manifest), defaults, overrides, effective,
            overrides.Keys.Order(StringComparer.Ordinal).ToArray(), expectedRevision ?? definition.Configuration?.Revision ?? 0,
            installation?.DesiredConfigurationRevision ?? definition.Configuration?.Revision ?? 0,
            installation?.AppliedConfigurationRevision ?? 0,
            installation?.ConfigurationSyncStatus.ToString() ?? "Current",
            installation?.ConfigurationSyncLastError);
    }

    private static void MarkConfigurationChanged(AgentInstallation installation)
    {
        installation.DesiredConfigurationRevision++;
        var active = installation.RuntimeInstances.Any(x => x.Status is AgentRuntimeStatus.Queued or AgentRuntimeStatus.Starting or
            AgentRuntimeStatus.WaitingForMcpSession or AgentRuntimeStatus.Running or AgentRuntimeStatus.CompletionReported);
        installation.ConfigurationSyncStatus = active
            ? AgentConfigurationSyncStatus.Refreshing : AgentConfigurationSyncStatus.PendingNextStart;
        installation.ConfigurationSyncLastAttemptAt = active ? DateTimeOffset.UtcNow : null;
        installation.ConfigurationSyncLastError = null;
        installation.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task QueueRefreshAsync(
        Guid installationId,
        IReadOnlyList<string> changedKeys,
        CancellationToken cancellationToken)
    {
        if (changedKeys.Count == 0 || inbox is null) return;
        var effective = await ResolveInstallationAsync(installationId, cancellationToken);
        var organizationId = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == installationId)
            .Select(x => x.BusinessId).SingleAsync(cancellationToken);
        try
        {
            await inbox.EnqueueAsync(
                organizationId,
                installationId,
                AgentWorkKind.ConfigurationUpdate,
                "configuration.update",
                JsonSerializer.SerializeToElement(new
                {
                    effective.InstallationId,
                    effective.SchemaVersion,
                    EffectiveSettings = effective.Settings,
                    ChangedKeys = changedKeys,
                    DesiredRevision = effective.Revision,
                    EffectiveDigest = effective.Digest
                }, JsonOptions),
                $"configuration:{installationId:N}:{effective.Revision}",
                DateTimeOffset.UtcNow.AddMinutes(5),
                sourceType: "configuration-control-plane",
                sourceId: effective.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var installation = await db.AgentInstallations.SingleAsync(x => x.Id == installationId, cancellationToken);
            installation.ConfigurationSyncStatus = AgentConfigurationSyncStatus.Failed;
            installation.ConfigurationSyncLastError = exception.Message.Length <= 2048
                ? exception.Message : exception.Message[..2048];
            await db.SaveChangesAsync(cancellationToken);
            AgentRuntimeMetrics.ConfigurationRefreshFailed();
        }
    }

    private async Task SaveWithRevisionConflictAsync(object configuration, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var entry = exception.Entries.FirstOrDefault(x => ReferenceEquals(x.Entity, configuration));
            var databaseValues = entry is null ? null : await entry.GetDatabaseValuesAsync(cancellationToken);
            var currentRevision = databaseValues is null
                ? 0
                : Convert.ToInt64(databaseValues[nameof(AgentInstallationConfiguration.Revision)],
                    System.Globalization.CultureInfo.InvariantCulture);
            db.ChangeTracker.Clear();
            throw new AgentConfigurationConflictException(currentRevision);
        }
    }

    private static IReadOnlyList<string> ChangedKeys(
        IReadOnlyDictionary<string, JsonElement> previous,
        IReadOnlyDictionary<string, JsonElement> current) =>
        previous.Keys.Concat(current.Keys).Distinct(StringComparer.Ordinal)
            .Where(key => !previous.TryGetValue(key, out var before) ||
                !current.TryGetValue(key, out var after) || !JsonEqual(before, after))
            .Order(StringComparer.Ordinal).ToArray();

    private static bool HasActiveRuntime(AgentInstallation installation) =>
        installation.RuntimeInstances.Any(x => x.Status is AgentRuntimeStatus.Queued or AgentRuntimeStatus.Starting or
            AgentRuntimeStatus.WaitingForMcpSession or AgentRuntimeStatus.Running or
            AgentRuntimeStatus.CompletionReported or AgentRuntimeStatus.Stopping);

    private static Dictionary<string, JsonElement> Merge(IReadOnlyDictionary<string, JsonElement> defaults,
        IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        var result = defaults.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
        if (overrides is not null)
            foreach (var pair in overrides) result[pair.Key] = pair.Value.Clone();
        return result;
    }

    private static Dictionary<string, JsonElement> Deserialize(string? json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json ?? "{}", JsonOptions)
        ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    private static string Serialize(IReadOnlyDictionary<string, JsonElement> values) => JsonSerializer.Serialize(values, JsonOptions);
    private static bool SettingsEqual(IReadOnlyDictionary<string, JsonElement> left, IReadOnlyDictionary<string, JsonElement> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && JsonEqual(pair.Value, value));
    private static bool JsonEqual(JsonElement left, JsonElement right) =>
        string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
    private static string NormalizeSchemaVersion(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 64
            ? throw new AgentInstallationException("Configuration schema version is required and cannot exceed 64 characters.")
            : value.Trim();
}

public sealed class AgentConfigurationConflictException(long currentRevision)
    : Exception($"The configuration changed after it was loaded. Current revision: {currentRevision}.")
{
    public long CurrentRevision { get; } = currentRevision;
}
