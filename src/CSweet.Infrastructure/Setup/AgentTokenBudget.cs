using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;

namespace CSweet.Infrastructure.Setup;

/// <summary>User-editable budgets shared by every platform-governed agent inference.</summary>
public static class AgentTokenBudget
{
    public const string ContextKey = "maxContextWindowTokens";
    public const string OutputKey = "maxOutputTokens";
    public const int DefaultContextTokens = 256_000;
    public const int DefaultOutputTokens = 128_000;

    public static bool AppliesTo(PluginManifest manifest) =>
        manifest.Requires.Any(x => x.Name == CSweet.Agent.SDK.PlatformCapabilities.LlmChatStream) ||
        manifest.Configuration.Any(x => x.Type == "llm-provider" || AgentConfigurationRules.NormalizeType(x.Type) ==
            CSweet.Contracts.Agents.AgentConfigurationFieldTypes.LlmProvider || x.Key == ContextKey || x.Key == OutputKey);

    public static IReadOnlyList<PluginConfigurationField> Fields(PluginManifest manifest)
    {
        if (!AppliesTo(manifest)) return manifest.Configuration;
        // These are platform settings, including for packages predating the token-budget fields.
        // Signed package limits must not turn a user-editable budget into a hidden workflow cap.
        return manifest.Configuration.Where(x => x.Key is not (ContextKey or OutputKey)).Concat([
            new PluginConfigurationField
            {
                Key = ContextKey, Type = "number", Label = "Maximum context-window tokens",
                Description = "Total context budget for agent model work. Choose a value supported by the selected model.",
                Minimum = 2, Step = 1, DefaultValue = JsonSerializer.SerializeToElement(DefaultContextTokens)
            },
            new PluginConfigurationField
            {
                Key = OutputKey, Type = "number", Label = "Maximum output tokens",
                Description = "Output budget for every agent model call, including reasoning. Choose a value supported by the selected model.",
                Minimum = 1, Step = 1, DefaultValue = JsonSerializer.SerializeToElement(DefaultOutputTokens),
                LessThanFieldKey = ContextKey
            }
        ]).ToArray();
    }

    public static Dictionary<string, JsonElement> WithDefaults(PluginManifest manifest,
        IReadOnlyDictionary<string, JsonElement> settings)
    {
        var result = settings.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal);
        if (AppliesTo(manifest))
        {
            result.TryAdd(ContextKey, JsonSerializer.SerializeToElement(DefaultContextTokens));
            result.TryAdd(OutputKey, JsonSerializer.SerializeToElement(DefaultOutputTokens));
        }
        return result;
    }

    public static int ResolveOutput(IReadOnlyDictionary<string, JsonElement> settings)
    {
        var context = Read(settings, ContextKey, DefaultContextTokens);
        var output = Read(settings, OutputKey, DefaultOutputTokens);
        if (output >= context)
            throw new AgentInstallationException("Maximum output tokens must be less than maximum context-window tokens. Update the agent configuration.");
        return output;
    }

    private static int Read(IReadOnlyDictionary<string, JsonElement> settings, string key, int fallback)
    {
        if (!settings.TryGetValue(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 1)
            throw new AgentInstallationException($"'{key}' must be a positive whole number in the agent configuration.");
        return number;
    }
}
