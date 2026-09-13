using CSweet.Api.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeProviderWakeChannelsTests
{
    [Fact]
    public async Task Publication_reaches_only_exact_scope_and_offline_recipients_are_not_acknowledged()
    {
        var channels = new ComputeProviderWakeChannels(); var org = Guid.NewGuid(); var node = Guid.NewGuid(); var id = Guid.NewGuid();
        Assert.False(await channels.PublishAsync(org, node, "provider", id, default));
        using var subscription = channels.Subscribe(org, node, "provider");
        Assert.False(await channels.PublishAsync(Guid.NewGuid(), node, "provider", id, default));
        Assert.False(await channels.PublishAsync(org, Guid.NewGuid(), "provider", id, default));
        Assert.False(await channels.PublishAsync(org, node, "another-provider", id, default));
        Assert.True(await channels.PublishAsync(org, node, "provider", id, default));
        Assert.Equal(id, await subscription.ReadAsync(default));
        Assert.False(await channels.PublishAsync(org, node, "provider", id, default));
    }

    [Fact]
    public async Task Subscription_limit_and_disposal_release_capacity_and_cancel_pending_reads()
    {
        var channels = new ComputeProviderWakeChannels(); var org = Guid.NewGuid(); var node = Guid.NewGuid();
        using var first = channels.Subscribe(org, node, "provider");
        using var second = channels.Subscribe(org, node, "provider");
        Assert.Throws<InvalidOperationException>(() => channels.Subscribe(org, node, "provider"));
        var read = first.ReadAsync(default); first.Dispose(); first.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        using var replacement = channels.Subscribe(org, node, "provider");
        var id = Guid.NewGuid(); Assert.True(await channels.PublishAsync(org, node, "provider", id, default));
        Assert.Equal(id, await second.ReadAsync(default)); Assert.Equal(id, await replacement.ReadAsync(default));
    }
}
