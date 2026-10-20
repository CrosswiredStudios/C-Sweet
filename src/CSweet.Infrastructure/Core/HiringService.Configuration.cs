using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class HiringService
{
    private async Task<WorkflowSnapshot> ConfigureDelegatedHireAsync(WorkflowSnapshot snapshot,
        Guid chiefInstallationId, bool broadDelegation, CancellationToken token)
    {
        if (snapshot.EmbeddedAgent is not { ConfigurationSettings: null } embedded) return snapshot;
        // Resolve once into the immutable hire snapshot. Later commits validate these exact
        // settings instead of silently inheriting defaults that changed during a build.
        var manifest = new PluginManifest { Configuration = embedded.ConfigurationFields };
        var fields = AgentTokenBudget.Fields(manifest).Where(x => !x.Secret).ToDictionary(x => x.Key);
        var settings = AgentConfigurationRules.GetManifestDefaults(manifest);
        if (embedded.DefinitionId is { } definitionId)
        {
            var defaults = await db.AgentDefinitionConfigurations.AsNoTracking().Where(x => x.AgentDefinitionId == definitionId)
                .Select(x => x.SettingsJson).SingleOrDefaultAsync(token);
            foreach (var pair in ReadConfiguration(defaults).Where(x => fields.ContainsKey(x.Key)))
                settings[pair.Key] = pair.Value.Clone();
        }
        if (broadDelegation)
        {
            var chief = await db.AgentInstallations.AsNoTracking().Include(x => x.Configuration)
                .Include(x => x.AgentDefinition)!.ThenInclude(x => x!.Configuration)
                .SingleOrDefaultAsync(x => x.Id == chiefInstallationId, token);
            var chiefSettings = ReadConfiguration(chief?.AgentDefinition?.Configuration?.SettingsJson);
            foreach (var pair in ReadConfiguration(chief?.Configuration?.SettingsJson)) chiefSettings[pair.Key] = pair.Value;
            var provider = fields.Values.FirstOrDefault(x => AgentConfigurationRules.NormalizeType(x.Type) == AgentConfigurationFieldTypes.LlmProvider);
            var model = fields.Values.FirstOrDefault(x => AgentConfigurationRules.NormalizeType(x.Type) == AgentConfigurationFieldTypes.LlmModel);
            // Reuse only the Chief's selected model pair, never unrelated settings or secrets.
            if (provider is not null && model is not null && !settings.ContainsKey(provider.Key) && !settings.ContainsKey(model.Key) &&
                chiefSettings.TryGetValue("llmProviderId", out var providerValue) && chiefSettings.TryGetValue("llmModel", out var modelValue))
            {
                settings[provider.Key] = providerValue.Clone();
                settings[model.Key] = modelValue.Clone();
            }
        }
        var configured = embedded with { ConfigurationSettings = settings };
        await ValidateDelegatedConfigurationAsync(configured, token);
        return snapshot with { EmbeddedAgent = configured };
    }

    private async Task ValidateDelegatedConfigurationAsync(EmbeddedAgentInstallSnapshot embedded, CancellationToken token)
    {
        var manifest = new PluginManifest { Configuration = embedded.ConfigurationFields };
        var settings = AgentConfigurationRules.GetManifestDefaults(manifest);
        foreach (var pair in embedded.ConfigurationSettings ?? new Dictionary<string, JsonElement>()) settings[pair.Key] = pair.Value;
        var missing = AgentTokenBudget.Fields(manifest).Where(x => !x.Secret && x.Required && AgentConfigurationRules.IsVisible(x, settings) &&
            (!settings.TryGetValue(x.Key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))).Select(x => x.Label).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Required employee configuration needs owner input: {string.Join(", ", missing)}.");
        try { await AgentConfigurationRules.ValidateAsync(db, manifest, settings, true, token); }
        catch (AgentInstallationException error) { throw new InvalidOperationException(error.Message, error); }
    }

    private static Dictionary<string, JsonElement> ReadConfiguration(string? json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json ?? "{}", JsonOptions) ?? [];
}
