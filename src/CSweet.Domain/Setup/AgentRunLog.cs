namespace CSweet.Domain.Setup;

public sealed class AgentRunLog
{
    public Guid Id { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? RequestEvidenceJson { get; set; }
    public Guid? TaskRunId { get; set; }
    public Guid? AgentWorkItemId { get; set; }
    public Guid? WorkItemId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public Guid? BenchmarkTrialId { get; set; }
    public Guid? QueueJobId { get; set; }
    public DateTimeOffset? ProviderStartedAt { get; set; }
    public string MeasurementKind { get; set; } = "Legacy";
    public string AttributionKind { get; set; } = "Unknown";
    public string AncestorWorkItemIdsJson { get; set; } = "[]";
    public string? AgentPackageVersion { get; set; }
    public string? ConfigurationDigest { get; set; }
    public string? InferenceSettingsJson { get; set; }
    public long? ReportedInputTokens { get; set; }
    public long? ReportedOutputTokens { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? AgentInstallationId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? ChatTurnId { get; set; }
    public string AgentKey { get; set; } = string.Empty;
    public Guid ProviderProfileId { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PromptHash { get; set; } = string.Empty;
    public string? PromptPreview { get; set; }
    public string? OutputPreview { get; set; }
    public string? FailureMessage { get; set; }
    public int? TokenInputCount { get; set; }
    public int? TokenOutputCount { get; set; }
    public int? TokenCachedInputCount { get; set; }
    public int? TokenReasoningCount { get; set; }
    public string InvocationKind { get; set; } = "agent-inference";
    public int? InvocationSequence { get; set; }
    public int? PromptMessageCharacters { get; set; }
    public int? PromptInstructionCharacters { get; set; }
    public int? PromptToolCharacters { get; set; }
    public int? PromptMemoryCharacters { get; set; }
    public string? UsageAdditionalCountsJson { get; set; }
    public long DurationMs { get; set; }
}
