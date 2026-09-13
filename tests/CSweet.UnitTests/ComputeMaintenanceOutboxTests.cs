using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeMaintenanceOutboxTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_fence_and_attributed_audit_request_are_durable_before_cleanup(bool failure)
    {
        await using var f = new Fixture(); await f.InitializeAsync(persistent: true);
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        var cleanup = f.Journal.Journal().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, async (authority, _) =>
        {
            // Inspect the committed snapshot while the callback owns the separate physical-operation lock.
            using var envelope = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(f.Journal.Root, "journal.json")));
            using var state = JsonDocument.Parse(envelope.RootElement.GetProperty("stateJson").GetString()!);
            var rows = state.RootElement.GetProperty("maintenanceOutbox").EnumerateObject().ToArray();
            var requested = JsonSerializer.Deserialize<ComputeMaintenanceEvent>(Assert.Single(rows).Value.GetProperty("eventJson").GetString()!, ComputeProtocol.Json)!;
            Assert.Equal(ComputeMaintenancePhase.Requested, requested.Phase);
            Assert.Equal(f.Claim.OrganizationId, requested.OrganizationId);
            Assert.Equal(f.Claim.InstallationId, requested.InstallationId);
            Assert.Equal(f.Claim.OperationId, requested.ProvisionOperationId);
            Assert.Equal(f.Claim.Grants, requested.ProvisionGrants);
            Assert.Equal(InfrastructureActions.Stop, requested.Action);
            Assert.False(requested.PhysicalConfirmed);
            if (failure) throw new IOException("Sensitive provider detail must not enter evidence.");
            return new ComputePhysicalOutcome<bool>(true, authority.ResourceId);
        }, default);
        if (failure) await Assert.ThrowsAsync<IOException>(() => cleanup); else Assert.True(await cleanup);
        var pending = await f.Journal.Journal().ListMaintenanceEventsAsync(100, default);
        Assert.Equal(2, pending.Count);
        var events = pending.Select(x => JsonSerializer.Deserialize<ComputeMaintenanceEvent>(x.EventJson, ComputeProtocol.Json)!).ToArray();
        var outcome = Assert.Single(events, x => x.Phase != ComputeMaintenancePhase.Requested);
        Assert.Equal(failure ? ComputeMaintenancePhase.Failed : ComputeMaintenancePhase.Observed, outcome.Phase);
        Assert.Equal(!failure, outcome.PhysicalConfirmed);
        Assert.Equal(failure ? "lease-cleanup-uncertain" : null, outcome.FailureCode);
        Assert.All(pending, x => { Assert.Equal(ComputeProtocol.Digest(x.EventJson), x.Digest); Assert.DoesNotContain("Sensitive", x.EventJson); });
        Assert.True(Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default)).LeaseExpired);
    }

    [Fact]
    public async Task Lost_delivery_acknowledgement_replays_identical_evidence_after_restart()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default);
        var accepted = new Dictionary<Guid, string>(); var deliveries = 0;
        await Assert.ThrowsAsync<IOException>(() => new ComputeMaintenanceOutboxDispatcher(f.Journal.Journal()).DispatchAsync((row, _) =>
        {
            accepted.Add(row.Id, row.EventJson); deliveries++;
            throw new IOException("Receiver committed, but acknowledgement was lost.");
        }, default));
        Assert.Equal(2, (await f.Journal.Journal().ListMaintenanceEventsAsync(100, default)).Count);
        Assert.Equal(2, await new ComputeMaintenanceOutboxDispatcher(f.Journal.Journal()).DispatchAsync(async (row, token) =>
        {
            deliveries++;
            if (accepted.TryGetValue(row.Id, out var previous)) Assert.Equal(previous, row.EventJson);
            else accepted.Add(row.Id, row.EventJson);
            // Delivery holds no journal lock, so unrelated protected reads remain possible.
            Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, token));
        }, default));
        Assert.Equal(3, deliveries); Assert.Equal(2, accepted.Count);
        Assert.Empty(await f.Journal.Journal().ListMaintenanceEventsAsync(100, default));
    }

    [Fact]
    public async Task Acknowledgement_requires_the_exact_queued_digest_and_does_not_remove_reservations()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Executor().ExecuteAsync(f.Packet, default);
        f.Journal.Core.Time.Now = f.Claim.EnvironmentLeaseExpiresAt.AddSeconds(1);
        await f.Executor().EnforceExpiredLeaseAsync(f.Claim.EnvironmentId, default);
        var row = Assert.Single(await f.Journal.Journal().ListMaintenanceEventsAsync(1, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal.Journal().AcknowledgeMaintenanceEventAsync(row.Id, "changed", default));
        Assert.Equal(2, (await f.Journal.Journal().ListMaintenanceEventsAsync(100, default)).Count);
        Assert.True(await f.Journal.Journal().AcknowledgeMaintenanceEventAsync(row.Id, row.Digest, default));
        Assert.False(await f.Journal.Journal().AcknowledgeMaintenanceEventAsync(row.Id, row.Digest, default));
        Assert.Single(await f.Journal.Journal().ListMaintenanceEventsAsync(100, default));
        Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Journal.Journal().ListMaintenanceEventsAsync(101, default));
    }
}
