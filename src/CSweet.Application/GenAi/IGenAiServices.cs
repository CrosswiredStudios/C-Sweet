using CSweet.Contracts.GenAi;
using CSweet.Domain.Setup;

namespace CSweet.Application.GenAi;

public interface IGenAiProviderProfileService
{
    Task<IReadOnlyList<GenAiProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default);
    Task<GenAiProviderProfileResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GenAiActionResponse> CreateAsync(CreateGenAiProviderProfileRequest request, CancellationToken cancellationToken = default);
    Task<GenAiActionResponse> UpdateAsync(Guid id, UpdateGenAiProviderProfileRequest request, CancellationToken cancellationToken = default);
    Task<GenAiActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GenAiConnectionTestResponse> TestDraftAsync(TestGenAiProviderConnectionRequest request, CancellationToken cancellationToken = default);
    Task<GenAiConnectionTestResponse> TestAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GenAiActionResponse> SaveOperationAsync(Guid providerId, Guid? operationId, SaveGenAiOperationConfigurationRequest request, CancellationToken cancellationToken = default);
    Task<GenAiActionResponse> DeleteOperationAsync(Guid providerId, Guid operationId, CancellationToken cancellationToken = default);
    Task<GenAiActionResponse> SetDefaultAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public interface ILocalGenAiProviderDiscoveryService
{
    Task<LocalGenAiProviderDiscoveryResponse> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface IGenAiJobService
{
    Task<GenAiJobResponse> StartAsync(Guid organizationId, Guid installationId, GenAiOperationType operationType, GenAiMediaRequest request, CancellationToken cancellationToken = default);
    Task<GenAiJobResponse?> GetAsync(Guid jobId, Guid organizationId, Guid? installationId = null, CancellationToken cancellationToken = default);
    Task<GenAiJobResponse?> CancelAsync(Guid jobId, Guid organizationId, Guid? installationId = null, CancellationToken cancellationToken = default);
}

public interface IMediaAssetService
{
    Task<MediaAssetResponse> SaveUploadAsync(Guid organizationId, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default);
    Task<MediaAssetResponse?> GetAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default);
    Task<(MediaAssetResponse Asset, Stream Content)?> OpenReadAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default);
}

public interface IMediaAssetStore
{
    Task<(string StorageKey, long SizeBytes, string Sha256)> SaveAsync(string fileName, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}

public interface IResumableMediaUploadService
{
    Task<MediaUploadSessionResponse> CreateAsync(Guid organizationId, CreateMediaUploadSessionRequest request,
        CancellationToken cancellationToken = default);
    Task<MediaUploadSessionResponse?> GetAsync(Guid organizationId, Guid sessionId,
        CancellationToken cancellationToken = default);
    Task<MediaUploadSessionResponse> AppendAsync(Guid organizationId, Guid sessionId, long offset,
        long contentLength, Stream content, CancellationToken cancellationToken = default);
    Task<MediaUploadSessionResponse> CompleteAsync(Guid organizationId, Guid sessionId,
        CancellationToken cancellationToken = default);
    Task CancelAsync(Guid organizationId, Guid sessionId, CancellationToken cancellationToken = default);
    Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default);
}

public sealed record GenAiAdapterSubmission(string ProviderJobId, bool IsComplete, IReadOnlyList<GenAiAdapterOutput> Outputs);
public sealed record GenAiAdapterPollResult(bool IsComplete, bool Failed, string? ErrorCode, string? ErrorMessage, IReadOnlyList<GenAiAdapterOutput> Outputs);
public sealed record GenAiAdapterOutput(string FileName, string ContentType, Stream Content);
public sealed record GenAiAdapterInput(Guid Id, string FileName, string ContentType, Stream Content);

public interface IGenAiProviderAdapter
{
    GenAiProviderType ProviderType { get; }
    Task<GenAiConnectionTestResponse> TestAsync(GenAiProviderProfile profile, string? apiKey, CancellationToken cancellationToken);
    Task ValidateOperationAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, CancellationToken cancellationToken);
    Task<GenAiAdapterSubmission> SubmitAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, GenAiMediaRequest request, IReadOnlyDictionary<Guid, GenAiAdapterInput> inputs, string? apiKey, CancellationToken cancellationToken);
    Task<GenAiAdapterPollResult> PollAsync(GenAiProviderProfile profile, GenAiOperationConfiguration operation, string providerJobId, string? apiKey, CancellationToken cancellationToken);
    Task CancelAsync(GenAiProviderProfile profile, string providerJobId, string? apiKey, CancellationToken cancellationToken);
}
