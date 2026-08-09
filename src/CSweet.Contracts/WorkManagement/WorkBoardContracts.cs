using System.ComponentModel.DataAnnotations;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Contracts.WorkManagement;

public static class WorkBoardActions
{
    public const string Read = WorkManagementCapabilityNames.BoardRead;
    public const string Create = WorkManagementCapabilityNames.BoardCreate;
    public const string Configure = "work.board.configure";
    public const string ConfigureColumns = WorkManagementCapabilityNames.BoardConfigureColumns;
    public const string ManageGrants = "work.board.grants.manage";
    public const string Archive = "work.board.archive";
    public const string Restore = "work.board.restore";

    public static readonly IReadOnlyList<string> All =
        [Read, Create, Configure, ConfigureColumns, ManageGrants, Archive, Restore];
}

public static class WorkItemActions
{
    public const string Read = WorkManagementCapabilityNames.ItemRead;
    public const string Create = WorkManagementCapabilityNames.ItemCreate;
    public const string Update = "work.item.update";
    public const string Start = WorkManagementCapabilityNames.ItemStart;
    public const string Move = WorkManagementCapabilityNames.ItemMove;
    public const string Complete = WorkManagementCapabilityNames.ItemComplete;
    public const string Cancel = WorkManagementCapabilityNames.ItemCancel;
    public const string Reopen = WorkManagementCapabilityNames.ItemReopen;
    public const string Transfer = WorkManagementCapabilityNames.ItemTransfer;
    public const string Comment = WorkManagementCapabilityNames.ItemComment;
    public const string Estimate = WorkManagementCapabilityNames.ItemEstimate;
    public const string QualitySubmit = WorkManagementCapabilityNames.ItemQualitySubmit;

    public static readonly IReadOnlyList<string> All =
        [Read, Create, Update, Move, Transfer, Comment, Estimate];
}

public static class WorkSprintActions
{
    public const string Read = WorkManagementCapabilityNames.SprintRead;
    public const string Create = WorkManagementCapabilityNames.SprintCreate;
    public const string Start = WorkManagementCapabilityNames.SprintStart;
    public const string Complete = WorkManagementCapabilityNames.SprintComplete;
    public const string Cancel = WorkManagementCapabilityNames.SprintCancel;
    public const string ManageScope = WorkManagementCapabilityNames.SprintManageScope;
    public const string ManageCapacity = WorkManagementCapabilityNames.SprintManageCapacity;
    public const string CarryOver = WorkManagementCapabilityNames.SprintCarryOver;
    public const string ReadReports = WorkManagementCapabilityNames.SprintReadReports;

    public static readonly IReadOnlyList<string> All =
        [Read, Create, ManageScope, ManageCapacity, CarryOver, ReadReports];
}

public static class WorkAutomationActions
{
    public const string Read = WorkManagementCapabilityNames.AutomationRead;
    public const string Manage = WorkManagementCapabilityNames.AutomationManage;

    public static readonly IReadOnlyList<string> All = [Read, Manage];
}

public static class PersonalTodoActions
{
    public const string Read = WorkManagementCapabilityNames.PersonalTodoRead;
    public const string Add = WorkManagementCapabilityNames.PersonalTodoAdd;
    public const string Reorder = WorkManagementCapabilityNames.PersonalTodoReorder;
    public const string Requeue = WorkManagementCapabilityNames.PersonalTodoRequeue;
    public const string Claim = WorkManagementCapabilityNames.PersonalTodoClaim;
    public const string Complete = WorkManagementCapabilityNames.PersonalTodoComplete;
    public const string Block = WorkManagementCapabilityNames.PersonalTodoBlock;
    public const string Release = WorkManagementCapabilityNames.PersonalTodoRelease;

    public static readonly IReadOnlyList<string> All =
        [Read, Add, Reorder, Requeue, Claim, Complete, Block, Release];
}

public sealed record WorkBoardDirectoryQuery(
    string? Search = null,
    Guid? WorkstreamId = null,
    bool IncludeArchived = false,
    bool FavoritesOnly = false);

public sealed record WorkBoardColumnResponse(
    Guid Id,
    string Name,
    string Category,
    int Position,
    string WipPolicy,
    int? WipLimit);

public sealed record WorkBoardSummaryResponse(
    Guid Id,
    Guid OrganizationId,
    Guid? WorkstreamId,
    string Name,
    string Description,
    bool IsDefault,
    bool IsArchived,
    bool IsFavorite,
    DateTimeOffset? LastVisitedAt,
    int ActiveItemCount,
    int GrantedSubjectCount,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> AllowedActions)
{
    public Guid? TeamId { get; init; }
    public Guid? ManagerOrganizationUserId { get; init; }
    public string Key { get; init; } = string.Empty;
}

