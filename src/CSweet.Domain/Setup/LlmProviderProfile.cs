namespace CSweet.Domain.Setup;

public sealed class LlmProviderProfile
{
    public const int DefaultMaximumConcurrentRequests = 4;
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public LlmProviderType ProviderType { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKeySecretName { get; set; }
    public string DefaultChatModel { get; set; } = string.Empty;
    public string? DefaultEmbeddingModel { get; set; }
    public int? ContextWindowTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public int MaximumConcurrentRequests { get; set; } = DefaultMaximumConcurrentRequests;
    public bool SupportsStreaming { get; set; }
    public bool SupportsToolCalling { get; set; }
    public bool SupportsStructuredOutput { get; set; }
    public bool SupportsVision { get; set; }
    public bool IsEnabled { get; set; }
    public DateTimeOffset? LastSuccessfulConnectionAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
