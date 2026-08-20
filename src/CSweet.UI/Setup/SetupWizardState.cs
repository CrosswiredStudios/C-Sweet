using CSweet.Contracts.Llm;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;

namespace CSweet.UI.Setup;

public static class SetupWizardState
{
    public static bool CanNavigateToStep(
        IReadOnlyList<string> orderedStepKeys,
        SetupStatusResponse status,
        string targetStepKey)
    {
        var targetIndex = IndexOf(orderedStepKeys, targetStepKey);
        if (targetIndex < 0)
        {
            return false;
        }

        var firstIncompleteIndex = orderedStepKeys
            .Select((key, index) => new { Key = key, Index = index })
            .Where(item => !status.Steps.Any(step => step.Key == item.Key && step.IsComplete))
            .Select(item => item.Index)
            .DefaultIfEmpty(orderedStepKeys.Count - 1)
            .First();

        return targetIndex <= firstIncompleteIndex;
    }

    public static string FirstIncompleteStepKey(
        IReadOnlyList<string> orderedStepKeys,
        SetupStatusResponse status)
    {
        return orderedStepKeys.FirstOrDefault(key =>
            !status.Steps.Any(step => step.Key == key && step.IsComplete))
            ?? orderedStepKeys.LastOrDefault()
            ?? "welcome";
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    public static LlmProviderSetupDefaults LmStudioDefaults()
    {
        return new LlmProviderSetupDefaults(
            LlmProviderType.LmStudio,
            "Local LM Studio",
            "http://localhost:1234/v1",
            "lm-studio",
            SupportsStreaming: true,
            SupportsToolCalling: false,
            SupportsStructuredOutput: false,
            SupportsVision: false);
    }

    public static IReadOnlyList<LlmProviderSetupPreset> LocalProviderPresets()
    {
        return
        [
            new(
                LlmProviderType.LmStudio,
                "LM",
                "LM Studio",
                "Friendly desktop model server",
                "Local LM Studio",
                "http://localhost:1234/v1",
                "lm-studio"),
            new(
                LlmProviderType.UnslothStudio,
                "US",
                "Unsloth Studio",
                "Run and serve tuned models locally",
                "Local Unsloth Studio",
                "http://localhost:8888/v1",
                "unsloth"),
            new(
                LlmProviderType.Ollama,
                "OL",
                "Ollama",
                "Popular local model runner",
                "Local Ollama",
                "http://localhost:11434/v1",
                "ollama"),
            new(
                LlmProviderType.Vllm,
                "vL",
                "vLLM",
                "High-throughput model serving",
                "Local vLLM",
                "http://localhost:8000/v1",
                "vllm")
        ];
    }

    public static IReadOnlyList<LlmProviderSetupPreset> HostedProviderPresets()
    {
        return
        [
            new(
                LlmProviderType.OpenAi,
                "OA",
                "OpenAI",
                "Connect GPT models directly",
                "OpenAI",
                "https://api.openai.com/v1",
                "sk-...",
                IsHosted: true,
                RequiresApiKey: true),
            new(
                LlmProviderType.GoogleGemini,
                "G",
                "Google Gemini",
                "Use Gemini through its compatible API",
                "Google Gemini",
                "https://generativelanguage.googleapis.com/v1beta/openai/",
                "AIza...",
                IsHosted: true,
                RequiresApiKey: true),
            new(
                LlmProviderType.OpenRouter,
                "OR",
                "OpenRouter",
                "One key for a broad model catalog",
                "OpenRouter",
                "https://openrouter.ai/api/v1",
                "sk-or-v1-...",
                IsHosted: true,
                RequiresApiKey: true),
            new(
                LlmProviderType.Groq,
                "GQ",
                "Groq",
                "Fast hosted inference for open models",
                "Groq",
                "https://api.groq.com/openai/v1",
                "gsk_...",
                IsHosted: true,
                RequiresApiKey: true),
            new(
                LlmProviderType.TogetherAi,
                "TA",
                "Together AI",
                "Hosted open models and dedicated endpoints",
                "Together AI",
                "https://api.together.ai/v1",
                "Your Together API key",
                IsHosted: true,
                RequiresApiKey: true)
        ];
    }

    public static LlmProviderSetupPreset CustomProviderPreset()
    {
        return new LlmProviderSetupPreset(
            LlmProviderType.Custom,
            "API",
            "Custom endpoint",
            "Configure any OpenAI-compatible server",
            "Custom provider",
            string.Empty,
            string.Empty);
    }

    public static string ProviderTestMessage(ModelCapabilityTestResult result)
    {
        if (result.ConnectionSucceeded && result.ChatSucceeded)
        {
            return "Provider test succeeded. Agent backings can be assigned when agents are imported.";
        }

        return string.IsNullOrWhiteSpace(result.FailureMessage)
            ? "Required provider checks did not pass."
            : result.FailureMessage;
    }
}

public sealed record LlmProviderSetupDefaults(
    LlmProviderType ProviderType,
    string ProviderName,
    string BaseUrl,
    string ApiKey,
    bool SupportsStreaming,
    bool SupportsToolCalling,
    bool SupportsStructuredOutput,
    bool SupportsVision);

public sealed record LlmProviderSetupPreset(
    LlmProviderType ProviderType,
    string Mark,
    string DisplayName,
    string Description,
    string ProviderName,
    string BaseUrl,
    string ApiKeyPlaceholder,
    bool IsHosted = false,
    bool RequiresApiKey = false);
