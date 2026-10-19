namespace CSweet.Domain.Setup;

public enum LlmProviderType
{
    LmStudio,
    UnslothStudio,
    Ollama,
    Vllm,
    OpenAiCompatible,
    OpenAi,
    GoogleGemini,
    OpenRouter,
    Groq,
    TogetherAi,
    AzureOpenAi,
    Anthropic,
    MicrosoftFoundry,
    Custom,
    // Append new providers to preserve persisted numeric values.
    MetaMuse,
    DeepSeek
}
