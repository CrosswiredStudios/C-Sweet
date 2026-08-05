namespace CSweet.Domain.Setup;

public enum GenAiProviderType
{
    ComfyUiLocal,
    ComfyUiCloud,
    OpenAi,
    GoogleGemini,
    Replicate
}

public enum GenAiOperationType
{
    ImageGeneration,
    ImageEditing,
    VideoGeneration,
    VideoEditing
}

public enum GenAiJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled
}

public sealed class GenAiProviderProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public GenAiProviderType ProviderType { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKeySecretName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastSuccessfulConnectionAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GenAiOperationConfiguration
{
    public Guid Id { get; set; }
    public Guid ProviderProfileId { get; set; }
    public GenAiOperationType OperationType { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ModelId { get; set; }
    public string? TemplateJson { get; set; }
    public string? OutputSelector { get; set; }
    public string? DefaultsJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastValidatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public GenAiProviderProfile? ProviderProfile { get; set; }
}

public sealed class GenAiOperationDefault
{
    public Guid Id { get; set; }
    public GenAiOperationType OperationType { get; set; }
    public Guid OperationConfigurationId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public GenAiOperationConfiguration? OperationConfiguration { get; set; }
}

public sealed class GenAiJob
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public Guid OperationConfigurationId { get; set; }
    public GenAiOperationType OperationType { get; set; }
    public GenAiJobStatus Status { get; set; }
    public string PromptHash { get; set; } = string.Empty;
    public string RequestJson { get; set; } = "{}";
    public string? ProviderJobId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptCount { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public GenAiOperationConfiguration? OperationConfiguration { get; set; }
}

public sealed class MediaAsset
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? CreatingAgentInstallationId { get; set; }
    public Guid? GenAiJobId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public GenAiJob? GenAiJob { get; set; }
}

public sealed class MediaUploadSession
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? MediaAssetId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public int ChunkSizeBytes { get; set; }
    public string? ExpectedSha256 { get; set; }
    public MediaUploadSessionStatus Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public MediaAsset? MediaAsset { get; set; }
}

public enum MediaUploadSessionStatus
{
    Active,
    Completed,
    Cancelled,
    Failed,
    Expired
}
