using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;

namespace CSweet.AI.Providers;

public static class LlmProviderPresets
{
    public static IReadOnlyList<LlmProviderPreset> AllLocalhost()
    {
        return
        [
            LmStudioLocalhost(),
            UnslothStudioLocalhost(),
            OllamaLocalhost(),
            VllmLocalhost()
        ];
    }

    public static LlmProviderPreset LmStudioLocalhost()
    {
        return new LlmProviderPreset
        {
            Name = "Local LM Studio",
            ProviderType = LlmProviderType.LmStudio,
            BaseUrl = "http://localhost:1234/v1",
            ApiKeyPlaceholder = "lm-studio",
            SupportsStreaming = true,
            SupportsToolCalling = false,
            SupportsStructuredOutput = false,
            SupportsVision = false
        };
    }

    public static LlmProviderPreset UnslothStudioLocalhost()
    {
        return LocalPreset(
            "Local Unsloth Studio",
            LlmProviderType.UnslothStudio,
            "http://localhost:8888/v1",
            "unsloth");
    }

    public static LlmProviderPreset OllamaLocalhost()
    {
        return LocalPreset(
            "Local Ollama",
            LlmProviderType.Ollama,
            "http://localhost:11434/v1",
            "ollama");
    }

    public static LlmProviderPreset VllmLocalhost()
    {
        return LocalPreset(
            "Local vLLM",
            LlmProviderType.Vllm,
            "http://localhost:8000/v1",
            "vllm");
    }

    private static LlmProviderPreset LocalPreset(
        string name,
        LlmProviderType providerType,
        string baseUrl,
        string apiKeyPlaceholder)
    {
        return new LlmProviderPreset
        {
            Name = name,
            ProviderType = providerType,
            BaseUrl = baseUrl,
            ApiKeyPlaceholder = apiKeyPlaceholder,
            SupportsStreaming = true,
            SupportsToolCalling = false,
            SupportsStructuredOutput = false,
            SupportsVision = false
        };
    }
}
