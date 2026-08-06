using System.Collections.Concurrent;
using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.Core;

/// <summary>
/// Deterministic orchestration test double. Production registration is intentionally absent.
/// It does not execute workloads and must never be treated as a security boundary.
/// </summary>
public sealed class InMemoryAgentIsolationProvider(
    IsolationProviderDescriptor descriptor,
    IsolationProviderCertification? certification = null) : IAgentIsolationProvider
{
    private readonly ConcurrentDictionary<Guid, StoredWorkload> _workloads = [];

    public IsolationProviderDescriptor Descriptor { get; } = descriptor;

    public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new IsolationProviderProbeResult(Descriptor, true, null, certification));

    public Task<IsolationWorkloadHandle> CreateAsync(IsolationWorkloadSpec workload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workload.ResourceLimits.Validate();
        var handle = new IsolationWorkloadHandle(
            Descriptor.ProviderId,
            workload.WorkloadId,
            $"memory-{workload.WorkloadId:N}",
            workload.Kind);
        var stored = new StoredWorkload(
            workload,
            new IsolationWorkloadStatus(
                handle,
                IsolationWorkloadState.Created,
                IsolationTerminationReason.None,
                null,
                null,
                null,
                null,
                null));
        if (!_workloads.TryAdd(workload.WorkloadId, stored))
            throw new InvalidOperationException("The workload already exists.");
        return Task.FromResult(handle);
    }

    public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = Get(handle);
        if (stored.Status.State != IsolationWorkloadState.Created)
            throw new InvalidOperationException("Only a created workload can be started.");
        stored.Status = stored.Status with
        {
            State = IsolationWorkloadState.Running,
            StartedAt = DateTimeOffset.UtcNow
        };
        stored.Logs.Add(new IsolationLogChunk(DateTimeOffset.UtcNow, "system", "workload-started"u8.ToArray(), false));
        return Task.CompletedTask;
    }

    public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_workloads.TryGetValue(handle.WorkloadId, out var stored) && Matches(handle, stored.Status.Handle)
            ? stored.Status
            : null);
    }

    public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (gracePeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        var stored = Get(handle);
        if (stored.Status.State is not (IsolationWorkloadState.Running or IsolationWorkloadState.Starting or IsolationWorkloadState.BootstrappingGuest))
            throw new InvalidOperationException("The workload is not running.");
        stored.Status = stored.Status with
        {
            State = IsolationWorkloadState.Stopped,
            TerminationReason = IsolationTerminationReason.Completed,
            ExitCode = 0,
            FinishedAt = DateTimeOffset.UtcNow
        };
        stored.Logs.Add(new IsolationLogChunk(DateTimeOffset.UtcNow, "system", "workload-stopped"u8.ToArray(), false));
        return Task.CompletedTask;
    }

    public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = Get(handle);
        stored.Status = stored.Status with { State = IsolationWorkloadState.Destroyed };
        if (!_workloads.TryRemove(handle.WorkloadId, out _)) throw new KeyNotFoundException("The workload does not exist.");
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(
        IsolationWorkloadHandle handle,
        int maximumBytes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var total = 0;
        foreach (var chunk in Get(handle).Logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total + chunk.Content.Length > maximumBytes) yield break;
            total += chunk.Content.Length;
            yield return chunk;
            await Task.Yield();
        }
    }

    private StoredWorkload Get(IsolationWorkloadHandle handle)
    {
        if (!_workloads.TryGetValue(handle.WorkloadId, out var stored) || !Matches(handle, stored.Status.Handle))
            throw new KeyNotFoundException("The workload does not exist.");
        return stored;
    }

    private static bool Matches(IsolationWorkloadHandle left, IsolationWorkloadHandle right) =>
        left == right;

    private sealed class StoredWorkload(IsolationWorkloadSpec spec, IsolationWorkloadStatus status)
    {
        public IsolationWorkloadSpec Spec { get; } = spec;
        public IsolationWorkloadStatus Status { get; set; } = status;
        public List<IsolationLogChunk> Logs { get; } = [];
    }
}
