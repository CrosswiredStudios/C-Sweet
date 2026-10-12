using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class ProviderInferenceGateTests
{
    [Fact]
    public async Task Increasing_capacity_admits_waiters_in_priority_order_without_releasing_active_requests()
    {
        var gate = new ProviderInferenceGate(1, 10);
        using var active = await gate.AcquireAsync(false, default);
        var chat = gate.AcquireAsync(false, default);
        var delivery = Enumerable.Range(0, 4).Select(_ => gate.AcquireAsync(true, default)).ToArray();
        gate.UpdateCapacity(4);
        using var first = await delivery[0].WaitAsync(TimeSpan.FromSeconds(3));
        using var second = await delivery[1].WaitAsync(TimeSpan.FromSeconds(3));
        using var third = await delivery[2].WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(chat.IsCompleted);
        Assert.False(delivery[3].IsCompleted);
        first.Dispose();
        using var conversation = await chat.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(delivery[3].IsCompleted);
        second.Dispose();
        using var last = await delivery[3].WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Lowering_capacity_drains_active_requests_before_admitting_another_waiter()
    {
        var gate = new ProviderInferenceGate(4, 10);
        var active = new List<IDisposable>();
        for (var i = 0; i < 4; i++) active.Add(await gate.AcquireAsync(false, default));
        var waiting = gate.AcquireAsync(false, default);
        gate.UpdateCapacity(1);
        foreach (var permit in active.Take(3))
        {
            permit.Dispose();
            Assert.False(waiting.IsCompleted);
        }
        active[3].Dispose();
        using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.UpdateCapacity(0));
    }

    [Fact]
    public async Task Delivery_overtakes_chat_without_preemption_or_starvation()
    {
        var gate = new ProviderInferenceGate(1, 10);
        using var active = await gate.AcquireAsync(false, default);
        var chat = gate.AcquireAsync(false, default);
        var delivery = Enumerable.Range(0, 4).Select(_ => gate.AcquireAsync(true, default)).ToArray();
        Assert.All(delivery, x => Assert.False(x.IsCompleted));
        active.Dispose();
        for (var i = 0; i < 3; i++)
        {
            using var permit = await delivery[i].WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(chat.IsCompleted);
        }
        using (await chat.WaitAsync(TimeSpan.FromSeconds(3))) Assert.False(delivery[3].IsCompleted);
        using var last = await delivery[3].WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Cancelled_waiter_releases_queue_space_and_double_disposal_cannot_add_capacity()
    {
        var gate = new ProviderInferenceGate(1, 1);
        using var active = await gate.AcquireAsync(false, default);
        using var cancel = new CancellationTokenSource();
        var cancelled = gate.AcquireAsync(true, cancel.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.AcquireAsync(false, default));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var next = gate.AcquireAsync(false, default);
        active.Dispose(); active.Dispose();
        using var held = await next.WaitAsync(TimeSpan.FromSeconds(3));
        var waiting = gate.AcquireAsync(true, default);
        Assert.False(waiting.IsCompleted);
        held.Dispose();
        using var final = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
