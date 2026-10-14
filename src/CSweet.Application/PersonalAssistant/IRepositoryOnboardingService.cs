using CSweet.Contracts.PersonalAssistant;

namespace CSweet.Application.PersonalAssistant;

public interface IRepositoryOnboardingService
{
    Task<RepositoryOnboardingDraftResponse> CreateAsync(Guid userId, CancellationToken token = default);
    Task<IReadOnlyList<RepositoryOnboardingDraftResponse>> ListAsync(Guid userId, CancellationToken token = default);
    Task<RepositoryOnboardingDraftResponse> GetAsync(Guid userId, Guid id, CancellationToken token = default);
    Task<DraftGitHubAuthorization> AuthorizeGitHubAsync(Guid userId, Guid id, CancellationToken token = default);
    Task<RepositoryOnboardingDraftResponse> CompleteGitHubAsync(Guid userId, Guid id, CompleteDraftGitHubAuthorizationRequest request, CancellationToken token = default);
    Task<IReadOnlyList<OnboardingGitHubAccount>> AccountsAsync(Guid userId, Guid id, CancellationToken token = default);
    Task<IReadOnlyList<OnboardingGitHubRepository>> RepositoriesAsync(Guid userId, Guid id, long installationId, CancellationToken token = default);
    Task<RepositoryOnboardingDraftResponse> SelectAsync(Guid userId, Guid id, SelectOnboardingRepositoriesRequest request, CancellationToken token = default);
    Task<RepositoryOnboardingDraftResponse> UploadAsync(Guid userId, Guid id, long expectedRevision, string fileName, Stream archive, CancellationToken token = default);
    Task<RepositoryOnboardingDraftResponse> RemoveAsync(Guid userId, Guid id, Guid sourceId, long expectedRevision, CancellationToken token = default);
    Task<RepositoryOnboardingDraftResponse> AnalyzeAsync(Guid userId, Guid id, AnalyzeOnboardingRepositoriesRequest request, CancellationToken token = default);
    Task<RepositoryOnboardingDraftResponse> SaveReviewAsync(Guid userId, Guid id, SaveRepositoryBusinessReviewRequest request, CancellationToken token = default);
    Task<RepositoryOnboardingDraftResponse> ConfirmAsync(Guid userId, Guid id, ConfirmRepositoryBusinessRequest request, CancellationToken token = default);
    Task<RepositoryOnboardingDraftResponse> RetryAsync(Guid userId, Guid id, long expectedRevision, CancellationToken token = default);
    Task DiscardAsync(Guid userId, Guid id, long expectedRevision, CancellationToken token = default);
    Task<bool> ProcessNextAsync(string workerId, CancellationToken token = default);
}
