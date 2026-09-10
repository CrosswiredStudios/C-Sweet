using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentInstallationConfigurationService
{
    public async Task<IReadOnlyList<AgentProviderMigrationCandidate>> ListMigrationCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = await DefinitionQuery().Include(x => x.Installations).ToListAsync(cancellationToken);
        return definitions.Where(x => x.Configuration is not null && MigrationFields(x).Count > 0)
            .OrderBy(x => x.AgentId).Select(x => new AgentProviderMigrationCandidate(
                x.Id, x.AgentId, string.IsNullOrWhiteSpace(x.PackageVersion!.AgentName) ? x.AgentId : x.PackageVersion.AgentName, x.Configuration!.Revision, x.Installations.Count)).ToArray();
    }

    public async Task<MigrateAgentProvidersResponse> MigrateProvidersAsync(MigrateAgentProvidersRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Agents is not { Count: > 0 } || string.IsNullOrWhiteSpace(request.Model))
            throw new AgentInstallationException("Select at least one agent, a provider, and a model.");
        var ids = request.Agents.Select(x => x.DefinitionId).Distinct().ToArray();
        if (ids.Length != request.Agents.Count)
            throw new AgentInstallationException("Each agent can only be selected once.");
        var provider = await db.LlmProviderProfiles.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.ProviderId && x.IsEnabled, cancellationToken)
            ?? throw new AgentInstallationException("Select an enabled LLM provider.");
        var model = request.Model.Trim();
        if (!string.Equals(model, provider.DefaultChatModel, StringComparison.Ordinal))
        {
            if (modelCatalog is null)
                throw new AgentInstallationException("The provider model catalog is unavailable.");
            IReadOnlyList<ModelDescriptor> models;
            try { models = await modelCatalog.ListModelsAsync(provider.Id, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AgentInstallationException($"Could not load the provider model catalog: {ex.Message}");
            }
            if (!models.Any(x => x.Id == model))
                throw new AgentInstallationException("Select a model supported by the provider.");
        }

        var definitions = await db.AgentDefinitions.Include(x => x.Configuration).Include(x => x.PackageVersion)
            .Include(x => x.Installations).ThenInclude(x => x.Configuration)
            .Include(x => x.Installations).ThenInclude(x => x.RuntimeInstances)
            .Include(x => x.Installations).ThenInclude(x => x.Schedule)
            .Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        if (definitions.Count != ids.Length)
            throw new AgentInstallationException("One or more selected agents no longer exist. Reload the list.");
        // Validate every selection before mutating entities; persist the entire batch in one save.
        foreach (var definition in definitions)
        {
            if (definition.Configuration is null || MigrationFields(definition).Count == 0)
                throw new AgentInstallationException($"{definition.AgentId} does not have configurable providers and models.");
            if (definition.Configuration.Revision != request.Agents.Single(x => x.DefinitionId == definition.Id).ExpectedRevision)
                throw new AgentConfigurationConflictException(definition.Configuration.Revision);
        }

        var refreshes = new List<(Guid Id, IReadOnlyList<string> Keys)>();
        var instanceCount = 0;
        foreach (var definition in definitions)
        {
            var configuration = definition.Configuration!;
            var fields = MigrationFields(definition);
            var previousDefaults = Deserialize(configuration.SettingsJson);
            var nextDefaults = Merge(previousDefaults, null);
            foreach (var field in fields)
                nextDefaults[field.Key] = JsonSerializer.SerializeToElement(
                    field.Type == AgentConfigurationFieldTypes.LlmProvider ? provider.Id.ToString("D") : model);
            foreach (var installation in definition.Installations)
            {
                var overrides = Deserialize(installation.Configuration?.SettingsJson);
                var previous = Merge(previousDefaults, overrides);
                foreach (var field in fields)
                {
                    if (request.IncludeInstances) overrides.Remove(field.Key);
                    else overrides[field.Key] = previous.TryGetValue(field.Key, out var value)
                        ? value : JsonSerializer.SerializeToElement<string?>(null);
                }
                if (!SettingsEqual(overrides, Deserialize(installation.Configuration?.SettingsJson)))
                {
                    installation.Configuration ??= new AgentInstallationConfiguration
                    {
                        Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
                        SchemaVersion = configuration.SchemaVersion, CreatedAt = DateTimeOffset.UtcNow, Revision = 0
                    };
                    installation.Configuration.SettingsJson = Serialize(overrides);
                    installation.Configuration.Revision++;
                    installation.Configuration.UpdatedAt = DateTimeOffset.UtcNow;
                }
                var next = Merge(nextDefaults, overrides);
                if (!SettingsEqual(previous, next))
                {
                    MarkConfigurationChanged(installation);
                    if (HasActiveRuntime(installation)) refreshes.Add((installation.Id, ChangedKeys(previous, next)));
                }
                if (request.IncludeInstances) instanceCount++;
            }
            if (!SettingsEqual(previousDefaults, nextDefaults))
            {
                configuration.SettingsJson = Serialize(nextDefaults);
                configuration.Revision++;
                configuration.UpdatedAt = DateTimeOffset.UtcNow;
                definition.UpdatedAt = configuration.UpdatedAt;
            }
        }
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new AgentInstallationException("Agent configuration changed during migration. Reload the list and try again.");
        }
        await auditWriter.WriteAsync("agent-definitions.providers-migrated", nameof(LlmProviderProfile), provider.Id,
            $"Migrated {definitions.Count} agent defaults and {instanceCount} instances to model '{model}'.",
            cancellationToken: cancellationToken);
        foreach (var refresh in refreshes)
            await QueueRefreshAsync(refresh.Id, refresh.Keys, cancellationToken);
        return new(definitions.Count, instanceCount);
    }

    private static IReadOnlyList<AgentConfigurationField> MigrationFields(AgentDefinition definition)
    {
        if (definition.PackageVersion is null) return [];
        var fields = AgentConfigurationRules.ToFields(AgentConfigurationRules.DeserializeManifest(definition.PackageVersion.ManifestJson));
        return fields.Any(x => x.Type == AgentConfigurationFieldTypes.LlmProvider) &&
               fields.Any(x => x.Type == AgentConfigurationFieldTypes.LlmModel)
            ? fields.Where(x => x.Type is AgentConfigurationFieldTypes.LlmProvider or AgentConfigurationFieldTypes.LlmModel).ToArray()
            : [];
    }
}
