using CSweet.Domain.Setup;

namespace CSweet.Contracts.Llm;

public sealed record CreateLlmProviderProfileRequest(
    string Name,
    LlmProviderType ProviderType,
    string BaseUrl,
    string? ApiKey,
    string DefaultChatModel,
    string? DefaultEmbeddingModel,
    int? ContextWindowTokens,
    int? MaxOutputTokens,
    bool SupportsStreaming,
    bool SupportsToolCalling,
    bool SupportsStructuredOutput,
    bool SupportsVision,
    int MaximumConcurrentRequests = LlmProviderProfile.DefaultMaximumConcurrentRequests);
