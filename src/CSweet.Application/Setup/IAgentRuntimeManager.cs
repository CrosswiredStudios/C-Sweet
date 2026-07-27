namespace CSweet.Application.Setup;

public interface IAgentRuntimeManager
{
    Task<bool> EnsureRuntimeQueuedAsync(
        Guid installationId,
        string reason,
        bool interactive = false,
        CancellationToken cancellationToken = default);

    Task<bool> RestartRuntimeAsync(
        Guid installationId,
        string reason,
        bool interactive = false,
        CancellationToken cancellationToken = default);

    Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default);

    Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default);
    Task<int> ReconcileAsync(CancellationToken cancellationToken = default);
}

public interface IAgentRuntimeSignalService
{
    Task RecordMcpSessionEstablishedAsync(
        Guid runtimeInstanceId,
        Guid tickId,
        Guid installationId,
        string workloadToken,
        CancellationToken cancellationToken = default);

    Task RecordCompletionAsync(
        Guid runtimeInstanceId,
        Guid tickId,
        Guid installationId,
        string payloadJson,
        CancellationToken cancellationToken = default);
}
