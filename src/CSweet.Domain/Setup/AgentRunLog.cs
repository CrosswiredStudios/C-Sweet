namespace CSweet.Domain.Setup;

public sealed class AgentRunLog
{
    public Guid Id { get; set; }
    public Guid? TaskRunId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? AgentInstallationId { get; set; }
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
    public long DurationMs { get; set; }
}
