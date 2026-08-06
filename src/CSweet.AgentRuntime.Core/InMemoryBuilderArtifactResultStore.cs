using System.Collections.Concurrent;
using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.Core;

public sealed class InMemoryBuilderArtifactResultStore : IBuilderArtifactResultStore, IBuilderArtifactResultPublisher
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<BuilderArtifactResult>> _results = [];

    public Task<BuilderArtifactResult> WaitAsync(Guid workloadId, CancellationToken cancellationToken = default)
    {
        if (workloadId == Guid.Empty) throw new ArgumentException("A workload identifier is required.", nameof(workloadId));
        return _results.GetOrAdd(workloadId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(cancellationToken);
    }

    public Task PublishAsync(BuilderArtifactResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(result);
        if (result.WorkloadId == Guid.Empty || result.Artifact.Digest.Length != 71 ||
            !result.Artifact.Digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidDataException("The builder artifact result is invalid.");
        if (!_results.GetOrAdd(result.WorkloadId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(result))
            throw new InvalidOperationException("A builder artifact result was already published.");
        return Task.CompletedTask;
    }
}
