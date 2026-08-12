namespace CSweet.Domain.Setup;

public sealed class AgentRuntimeInstance
{
    public Guid Id { get; set; }
    public Guid TickId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public AgentRuntimeStatus Status { get; private set; } = AgentRuntimeStatus.Queued;
    public string BrokerTokenHash { get; set; } = string.Empty;
    public string? IsolationProviderId { get; set; }
    public string? ProviderInstanceId { get; set; }
    public string? Reason { get; private set; }
    public string? LogExcerpt { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? McpSessionWaitingAt { get; private set; }
    public DateTimeOffset? McpSessionEstablishedAt { get; private set; }
    public DateTimeOffset? CompletionReportedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? RuntimeDeadlineAt { get; set; }
    public DateTimeOffset? LastInteractiveActivityAt { get; set; }
    public DateTimeOffset? IdleDeadlineAt { get; set; }
    public bool IsInteractive { get; set; }

    public AgentInstallation? AgentInstallation { get; set; }
    public ICollection<AgentRuntimeEvent> Events { get; set; } = [];
    public ICollection<ExecutionWorkloadAssignment> ExecutionAssignments { get; set; } = [];

    public void TransitionTo(AgentRuntimeStatus next, DateTimeOffset occurredAt, string? reason = null)
    {
        if (!CanTransition(Status, next))
            throw new InvalidOperationException($"Agent runtime cannot transition from {Status} to {next}.");

        Status = next;
        Reason = reason;
        if (next == AgentRuntimeStatus.Starting) StartedAt = occurredAt;
        if (next == AgentRuntimeStatus.WaitingForMcpSession) McpSessionWaitingAt = occurredAt;
        if (next == AgentRuntimeStatus.Running)
        {
            McpSessionEstablishedAt = occurredAt;
            McpSessionWaitingAt = null;
        }
        if (next == AgentRuntimeStatus.CompletionReported) CompletionReportedAt = occurredAt;
        if (IsTerminal(next))
        {
            CompletedAt = occurredAt;
            McpSessionWaitingAt = null;
        }
    }

    public static bool IsActive(AgentRuntimeStatus status) => status is
        AgentRuntimeStatus.Queued or AgentRuntimeStatus.Starting or
        AgentRuntimeStatus.WaitingForMcpSession or AgentRuntimeStatus.Running or
        AgentRuntimeStatus.CompletionReported or AgentRuntimeStatus.Stopping;

    public static bool IsTerminal(AgentRuntimeStatus status) => !IsActive(status);

    private static bool CanTransition(AgentRuntimeStatus current, AgentRuntimeStatus next) =>
        current == next || (current, next) switch
        {
            (AgentRuntimeStatus.Queued, AgentRuntimeStatus.Starting or AgentRuntimeStatus.Stopping or AgentRuntimeStatus.Failed or AgentRuntimeStatus.PolicyDenied or AgentRuntimeStatus.Skipped or AgentRuntimeStatus.Cancelled) => true,
            (AgentRuntimeStatus.Starting, AgentRuntimeStatus.WaitingForMcpSession or AgentRuntimeStatus.Stopping or AgentRuntimeStatus.StartFailed or AgentRuntimeStatus.Cancelled) => true,
            (AgentRuntimeStatus.WaitingForMcpSession, AgentRuntimeStatus.Running or AgentRuntimeStatus.Stopping or AgentRuntimeStatus.McpSessionTimedOut or AgentRuntimeStatus.StartFailed or AgentRuntimeStatus.Cancelled) => true,
            (AgentRuntimeStatus.Running, AgentRuntimeStatus.CompletionReported or AgentRuntimeStatus.Stopping or AgentRuntimeStatus.RuntimeTimedOut or AgentRuntimeStatus.ExitedWithoutCompletion or AgentRuntimeStatus.Failed or AgentRuntimeStatus.Cancelled) => true,
            (AgentRuntimeStatus.CompletionReported, AgentRuntimeStatus.Stopping or AgentRuntimeStatus.Completed or AgentRuntimeStatus.Failed) => true,
            (AgentRuntimeStatus.Stopping, AgentRuntimeStatus.Completed or AgentRuntimeStatus.McpSessionTimedOut or AgentRuntimeStatus.RuntimeTimedOut or AgentRuntimeStatus.ExitedWithoutCompletion or AgentRuntimeStatus.Failed or AgentRuntimeStatus.Cancelled) => true,
            _ => false
        };
}
