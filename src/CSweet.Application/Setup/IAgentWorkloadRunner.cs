using CSweet.SatelliteOffice.Contracts.Workloads;

namespace CSweet.Application.Setup;

public interface IAgentWorkloadRunner
{
    Task<IsolationWorkloadHandle> CreateAndStartAsync(
        RuntimeWorkloadSpecification workload,
        AgentTrustLevel trustLevel,
        string? preferredProviderId = null,
        CancellationToken cancellationToken = default);

    Task<IsolationWorkloadStatus?> InspectAsync(
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        IsolationWorkloadHandle handle,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default);

    Task DestroyAsync(
        IsolationWorkloadHandle handle,
        CancellationToken cancellationToken = default);

    Task<string> GetLogsAsync(
        IsolationWorkloadHandle handle,
        int maximumBytes,
        CancellationToken cancellationToken = default);
}

public sealed class AgentWorkloadException : Exception
{
    public AgentWorkloadException(string message) : base(message) { }
    public AgentWorkloadException(string message, Exception innerException) : base(message, innerException) { }
}
