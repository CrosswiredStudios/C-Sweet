using CSweet.Domain.Setup;

namespace CSweet.Contracts.Llm;

public sealed record UpdateLlmProviderProfileRequest(
    string Name,
    LlmProviderType ProviderType,
    string BaseUrl,
    string? ApiKey,
    bool ReplaceApiKey,
    string DefaultChatModel,
    string? DefaultEmbeddingModel,
    int? ContextWindowTokens,
    int? MaxOutputTokens,
    bool SupportsStreaming,
    bool SupportsToolCalling,
    bool SupportsStructuredOutput,
    bool SupportsVision,
    bool IsEnabled,
    int? MaximumConcurrentRequests = null);
