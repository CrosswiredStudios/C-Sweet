using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using Fixture = CSweet.UnitTests.ComputeReplayJournalTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeResultDispatcherTests
{
    [Fact]
    public async Task Lost_acknowledgement_retains_identical_evidence_and_delivery_holds_no_physical_lock()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await Observe(f);
        var original = Assert.Single(await f.Journal().ListResultsAsync(100, default));
        await Assert.ThrowsAsync<IOException>(() => new ComputeResultOutboxDispatcher(f.Journal(), f.Core.Time)
            .DispatchAsync(null, (_, _) => throw new IOException("Lost response."), default));
        var pass = await new ComputeResultOutboxDispatcher(f.Journal(), f.Core.Time).DispatchAsync(null, async (row, token) =>
        {
            Assert.Equal(original, row);
            Assert.Single(await f.Journal().ListReservationsAsync(null, 100, token));
            return Ack(row);
        }, default);
        Assert.Equal(1, pass.Acknowledged); Assert.Empty(pass.RequiresObservation);
        Assert.Empty(await f.Journal().ListResultsAsync(100, default));
    }

    [Fact]
    public async Task Expired_result_remains_unchanged_and_requests_observation_without_delivery()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await Observe(f);
        var original = Assert.Single(await f.Journal().ListResultsAsync(100, default));
        f.Core.Time.Now = original.Result.ExpiresAt;
        var pass = await new ComputeResultOutboxDispatcher(f.Journal(), f.Core.Time).DispatchAsync(null,
            (_, _) => throw new InvalidOperationException("Expired evidence must not be delivered."), default);
        Assert.Equal(0, pass.Acknowledged); Assert.Equal(original, Assert.Single(pass.RequiresObservation));
        Assert.Equal(original, Assert.Single(await f.Journal().ListResultsAsync(100, default)));
        Assert.Empty(await f.Journal().ListResultsAsync(original.Result.OperationId, 100, default));
    }

    [Fact]
    public async Task Observation_during_delivery_survives_old_acknowledgement()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await Observe(f);
        var pass = await new ComputeResultOutboxDispatcher(f.Journal(), f.Core.Time).DispatchAsync(null, async (row, _) =>
        {
            await Observe(f);
            return Ack(row);
        }, default);
        Assert.Equal(0, pass.Acknowledged);
        Assert.Equal(2, Assert.Single(await f.Journal().ListResultsAsync(100, default)).Result.Sequence);
    }

    [Fact]
    public async Task Acknowledgement_for_another_operation_cannot_remove_queued_evidence()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await Observe(f);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ComputeResultOutboxDispatcher(f.Journal(), f.Core.Time)
            .DispatchAsync(null, (row, _) => Task.FromResult(Ack(row) with { OperationId = Guid.NewGuid() }), default));
        Assert.Single(await f.Journal().ListResultsAsync(100, default));
    }

    [Fact]
    public async Task Cursor_passes_a_full_page_of_expired_results_to_deliver_fresh_evidence()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await Observe(f);
        var original = f.Core.Signing.Claims[0];
        async Task Stage(Guid operationId, long generation)
        {
            var now = f.Core.Time.GetUtcNow();
            var claim = original with { DispatchId = Guid.NewGuid(), OperationId = operationId, Generation = generation,
                Action = InfrastructureActions.Start, Mode = ComputeDispatchMode.Observe, TemplateDigest = null, ResourceId = "vm-1",
                IssuedAt = now, ExpiresAt = now.AddMinutes(1),
                Grants = [] };
            var packet = f.Packet with { Authorization = await f.Core.Signing.SignAsync(claim, default), Template = null };
            await f.Journal().RunPhysicalAsync(f.Verifier.Verify(packet), (_, _, _) =>
                Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Bootstrapping))), default);
        }
        for (var i = 1; i < 100; i++) await Stage(Guid.Parse($"00000000-0000-0000-0000-{i:D12}"), i + 1);
        f.Core.Time.Now = f.Core.Time.GetUtcNow().AddMinutes(3);
        var freshId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        await Stage(freshId, 101);
        var dispatcher = new ComputeResultOutboxDispatcher(f.Journal(), f.Core.Time);
        var first = await dispatcher.DispatchAsync(null, (_, _) => throw new InvalidOperationException("First page is expired."), default);
        Assert.Equal(100, first.RequiresObservation.Count); Assert.NotNull(first.NextAfterOperationId);
        var second = await dispatcher.DispatchAsync(first.NextAfterOperationId, (row, _) =>
        {
            Assert.Equal(freshId, row.Result.OperationId); return Task.FromResult(Ack(row));
        }, default);
        Assert.Equal(1, second.Acknowledged); Assert.Empty(second.RequiresObservation); Assert.Null(second.NextAfterOperationId);
    }
    private static Task<bool> Observe(Fixture f) => f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (_, _, _) =>
        Task.FromResult(new ComputePhysicalOutcome<bool>(true, "vm-1", new(ComputeLifecycleState.Provisioning))), default);
    private static ComputeResultAcknowledgement Ack(ComputeResultOutboxEntry row) => new(row.Result.OperationId, row.Result.Sequence, row.Digest, false);
}