public sealed record WorkBoardDetailResponse(
    WorkBoardSummaryResponse Board,
    IReadOnlyList<WorkBoardColumnResponse> Columns,
    IReadOnlyList<WorkBoardItemResponse> Items);

public sealed record WorkBoardDirectoryResponse(
    IReadOnlyList<WorkBoardSummaryResponse> Boards,
    bool CanCreateBoard);

public sealed record CreateWorkBoardRequest(
    [property: Required, MaxLength(160)] string Name,
    [property: MaxLength(2048)] string? Description,
    Guid? WorkstreamId = null,
    Guid? TeamId = null)
{
    public Guid? ManagerOrganizationUserId { get; init; }
    public string? Key { get; init; }
}

public sealed record UpdateWorkBoardRequest(
    [property: Required, MaxLength(160)] string Name,
    [property: MaxLength(2048)] string? Description,
    Guid? WorkstreamId,
    bool IsDefault,
    long? ExpectedRevision = null)
{
    public Guid? ManagerOrganizationUserId { get; init; }
    public string? Key { get; init; }
}

public sealed record SetWorkBoardFavoriteRequest(bool IsFavorite);

public sealed record WorkBoardActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string Message,
    WorkBoardDetailResponse? Board = null);

public sealed record WorkBoardColumnInput(
    Guid? Id,
    [property: Required, MaxLength(120)] string Name,
    string Category,
    string WipPolicy = "Disabled",
    int? WipLimit = null);

public sealed record ConfigureWorkBoardColumnsRequest(
    long ExpectedRevision,
    IReadOnlyList<WorkBoardColumnInput> Columns);

public sealed record WorkBoardItemResponse(
    Guid Id,
    Guid BoardId,
    Guid ColumnId,
    Guid? ParentItemId,
    Guid? SprintId,
    string Kind,
    string Title,
    string Description,
    string Status,
    string Priority,
    decimal? EstimatePoints,
    long Rank,
    long Revision,
    DateTimeOffset? DueDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? AssignedWorkerId = null,
    Guid? AssignedEmployeeId = null,
    Guid? AssignedInstallationId = null,
    string? AssignedDisplayName = null,
    SoftwareDevelopmentBrief? Development = null,
    long AssignmentRevision = 0)
{
    public string? Identifier { get; init; }
    public Guid? AccountableOrganizationUserId { get; init; }
    public IReadOnlyList<WorkStageAssignment> StageAssignments { get; init; } = [];
}

public sealed record CreateBoardWorkItemRequest(
    [property: Required, MaxLength(512)] string Title,
    [property: MaxLength(8192)] string? Description = null,
    string Kind = "Task",
    string Priority = "Medium",
    Guid? ColumnId = null,
    Guid? ParentItemId = null,
    DateTimeOffset? DueDate = null)
{
    public Guid? AccountableOrganizationUserId { get; init; }
    public IReadOnlyList<WorkStageAssignment> StageAssignments { get; init; } = [];
}

public sealed record MoveBoardWorkItemRequest(
    Guid TargetColumnId,
    Guid? BeforeItemId,
    long ExpectedRevision);

public sealed record WorkBoardGrantResponse(
    Guid Id,
    string SubjectKind,
    Guid SubjectId,
    string Action,
    bool CanDelegate,
    long Revision,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt);

public sealed record SetWorkBoardSubjectGrantsRequest(
    string SubjectKind,
    Guid SubjectId,
    IReadOnlyList<string> Actions,
    bool CanDelegate = false,
    DateTimeOffset? ExpiresAt = null);

public sealed record WorkItemCommentResponse(
    Guid Id,
    Guid WorkItemId,
    string AuthorKind,
    Guid AuthorSubjectId,
    string AuthorDisplayName,
    string Body,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt);

public sealed record WorkItemActivityResponse(
    Guid Id,
    Guid BoardId,
    Guid WorkItemId,
    string EventType,
    string Action,
    string ActorKind,
    Guid ActorSubjectId,
    string ActorDisplayName,
    string DataJson,
    DateTimeOffset OccurredAt);

public sealed record WorkItemCollaborationResponse(
    IReadOnlyList<WorkItemCommentResponse> Comments,
    IReadOnlyList<WorkItemActivityResponse> Activity);

