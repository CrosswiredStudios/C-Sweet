namespace CSweet.Domain.Setup;

public sealed class AgentBuildJob
{
    public Guid Id { get; set; }
    public Guid PackageVersionId { get; set; }
    public Guid? ExecutionPoolId { get; set; }
    public int Attempt { get; set; } = 1;
    public AgentBuildStatus Status { get; private set; } = AgentBuildStatus.Queued;
    public string? SourceWorkspacePath { get; set; }
    public string? PackagePath { get; set; }
    public string? PackageDigest { get; set; }
    public string? LogPath { get; set; }
    public string? FailureMessage { get; set; }
    public string StepsJson { get; set; } = "[]";
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public AgentPackageVersion? PackageVersion { get; set; }
    public ICollection<ExecutionWorkloadAssignment> ExecutionAssignments { get; set; } = [];

    public void TransitionTo(AgentBuildStatus nextStatus, DateTimeOffset occurredAt)
    {
        if (!CanTransition(Status, nextStatus))
        {
            throw new InvalidOperationException(
                $"Agent build cannot transition from {Status} to {nextStatus}.");
        }

        Status = nextStatus;
        if (nextStatus == AgentBuildStatus.Cloning)
        {
            StartedAt = occurredAt;
        }

        if (nextStatus is AgentBuildStatus.Succeeded or AgentBuildStatus.Failed or AgentBuildStatus.Cancelled)
        {
            CompletedAt = occurredAt;
        }
    }

    private static bool CanTransition(AgentBuildStatus current, AgentBuildStatus next) =>
        (current, next) switch
        {
            (AgentBuildStatus.Queued, AgentBuildStatus.Cloning or AgentBuildStatus.Failed or AgentBuildStatus.Cancelled) => true,
            (AgentBuildStatus.Cloning, AgentBuildStatus.Building or AgentBuildStatus.Failed or AgentBuildStatus.Cancelled) => true,
            (AgentBuildStatus.Building, AgentBuildStatus.Succeeded or AgentBuildStatus.Failed or AgentBuildStatus.Cancelled) => true,
            _ => false
        };
}
