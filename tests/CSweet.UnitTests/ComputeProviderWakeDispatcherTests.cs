using CSweet.Application.Compute;
using CSweet.Infrastructure.Compute;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeProviderWakeDispatcherTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Publisher : IComputeProviderWakePublisher
    {
        public bool Available { get; set; }
        public bool Fail { get; set; }
        public List<(Guid Organization, Guid Node, string Provider, Guid Event)> Calls { get; } = [];
        public Task<bool> PublishAsync(Guid organizationId, Guid nodeId, string providerId, Guid eventId, CancellationToken token)
        {
            Calls.Add((organizationId, nodeId, providerId, eventId));
            if (Fail) throw new IOException("Disconnected");
            return Task.FromResult(Available);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_recipient_or_transport_retries_stable_scoped_hint_without_mutating_work(bool fails)
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync(); await f.Send();
        var row = await f.Db.ComputeProviderWakes.SingleAsync();
        var clock = new Clock { Now = row.CreatedAt }; var publisher = new Publisher { Fail = fails };
        var dispatcher = new ComputeProviderWakeDispatcher(f.Db, publisher, clock);
        Assert.Equal(new ComputeProviderWakeDispatchPass(1, 0, 1), await dispatcher.DispatchAsync(default));
        Assert.Null(row.PublishedAt); Assert.Equal(1, row.Attempts); Assert.True(row.NextAttemptAt > clock.Now);
        Assert.Equal(new ComputeProviderWakeDispatchPass(0, 0, 0), await dispatcher.DispatchAsync(default));
        publisher.Fail = false; publisher.Available = true; clock.Now = row.NextAttemptAt;
        Assert.Equal(new ComputeProviderWakeDispatchPass(1, 1, 0), await dispatcher.DispatchAsync(default));
        Assert.Equal(clock.Now, row.PublishedAt); Assert.Equal(2, publisher.Calls.Count);
        Assert.All(publisher.Calls, call => Assert.Equal((row.OrganizationId, row.NodeId, row.ProviderId, row.Id), call));
        Assert.Equal(new ComputeProviderWakeDispatchPass(0, 0, 0), await dispatcher.DispatchAsync(default));
        var operation = await f.Db.ComputeOperations.SingleAsync();
        Assert.Equal("Pending", operation.Status); Assert.Equal(0, operation.Attempts); Assert.Null(operation.DispatchLeaseId);
        Assert.True((await f.Db.ComputeEnvironments.SingleAsync()).HoldsReservation);
    }
}
