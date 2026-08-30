namespace CSweet.Contracts.Core;

public static class ArtifactActions
{
    public const string Create = "artifact.create";
    public const string Read = "artifact.read";
    public const string Revise = "artifact.revise";
    public const string Submit = "artifact.submit";
    public const string Decide = "artifact.decide";
    public const string RequestAccess = "artifact.request-access";
    public static readonly IReadOnlySet<string> FileActions = new HashSet<string>(
        [Read, Revise, Submit, Decide], StringComparer.Ordinal);
}

public static class ArtifactPlatformCapabilities
{
    public const string Create = "platform.artifact.create.v1";
    public const string Read = "platform.artifact.read.v1";
    public const string Revise = "platform.artifact.revise.v1";
    public const string Submit = "platform.artifact.submit.v1";
    public const string Decide = "platform.artifact.decide.v1";
    public const string DecideV2 = "platform.artifact.decide.v2";
    public const string RequestAccess = "platform.artifact.request-access.v1";
    public const string PackageCreate = "platform.artifact-package.create.v1";
    public const string PackageRead = "platform.artifact-package.read.v1";
    public const string PackageSubmit = "platform.artifact-package.submit.v1";
    public const string PackageDecide = "platform.artifact-package.decide.v1";
    public const string AccessDecisionEvent = "com.csweet.artifact.access-decision.v1";
}

public sealed record ArtifactDocumentQuery(
    string? Search = null,
    Guid? FolderId = null,
    Guid? PackageId = null,
    string? Status = null,
    string? DocumentType = null,
    bool IncludeArchived = false,
    string? CreatorOrSteward = null,
    Guid? OriginWorkItemId = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null);

public sealed record ArtifactDocumentSummary(
    Guid Id,
    Guid OrganizationId,
    string Title,
    string DocumentType,
    string Status,
    Guid? FolderId,
    Guid? PackageId,
    Guid? LatestRevisionId,
    Guid? SubmittedRevisionId,
    Guid? AcceptedRevisionId,
    int LatestRevisionNumber,
    string CreatorDisplayName,
    bool CreatorIsFormerEmployee,
    Guid? StewardOrganizationUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt)
{
    public Guid? WorkstreamId { get; init; }
    public Guid? TeamId { get; init; }
}

public sealed record ArtifactRevisionResponse(
    Guid Id,
    Guid ArtifactId,
    int Number,
    Guid? BaseRevisionId,
    string Content,
    string ContentSha256,
    string Status,
    string CreatorDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DecidedAt);

public sealed record ArtifactDocumentDetail(
    ArtifactDocumentSummary Document,
    ArtifactRevisionResponse LatestRevision,
    ArtifactRevisionResponse? AcceptedRevision,
    IReadOnlyList<ArtifactRevisionResponse> Revisions,
    IReadOnlyList<ArtifactGrantResponse> Grants,
    IReadOnlyList<ArtifactAccessRequestResponse> AccessRequests);

public sealed record CreateArtifactDocumentRequest(
    string Title,
    string Content,
    string DocumentType,
    string IdempotencyKey,
    Guid? FolderId = null,
    Guid? PackageId = null,
    Guid? OriginConversationId = null,
    Guid? OriginWorkItemId = null,
    Guid? StewardOrganizationUserId = null)
{
    public Guid? WorkstreamId { get; init; }
    public Guid? TeamId { get; init; }
}

public sealed record CreateArtifactRevisionRequest(
    Guid ExpectedBaseRevisionId,
    string Content,
    string IdempotencyKey);

public sealed record SubmitArtifactRevisionRequest(
    Guid RevisionId,
    string IdempotencyKey,
    Guid? ConversationId = null,
    Guid? ReviewerOrganizationUserId = null);

public sealed record DecideArtifactRevisionRequest(
    Guid RevisionId,
    string Decision,
    string? Comment,
    string IdempotencyKey,
    Guid? EvidenceConversationMessageId = null);

public sealed record MoveArtifactRequest(Guid? FolderId, string IdempotencyKey);
public sealed record ReassignArtifactStewardRequest(Guid? StewardOrganizationUserId, string IdempotencyKey);
public sealed record ArtifactArchiveRequest(string IdempotencyKey);

public sealed record ArtifactFolderResponse(
    Guid Id, Guid OrganizationId, Guid? ParentFolderId, string Name,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt);
public sealed record CreateArtifactFolderRequest(string Name, Guid? ParentFolderId, string IdempotencyKey);
public sealed record UpdateArtifactFolderRequest(string Name, Guid? ParentFolderId, string IdempotencyKey);

public sealed record ArtifactGrantResponse(
    Guid Id, string SubjectKind, Guid SubjectId, string SubjectDisplayName,
    string Action, DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt,
    Guid? AuthorizingGrantId, long Revision);

public sealed record UpsertArtifactGrantRequest(
    string SubjectKind, Guid SubjectId, IReadOnlyList<string> Actions,
    DateTimeOffset? ExpiresAt, string IdempotencyKey);

public sealed record RequestArtifactAccessRequest(
    IReadOnlyList<string> Actions, string Justification, string IdempotencyKey,
    DateTimeOffset? ExpiresAt = null);

public sealed record DecideArtifactAccessRequest(
    string Decision, string? Comment, string IdempotencyKey,
    DateTimeOffset? GrantExpiresAt = null, Guid? EvidenceConversationMessageId = null);

public sealed record ArtifactAccessRequestResponse(
    Guid Id, Guid ArtifactId, string SubjectKind, Guid SubjectId, string SubjectDisplayName,
    IReadOnlyList<string> Actions, string Justification, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? DecidedAt);

public sealed record ArtifactAccessDecisionEvent(
    Guid RequestId, Guid ArtifactId, string Outcome, IReadOnlyList<string> Actions,
    IReadOnlyList<Guid> GrantIds, IReadOnlyList<long> GrantRevisions, DateTimeOffset DecidedAt);

public sealed record ArtifactPackageMemberResponse(
    Guid Id, Guid ArtifactId, Guid? AcceptedRevisionId, int Position, string RequiredDocumentType);
public sealed record ArtifactPackageResponse(
    Guid Id, Guid OrganizationId, string Name, string PackageType, int Version, string Status,
    IReadOnlyList<ArtifactPackageMemberResponse> Members,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? AcceptedAt, DateTimeOffset? ArchivedAt);
public sealed record CreateArtifactPackageRequest(
    string Name, string PackageType, IReadOnlyList<ArtifactPackageMemberInput> Members, string IdempotencyKey);
public sealed record ArtifactPackageMemberInput(Guid ArtifactId, int Position, string RequiredDocumentType);
public sealed record SubmitArtifactPackageRequest(string IdempotencyKey);
public sealed record DecideArtifactPackageRequest(string Decision, string IdempotencyKey);
