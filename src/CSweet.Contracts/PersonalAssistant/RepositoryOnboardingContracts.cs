namespace CSweet.Contracts.PersonalAssistant;

public sealed record RepositorySourceFile(string Path, string Content);
public sealed record RepositoryAnalysisInput(string SourceId, string Name, string Snapshot,
    IReadOnlyList<RepositorySourceFile> Files, IReadOnlyList<string> Warnings);
public sealed record AnalyzeRepositoryRequest(IReadOnlyList<RepositoryAnalysisInput> Repositories);
public sealed record RepositoryEvidence(string SourceId, string Path, string Snapshot);
public sealed record RepositoryProjectBrief(string SourceId, string Name, string Summary, string Technology,
    string CurrentState, IReadOnlyList<RepositoryEvidence> Evidence)
{ public string Category { get; init; } = "Unknown";
    public IReadOnlyList<string> Uncertainties { get; init; } = []; }
public sealed record RepositoryCompanyBrief(string BusinessName, string Industry, string Mission, string Summary,
    IReadOnlyList<RepositoryProjectBrief> Projects, IReadOnlyList<string> OpenQuestions);
public sealed record RepositoryBusinessReview(string BusinessName, string Industry, string Mission, string NextGoal,
    string Summary, IReadOnlyList<RepositoryProjectBrief> Projects, IReadOnlyList<string> OpenQuestions);
public sealed record RepositorySourceResponse(Guid Id, string Kind, string Name, string Status, string? Snapshot,
    string? Error, bool Attached);
public sealed record RepositoryOnboardingDraftResponse(Guid Id, string Status, long Revision, long SourceRevision,
    IReadOnlyList<RepositorySourceResponse> Sources, RepositoryBusinessReview? Review,
    RepositoryCompanyBrief? Analysis, Guid? OrganizationId, Guid? BriefArtifactId,
    string? Error, string? ActionUri, DateTimeOffset UpdatedAt);
public sealed record SelectOnboardingRepositoriesRequest(long ExpectedRevision, long InstallationId,
    IReadOnlyList<long> RepositoryIds);
public sealed record RepositoryDraftRevisionRequest(long ExpectedRevision);
public sealed record AnalyzeOnboardingRepositoriesRequest(long ExpectedRevision, bool ReadAccessApproved);
public sealed record SaveRepositoryBusinessReviewRequest(long ExpectedRevision, RepositoryBusinessReview Review);
public sealed record ConfirmRepositoryBusinessRequest(long ExpectedRevision, bool RepositoryManagementApproved);
public sealed record DraftGitHubInstallationLink(string? InstallationUrl);
public sealed record DraftGitHubAuthorization(string AuthorizationUrl, string State);
public sealed record CompleteDraftGitHubAuthorizationRequest(string State, long InstallationId, string Code);
public sealed record OnboardingGitHubRepository(long RepositoryId, string FullName, bool IsPrivate, bool IsArchived,
    bool Available, string? UnavailableReason);
public sealed record OnboardingGitHubAccount(long InstallationId, string Login, string AccountType);

public sealed record StartBusinessImportRequest(Guid? DraftId = null);
public sealed record AddImportedProjectsRequest(long ExpectedRevision, IReadOnlyDictionary<Guid, string> ProjectNames);
public sealed record ImportedProjectResponse(Guid ProjectId, Guid SourceId, Guid RepositoryId, string Name,
    string ProfileStatus, long ProfileRevision, RepositoryProjectBrief? InferredProfile, RepositoryProjectBrief? AcceptedProfile, string? Error);
public sealed record AcceptImportedProjectProfileRequest(long ExpectedRevision, RepositoryProjectBrief Profile);
