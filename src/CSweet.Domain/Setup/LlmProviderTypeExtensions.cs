namespace CSweet.Domain.Setup;

public static class LlmProviderTypeExtensions
{
    public static bool UsesOpenAiCompatibleApi(this LlmProviderType providerType)
    {
        return providerType is LlmProviderType.LmStudio
            or LlmProviderType.UnslothStudio
            or LlmProviderType.Ollama
            or LlmProviderType.Vllm
            or LlmProviderType.OpenAiCompatible
            or LlmProviderType.OpenAi
            or LlmProviderType.GoogleGemini
            or LlmProviderType.OpenRouter
            or LlmProviderType.Groq
            or LlmProviderType.TogetherAi
            or LlmProviderType.MetaMuse
            or LlmProviderType.DeepSeek
            or LlmProviderType.Custom;
    }

    public static bool IsLocalRuntime(this LlmProviderType providerType)
    {
        return providerType is LlmProviderType.LmStudio
            or LlmProviderType.UnslothStudio
            or LlmProviderType.Ollama
            or LlmProviderType.Vllm;
    }

    public static bool IsHostedProvider(this LlmProviderType providerType)
    {
        return providerType is LlmProviderType.OpenAi
            or LlmProviderType.GoogleGemini
            or LlmProviderType.OpenRouter
            or LlmProviderType.Groq
            or LlmProviderType.TogetherAi
            or LlmProviderType.MetaMuse
            or LlmProviderType.DeepSeek;
    }
}
