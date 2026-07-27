namespace CSweet.Domain.Setup;

public enum AgentRuntimeStatus
{
    Queued,
    Starting,
    WaitingForMcpSession,
    Running,
    CompletionReported,
    Stopping,
    Completed,
    StartFailed,
    McpSessionTimedOut,
    RuntimeTimedOut,
    ExitedWithoutCompletion,
    Failed,
    Cancelled,
    PolicyDenied,
    Skipped
}
