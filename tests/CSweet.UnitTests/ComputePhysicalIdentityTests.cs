using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeReplayJournalTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputePhysicalIdentityTests
{
    [Fact]
    public async Task Restart_returns_recorded_identity_and_never_rebinds_to_another_resource()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var dispatch = f.Verifier.Verify(f.Packet);
        var resource = Guid.NewGuid().ToString("D");
        Assert.Equal(7, await f.Journal().RunPhysicalAsync(dispatch, (decision, known, _) =>
        {
            Assert.Equal(ComputeJournalDecision.Execute, decision); Assert.Null(known);
            return Task.FromResult(new ComputePhysicalOutcome<int>(7, resource));
        }, default));
        Assert.Equal(resource, Assert.Single(await f.Journal().ListReservationsAsync(null, 100, default)).ResourceId);
        Assert.Equal(8, await f.Journal().RunPhysicalAsync(dispatch, (decision, known, _) =>
        {
            Assert.Equal(ComputeJournalDecision.Observe, decision); Assert.Equal(resource, known);
            return Task.FromResult(new ComputePhysicalOutcome<int>(8, resource));
        }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Journal().RunPhysicalAsync(dispatch,
            (_, _, _) => Task.FromResult(new ComputePhysicalOutcome<int>(9, "different-vm")), default));
        Assert.Equal(resource, Assert.Single(await f.Journal().ListReservationsAsync(null, 100, default)).ResourceId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Signed_control_requires_the_matching_recorded_resource_identity(bool known)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var original = f.Core.Signing.Claims[0];
        await f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet),
            (_, _, _) => Task.FromResult(new ComputePhysicalOutcome<bool>(true, known ? "owned-vm" : null)), default);
        var start = original with { Generation = 2, OperationId = Guid.NewGuid(), DispatchId = Guid.NewGuid(),
            Action = InfrastructureActions.Start, TemplateDigest = null, ResourceId = "another-vm",
            Grants = [new(Guid.NewGuid(), 1, InfrastructureActions.Start, original.ExpiresAt)] };
        var packet = f.Packet with { Authorization = await f.Core.Signing.SignAsync(start, default), Template = null };
        var effects = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Journal().RunPhysicalAsync(f.Verifier.Verify(packet),
            (_, _, _) => { effects++; return Task.FromResult(new ComputePhysicalOutcome<bool>(true, "another-vm")); }, default));
        Assert.Equal(0, effects);
        var reservation = Assert.Single(await f.Journal().ListReservationsAsync(null, 100, default));
        Assert.Equal(known ? "owned-vm" : null, reservation.ResourceId);
        Assert.Equal(1, reservation.Generation);
    }

    [Fact]
    public async Task Cancellation_after_completed_effect_does_not_discard_known_identity()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        await f.Journal().RunPhysicalAsync(f.Verifier.Verify(f.Packet), (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ComputePhysicalOutcome<bool>(true, "completed-vm"));
        }, cancellation.Token);
        Assert.Equal("completed-vm", Assert.Single(await f.Journal().ListReservationsAsync(null, 100, default)).ResourceId);
    }
}
