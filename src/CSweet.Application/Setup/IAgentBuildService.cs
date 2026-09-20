namespace CSweet.Application.Setup;

public interface IAgentBuildService
{
    Task<Guid> QueueAsync(
        Guid packageVersionId,
        CancellationToken cancellationToken = default);

    Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles build jobs left in a non-terminal worker-owned state
    /// (<see cref="AgentBuildStatus.Cloning"/> or <see cref="AgentBuildStatus.Building"/>)
    /// by an ungraceful host shutdown. Returns the number of reconciled jobs.
    /// </summary>
    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default);
}
