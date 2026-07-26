using CSweet.Domain.Setup;

namespace CSweet.Contracts.GenAi;

public static class GenAiCapabilities
{
    public const string ImageGenerate = "genai.image.generate.v1";
    public const string ImageEdit = "genai.image.edit.v1";
    public const string VideoGenerate = "genai.video.generate.v1";
    public const string VideoEdit = "genai.video.edit.v1";
    public const string JobRead = "genai.job.read.v1";
    public const string JobCancel = "genai.job.cancel.v1";

    public static readonly IReadOnlySet<string> Operations = new HashSet<string>(StringComparer.Ordinal)
    {
        ImageGenerate, ImageEdit, VideoGenerate, VideoEdit
    };

    public static GenAiOperationType? ToOperation(string capability) => capability switch
    {
        ImageGenerate => GenAiOperationType.ImageGeneration,
        ImageEdit => GenAiOperationType.ImageEditing,
        VideoGenerate => GenAiOperationType.VideoGeneration,
        VideoEdit => GenAiOperationType.VideoEditing,
        _ => null
    };
}

public sealed record CreateGenAiProviderProfileRequest(
    string Name,
    GenAiProviderType ProviderType,
    string BaseUrl,
    string? ApiKey);

public sealed record UpdateGenAiProviderProfileRequest(
    string Name,
    GenAiProviderType ProviderType,
    string BaseUrl,
    string? ApiKey,
    bool ReplaceApiKey,
    bool IsEnabled);

public sealed record GenAiProviderProfileResponse(
    Guid Id,
    string Name,
    GenAiProviderType ProviderType,
    string BaseUrl,
    bool HasApiKey,
    bool IsEnabled,
    DateTimeOffset? LastSuccessfulConnectionAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<GenAiOperationConfigurationResponse> Operations);

public sealed record SaveGenAiOperationConfigurationRequest(
    GenAiOperationType OperationType,
    string Name,
    string? ModelId,
    string? TemplateJson,
    string? OutputSelector,
    string? DefaultsJson,
    bool IsEnabled);

public sealed record GenAiOperationConfigurationResponse(
    Guid Id,
    Guid ProviderProfileId,
    GenAiOperationType OperationType,
    string Name,
    string? ModelId,
    string? TemplateJson,
    string? OutputSelector,
    string? DefaultsJson,
    bool IsEnabled,
    bool IsDefault,
    DateTimeOffset? LastValidatedAt);

public sealed record GenAiActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string? Message,
    GenAiProviderProfileResponse? Profile = null,
    GenAiOperationConfigurationResponse? Operation = null);

public sealed record GenAiConnectionTestResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    DateTimeOffset TestedAt);

public sealed record SetGenAiOperationDefaultRequest(Guid OperationConfigurationId);

public sealed record GenAiMediaRequest(
    string Prompt,
    Guid? OperationConfigurationId = null,
    string? NegativePrompt = null,
    int? Seed = null,
    int? Width = null,
    int? Height = null,
    string? AspectRatio = null,
    double? DurationSeconds = null,
    double? EditStrength = null,
    IReadOnlyList<Guid>? SourceAssetIds = null,
    Guid? MaskAssetId = null,
    string? IdempotencyKey = null);

public sealed record GenAiJobLookupRequest(Guid JobId);

public sealed record GenAiJobResponse(
    Guid Id,
    GenAiOperationType OperationType,
    GenAiJobStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MediaAssetResponse> Assets);

public sealed record MediaAssetResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    int? Width,
    int? Height,
    double? DurationSeconds,
    DateTimeOffset CreatedAt);
