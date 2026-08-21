namespace CSweet.Domain.Setup;

public sealed class AgentSchedule
{
    public Guid Id { get; set; }
    public Guid AgentInstallationId { get; set; }
    public ActivationMode ActivationMode { get; set; }
    public int TickFrequencySeconds { get; set; }
    public DateTimeOffset? NextTickAt { get; set; }
    public DateTimeOffset? LastTickAt { get; set; }
    public DateTimeOffset? LastCompletedAt { get; set; }
    public DateTimeOffset? LastAttentionReviewAt { get; set; }
    public DateTimeOffset? NextAttentionReviewAt { get; set; }
    public DateTimeOffset? RunRequestedAt { get; set; }
    public int MaxRuntimeSeconds { get; set; }
    public int MaxRetriesPerTick { get; set; }
    public int ConsecutiveStartupFailures { get; set; }
    public DateTimeOffset? AutomaticStartSuppressedAt { get; set; }
    public OverlapPolicy OverlapPolicy { get; set; }
    public bool IsEnabled { get; set; }

    public AgentInstallation? AgentInstallation { get; set; }
}
