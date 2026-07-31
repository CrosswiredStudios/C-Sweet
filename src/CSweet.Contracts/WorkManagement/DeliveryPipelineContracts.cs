using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.WorkManagement;

public sealed record ConfigureDeliveryPipelineRequest(
    Guid DeveloperInstallationId,
    Guid QualityInstallationId,
    Guid DevelopmentColumnId,
    Guid QualityColumnId,
    Guid DoneColumnId,
    Guid RepositoryConnectionId,
    [property: Required, MaxLength(256)] string BaseBranch,
    [property: Required, MaxLength(24)] string MergeStrategy,
    bool IsEnabled,
    long ExpectedRevision = 0);

public sealed record ChangeDeliveryPipelineStateRequest(
    long ExpectedRevision,
    [property: MaxLength(512)] string? Reason = null);

public sealed record DeliveryPipelineResponse(
    Guid Id,
    Guid OrganizationId,
    Guid BoardId,
    Guid DeveloperInstallationId,
    Guid QualityInstallationId,
    Guid DevelopmentColumnId,
    Guid QualityColumnId,
    Guid DoneColumnId,
    Guid RepositoryConnectionId,
    string BaseBranch,
    string MergeStrategy,
    bool IsEnabled,
    string Status,
    string Stage,
    Guid? ActiveSprintId,
    Guid? ActiveWorkItemId,
    int QualityCycle,
    string MergeStatus,
    string? SourcePullRequestUrl,
    string? SourceCommitSha,
    string? LastError,
    string? ResumeAction,
    long Revision,
    DateTimeOffset UpdatedAt);
