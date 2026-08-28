using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.AI.Providers;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

internal static class AgentConfigurationRules
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static PluginManifest DeserializeManifest(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(json ?? "{}", SerializerOptions)
                   ?? throw new AgentInstallationException("The stored agent manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new AgentInstallationException($"The stored agent manifest is invalid: {exception.Message}");
        }
    }

    public static Dictionary<string, JsonElement> GetManifestDefaults(PluginManifest manifest)
    {
        var defaults = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in manifest.Configuration.Where(x => !x.Secret && x.DefaultValue.HasValue))
            defaults[field.Key] = field.DefaultValue!.Value.Clone();
        return defaults;
    }

    public static IReadOnlyList<AgentConfigurationField> ToFields(PluginManifest manifest) =>
        manifest.Configuration.Where(x => !x.Secret).Select(x => new AgentConfigurationField(
            x.Key,
            x.Label,
            NormalizeType(x.Type),
            x.Required,
            x.Description,
            null,
            x.Options?.Select(option => new AgentConfigurationOption(option.Value, option.Label)).ToArray(),
            x.Minimum,
            x.Maximum,
            x.Step,
            x.DependsOnFieldKey,
            x.VisibleWhenFieldKey,
            x.VisibleWhenValue)).ToArray();

    public static async Task ValidateAsync(
        CSweetDbContext db,
        PluginManifest manifest,
        IReadOnlyDictionary<string, JsonElement> settings,
        bool requireRequired,
        CancellationToken cancellationToken,
        IModelCatalogClient? modelCatalog = null,
        bool validateSupportedModels = false)
    {
        var fields = manifest.Configuration.Where(x => !x.Secret)
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
        var unknown = settings.Keys.Where(key => !fields.ContainsKey(key)).Order().ToArray();
        if (unknown.Length > 0)
            throw new AgentInstallationException($"Unsupported configuration setting(s): {string.Join(", ", unknown)}.");

        foreach (var field in fields.Values.Where(x => !string.IsNullOrWhiteSpace(x.DependsOnFieldKey)))
        {
            if (!fields.ContainsKey(field.DependsOnFieldKey!))
                throw new AgentInstallationException(
                    $"'{field.Label}' depends on an unknown configuration field '{field.DependsOnFieldKey}'.");
            if (settings.TryGetValue(field.Key, out var dependentValue) && HasValue(dependentValue) &&
                (!settings.TryGetValue(field.DependsOnFieldKey!, out var dependencyValue) || !HasValue(dependencyValue)))
                throw new AgentInstallationException(
                    $"'{field.Label}' requires configuration field '{field.DependsOnFieldKey}'.");
        }

        foreach (var field in fields.Values)
        {
            var present = settings.TryGetValue(field.Key, out var value) && HasValue(value);
            if (requireRequired && field.Required && IsVisible(field, settings) && !present)
                throw new AgentInstallationException($"'{field.Label}' ({field.Key}) is required.");
            if (!present)
                continue;

            var type = NormalizeType(field.Type);
            if (type == AgentConfigurationFieldTypes.Boolean && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new AgentInstallationException($"'{field.Label}' must be true or false.");
            if (type == AgentConfigurationFieldTypes.Number && value.ValueKind != JsonValueKind.Number)
                throw new AgentInstallationException($"'{field.Label}' must be a number.");
            if (type is AgentConfigurationFieldTypes.Text or AgentConfigurationFieldTypes.TextArea or
                AgentConfigurationFieldTypes.Select or AgentConfigurationFieldTypes.LlmProvider or AgentConfigurationFieldTypes.LlmModel &&
                value.ValueKind != JsonValueKind.String)
                throw new AgentInstallationException($"'{field.Label}' must be text.");
            if (type == AgentConfigurationFieldTypes.Select && field.Options is { Count: > 0 } &&
                !field.Options.Any(x => string.Equals(x.Value, value.GetString(), StringComparison.Ordinal)))
                throw new AgentInstallationException($"'{field.Label}' must use an option declared by the signed manifest.");
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                if (field.Minimum.HasValue && number < field.Minimum.Value)
                    throw new AgentInstallationException($"'{field.Label}' must be at least {field.Minimum.Value}.");
                if (field.Maximum.HasValue && number > field.Maximum.Value)
                    throw new AgentInstallationException($"'{field.Label}' must be at most {field.Maximum.Value}.");
            }
            if (type == AgentConfigurationFieldTypes.LlmProvider &&
                (!Guid.TryParse(value.GetString(), out var providerId) ||
                 !await db.LlmProviderProfiles.AsNoTracking().AnyAsync(x => x.Id == providerId && x.IsEnabled, cancellationToken)))
                throw new AgentInstallationException($"'{field.Label}' must reference an enabled LLM provider profile.");
        }

        var providerField = fields.Values.FirstOrDefault(x => NormalizeType(x.Type) == AgentConfigurationFieldTypes.LlmProvider);
        var modelField = fields.Values.FirstOrDefault(x => NormalizeType(x.Type) == AgentConfigurationFieldTypes.LlmModel);
        if (providerField is not null && modelField is not null &&
            settings.TryGetValue(providerField.Key, out var providerValue) && Guid.TryParse(providerValue.GetString(), out var providerIdValue) &&
            settings.TryGetValue(modelField.Key, out var modelValue) && !string.IsNullOrWhiteSpace(modelValue.GetString()))
        {
            var profile = await db.LlmProviderProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == providerIdValue && x.IsEnabled, cancellationToken)
                ?? throw new AgentInstallationException("The selected LLM provider is not enabled.");
            var selectedModel = modelValue.GetString()!;
            if (validateSupportedModels &&
                !string.Equals(profile.DefaultChatModel, selectedModel, StringComparison.Ordinal))
            {
                if (modelCatalog is null)
                    throw new AgentInstallationException("The trusted provider model catalog is unavailable.");
                IReadOnlyList<CSweet.Contracts.Llm.ModelDescriptor> models;
                try
                {
                    models = await modelCatalog.ListModelsAsync(profile.Id, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new AgentInstallationException(
                        $"The trusted provider model catalog could not be loaded: {exception.Message}");
                }
                if (!models.Any(x => string.Equals(x.Id, selectedModel, StringComparison.Ordinal)))
                    throw new AgentInstallationException(
                        $"'{modelField.Label}' must be a model supported by the selected provider.");
            }
        }
    }

    public static string Digest(IReadOnlyDictionary<string, JsonElement> settings)
    {
        var canonical = string.Join("\n", settings.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}={x.Value.GetRawText()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static bool HasAllRequired(PluginManifest manifest, IReadOnlyDictionary<string, JsonElement> settings) =>
        manifest.Configuration.Where(x => !x.Secret && x.Required)
            .All(field => !IsVisible(field, settings) ||
                settings.TryGetValue(field.Key, out var value) && HasValue(value));

    public static bool IsVisible(
        PluginConfigurationField field,
        IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (string.IsNullOrWhiteSpace(field.VisibleWhenFieldKey))
            return true;
        return settings.TryGetValue(field.VisibleWhenFieldKey, out var controller) &&
               controller.ValueKind == JsonValueKind.String &&
               string.Equals(controller.GetString(), field.VisibleWhenValue, StringComparison.Ordinal);
    }

    private static bool HasValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        _ => true
    };

    private static string NormalizeType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "string" or "text" => AgentConfigurationFieldTypes.Text,
        "textarea" => AgentConfigurationFieldTypes.TextArea,
        "number" or "integer" => AgentConfigurationFieldTypes.Number,
        "boolean" or "bool" => AgentConfigurationFieldTypes.Boolean,
        "select" => AgentConfigurationFieldTypes.Select,
        "secret" => AgentConfigurationFieldTypes.Secret,
        "provider" or "llmprovider" => AgentConfigurationFieldTypes.LlmProvider,
        "model" or "llmmodel" => AgentConfigurationFieldTypes.LlmModel,
        _ => type
    };
}
