using System.ComponentModel.DataAnnotations;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Contracts.WorkManagement;

public static class WorkOrchestrationActions
{
    public const string Read = WorkManagementCapabilityNames.OrchestrationRead;
    public const string Configure = "work.orchestration.configure";
    public const string Publish = "work.orchestration.publish";
    public const string Preflight = WorkManagementCapabilityNames.OrchestrationPreflight;
    public const string Start = WorkManagementCapabilityNames.OrchestrationStart;
    public const string Pause = WorkManagementCapabilityNames.OrchestrationPause;
    public const string Resume = WorkManagementCapabilityNames.OrchestrationResume;
    public const string Cancel = WorkManagementCapabilityNames.OrchestrationCancel;
    public const string Retry = WorkManagementCapabilityNames.OrchestrationRetry;
    public const string ConfigureSoftwareTemplate =
        WorkManagementCapabilityNames.OrchestrationConfigureSoftwareTemplate;
    public const string ConfigureProfile = WorkManagementCapabilityNames.OrchestrationConfigureProfileV1;
    public const string CompleteManual = "work.orchestration.manual.complete";
    public const string DecideApproval = "work.orchestration.approval.decide";

    public static IReadOnlyList<string> All { get; } =
        [Read, Configure, Publish, ConfigureSoftwareTemplate, ConfigureProfile, Preflight, Start, Pause, Resume, Cancel, Retry, CompleteManual, DecideApproval];
}

public static class WorkFlowMetricActions
{
    public const string Read = WorkManagementCapabilityNames.FlowMetricsReadV1;
}

public sealed record SaveWorkOrchestrationPolicyRequest(
    [property: Required, MaxLength(160)] string Name,
    [property: Required, MaxLength(64)] string InitialStageKey,
    [property: Required, MaxLength(32)] string MergeMode,
    WorkOrchestrationConcurrencyLimits Concurrency,
    IReadOnlyList<WorkOrchestrationStageDefinition> Stages,
    IReadOnlyList<WorkOrchestrationTransitionDefinition> Transitions,
    string IdempotencyKey);

public sealed record PublishWorkOrchestrationPolicyRequest(
    Guid RevisionId,
    string IdempotencyKey);

public sealed record CreateSoftwareOrchestrationTemplateRequest(
    Guid ReadyColumnId,
    Guid DevelopmentColumnId,
    Guid DevCompleteColumnId,
    Guid QualityColumnId,
    Guid ReadyToMergeColumnId,
    Guid DoneColumnId,
    string MergeMode,
    int MaximumQualityCycles,
    string IdempotencyKey);

public sealed record WorkOrchestrationPolicyResponse(
    Guid PolicyId,
    Guid BoardId,
    Guid? PublishedRevisionId,
    IReadOnlyList<WorkOrchestrationPolicyRevision> Revisions);

public sealed record WorkOrchestrationControlRequest(
    long ExpectedRevision,
    [property: Required, MaxLength(160)] string IdempotencyKey,
    [property: MaxLength(2048)] string? Reason = null);
