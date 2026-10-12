using CSweet.Domain.Setup;

namespace CSweet.Contracts.Llm;

public sealed record LlmProviderProfileResponse(
    Guid Id,
    string Name,
    LlmProviderType ProviderType,
    string BaseUrl,
    string? ApiKeySecretName,
    string DefaultChatModel,
    string? DefaultEmbeddingModel,
    int? ContextWindowTokens,
    int? MaxOutputTokens,
    bool SupportsStreaming,
    bool SupportsToolCalling,
    bool SupportsStructuredOutput,
    bool SupportsVision,
    bool IsEnabled,
    DateTimeOffset? LastSuccessfulConnectionAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MaximumConcurrentRequests = LlmProviderProfile.DefaultMaximumConcurrentRequests);