public sealed record AddWorkItemCommentRequest(
    [property: Required, MaxLength(8192)] string Body,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record TransferWorkItemRequest(
    Guid TargetBoardId,
    Guid? TargetColumnId,
    long ExpectedRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record WorkSprintResponse(
    Guid Id,
    Guid BoardId,
    string Name,
    string Goal,
    string Status,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    decimal? CapacityPoints,
    int ItemCount,
    int CompletedItemCount,
    decimal PlannedPoints,
    decimal CompletedPoints,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateWorkSprintRequest(
    [property: Required, MaxLength(160)] string Name,
    [property: MaxLength(2048)] string? Goal,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record ChangeWorkSprintStateRequest(
    long ExpectedRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record SetWorkItemSprintRequest(
    Guid? SprintId,
    long ExpectedItemRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record SetWorkItemEstimateRequest(
    decimal? EstimatePoints,
    long ExpectedItemRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record SetWorkSprintCapacityRequest(
    decimal? CapacityPoints,
    long ExpectedSprintRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record CarryOverSprintRequest(
    Guid TargetSprintId,
    IReadOnlyList<Guid>? ItemIds,
    long ExpectedSourceSprintRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey);

public sealed record SprintCarryoverResponse(
    Guid SourceSprintId,
    Guid TargetSprintId,
    IReadOnlyList<Guid> ItemIds,
    decimal CarriedPoints);

public sealed record WorkSprintSnapshotItemResponse(
    Guid ItemId,
    string Kind,
    string Title,
    string Status,
    decimal? EstimatePoints,
    bool Completed);

public sealed record WorkSprintSnapshotResponse(
    Guid Id,
    Guid SprintId,
    string SprintName,
    string Goal,
    DateTimeOffset? StartedAt,
    DateTimeOffset CompletedAt,
    decimal? CapacityPoints,
    int CommittedItemCount,
    int CompletedItemCount,
    decimal CommittedPoints,
    decimal CompletedPoints,
    IReadOnlyList<WorkSprintSnapshotItemResponse> Items);

public sealed record WorkSprintReportResponse(
    Guid BoardId,
    int CompletedSprintCount,
    decimal AverageVelocity,
    decimal TotalCompletedPoints,
    decimal? AverageCapacityUtilizationPercent,
    IReadOnlyList<WorkSprintSnapshotResponse> Sprints,
    IReadOnlyList<WorkSprintBurndownSeriesResponse> Burndown,
    WorkSprintForecastResponse? ActiveForecast);

public sealed record WorkSprintMetricPointResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Reason,
    int ScopeItemCount,
    int CompletedItemCount,
    decimal ScopePoints,
    decimal CompletedPoints,
    decimal RemainingPoints);

public sealed record WorkSprintBurndownSeriesResponse(
    Guid SprintId,
    string SprintName,
    string Status,
    decimal? CapacityPoints,
    IReadOnlyList<WorkSprintMetricPointResponse> Points);

public sealed record WorkSprintForecastResponse(
    Guid SprintId,
    string SprintName,
    decimal RemainingPoints,
    decimal AverageVelocity,
    decimal? ProjectedSprintsRequired,
    bool IsOverCapacity);

public sealed record WorkAutomationRuleResponse(
    Guid Id,
    Guid BoardId,
    Guid AutomationIdentityId,
    string Name,
    string TriggerEventType,
    Guid? ConditionColumnId,
    string Action,
    Guid TargetColumnId,
    bool IsEnabled,
    bool HasExecutionGrant,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WorkAutomationExecutionResponse(
    Guid Id,
    Guid RuleId,
    Guid SourceActivityId,
    Guid WorkItemId,
    string Status,
    string RequiredAction,
    Guid? AuthorizingGrantId,
    long? AuthorizingGrantRevision,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CompletedAt);

public sealed record WorkAutomationDirectoryResponse(
    IReadOnlyList<WorkAutomationRuleResponse> Rules,
    IReadOnlyList<WorkAutomationExecutionResponse> RecentExecutions);

public sealed record CreateWorkAutomationRuleRequest(
    [property: Required, MaxLength(160)] string Name,
    [property: Required, MaxLength(160)] string TriggerEventType,
    Guid? ConditionColumnId,
    [property: Required, MaxLength(160)] string Action,
    Guid TargetColumnId,
    bool IsEnabled = false);

public sealed record UpdateWorkAutomationRuleRequest(
    [property: Required, MaxLength(160)] string Name,
    [property: Required, MaxLength(160)] string TriggerEventType,
    Guid? ConditionColumnId,
    [property: Required, MaxLength(160)] string Action,
    Guid TargetColumnId,
    bool IsEnabled,
    long ExpectedRevision);
