namespace CSweet.Application.Setup;

public interface IAgentRuntimeCleanupService
{
    Task<AgentRuntimeCleanupResult> CleanupAsync(CancellationToken cancellationToken = default);
}

public sealed record AgentRuntimeCleanupResult(
    int WorkloadsRemoved,
    int WorkspacesRemoved,
    int BuildLogsRemoved,
    int RuntimeHistoriesRemoved);
