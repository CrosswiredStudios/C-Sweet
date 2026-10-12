using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Llm;

internal static class LlmProviderProfileMapper
{
    public static LlmProviderProfileResponse ToResponse(this LlmProviderProfile profile)
    {
        return new LlmProviderProfileResponse(
            profile.Id,
            profile.Name,
            profile.ProviderType,
            profile.BaseUrl,
            profile.ApiKeySecretName,
            profile.DefaultChatModel,
            profile.DefaultEmbeddingModel,
            profile.ContextWindowTokens,
            profile.MaxOutputTokens,
            profile.SupportsStreaming,
            profile.SupportsToolCalling,
            profile.SupportsStructuredOutput,
            profile.SupportsVision,
            profile.IsEnabled,
            profile.LastSuccessfulConnectionAt,
            profile.CreatedAt,
            profile.UpdatedAt,
            profile.MaximumConcurrentRequests);
    }
}
