namespace CSweet.AgentHost.Broker;

/// <summary>Bounded, non-preemptive provider admission with starvation-safe delivery priority.</summary>
internal sealed class ProviderInferenceGate(int capacity, int queueLimit)
{
    private readonly object sync = new();
    private readonly LinkedList<Waiter> delivery = new();
    private readonly LinkedList<Waiter> conversation = new();
    private int active;
    private int deliveryStreak;

    internal async Task<IDisposable> AcquireAsync(bool isDelivery, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (sync)
        {
            if (active < capacity) { active++; return new Permit(this); }
            if (delivery.Count + conversation.Count >= queueLimit)
                throw new InvalidOperationException("The provider queue is full.");
            waiter = new();
            waiter.Node = (isDelivery ? delivery : conversation).AddLast(waiter);
        }
        using var registration = token.Register(() =>
        {
            lock (sync)
            {
                if (waiter.Node?.List is { } list)
                {
                    list.Remove(waiter.Node);
                    waiter.Completion.TrySetCanceled(token);
                }
            }
        });
        return await waiter.Completion.Task;
    }

    private void Release()
    {
        lock (sync)
        {
            active--;
            // Serve one older conversational request after three delivery requests.
            var queue = delivery.Count > 0 && (conversation.Count == 0 || deliveryStreak < 3)
                ? delivery : conversation;
            if (queue.First is not { } node) { deliveryStreak = 0; return; }
            queue.RemoveFirst();
            deliveryStreak = queue == delivery ? deliveryStreak + 1 : 0;
            active++;
            node.Value.Completion.SetResult(new Permit(this));
        }
    }

    private sealed class Waiter
    {
        internal LinkedListNode<Waiter>? Node;
        internal TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Permit(ProviderInferenceGate gate) : IDisposable
    {
        private ProviderInferenceGate? owner = gate;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
