using CSweet.Application.Compute;

#if COMPUTE_GATEWAY
namespace CSweet.ExecutionGateway.Compute;
#else
namespace CSweet.Api.Compute;
#endif

/// <summary>Bounded local waiters. Authentication and current enrollment checks belong to the endpoint.</summary>
public sealed class ComputeProviderWakeChannels : IComputeProviderWakePublisher
{
    private readonly object gate = new();
    private readonly Dictionary<(Guid Organization, Guid Node, string Provider), List<Subscription>> groups = [];
    private int count;

    public Subscription Subscribe(Guid organization, Guid node, string provider)
    {
        lock (gate)
        {
            var key = (organization, node, provider);
            groups.TryGetValue(key, out var entries);
            if (count >= 1024 || entries?.Count >= 2) throw new InvalidOperationException("Provider notification capacity is unavailable.");
            if (entries is null) groups.Add(key, entries = []);
            var subscription = new Subscription(() => Remove(key));
            entries.Add(subscription); count++;
            return subscription;

            void Remove((Guid, Guid, string) scope)
            {
                lock (gate)
                {
                    if (!groups.TryGetValue(scope, out var values)) return;
                    var removed = values.RemoveAll(x => x.IsDisposed); count -= removed;
                    if (values.Count == 0) groups.Remove(scope);
                }
            }
        }
    }

    public Task<bool> PublishAsync(Guid organizationId, Guid nodeId, string providerId, Guid eventId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var published = false;
            if (groups.TryGetValue((organizationId, nodeId, providerId), out var entries))
                foreach (var entry in entries) published |= entry.Deliver(eventId);
            return Task.FromResult(published);
        }
    }

    public sealed class Subscription(Action remove) : IDisposable
    {
        private readonly TaskCompletionSource<Guid> next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposed;
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        internal bool Deliver(Guid id) => !IsDisposed && next.TrySetResult(id);
        public Task<Guid> ReadAsync(CancellationToken token) => next.Task.WaitAsync(token);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            next.TrySetCanceled(); remove();
        }
    }
}
