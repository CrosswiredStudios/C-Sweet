namespace CSweet.Domain.Setup;

/// <summary>Retains teardown evidence after an assignment is fenced or moved to another Office.</summary>
public sealed class ExecutionAssignmentAttempt
{
    public Guid AssignmentId { get; set; }
    public long FencingEpoch { get; set; }
    public Guid ExecutionNodeId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string? ProviderInstanceId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public bool NeverCreated { get; set; }
}
