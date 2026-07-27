namespace CSweet.Contracts.Agents;

public sealed record AgentRuntimeReadinessResponse(
    Guid InstallationId,
    Guid? RuntimeInstanceId,
    string Stage,
    string? RuntimeStatus,
    string? Reason,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? McpSessionEstablishedAt,
    bool IsReady,
    bool IsTerminal);

public static class AgentRuntimeReadinessStages
{
    public const string Offline = "Offline";
    public const string Queued = "Queued";
    public const string StartingContainer = "StartingContainer";
    public const string WaitingForMcpSession = "WaitingForMcpSession";
    public const string Stopping = "Stopping";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
}
