using CSweet.Domain.Setup;

namespace CSweet.Contracts.Llm;

public static class LocalLlmProviderDiscoveryStatuses
{
    public const string Added = "added";
    public const string AlreadyConfigured = "already_configured";
    public const string NotFound = "not_found";
}

public sealed record LocalLlmProviderDiscoveryResult(
    LlmProviderType ProviderType,
    string Name,
    string? BaseUrl,
    string Status,
    int ModelCount,
    string? Message);

public sealed record LocalLlmProviderDiscoveryResponse(
    IReadOnlyList<LlmProviderProfileResponse> Profiles,
    IReadOnlyList<LocalLlmProviderDiscoveryResult> Results);
