using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using CSweet.Domain.Compute;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeProvisionActivationTests
{
    [Fact]
    public async Task Lost_activation_response_recovers_running_identity_without_repeating_start()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        f.Runner.LoseStartResponseAfterEffect = true;
        await f.Executor().ExecuteAsync(f.Packet, default);
        Assert.Equal("Running", f.Runner.PhysicalState);
        Assert.Equal(ComputeLifecycleState.Failed, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)).Result.State);
        Assert.Equal(f.Runner.Id.ToString("D"), Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default)).ResourceId);
        var recovered = await f.Executor().ExecuteAsync(f.Packet, default);
        Assert.Equal(f.Runner.Id, recovered.Id); Assert.Equal("Running", recovered.State);
        Assert.Equal(InfrastructureActions.Start, Assert.Single(f.Runner.Actions));
        Assert.Single(f.Runner.Scripts, x => x == HyperVComputeDriver.CreateScript);
        Assert.Contains(HyperVComputeDriver.DiscoverScript, f.Runner.Scripts);
        var result = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)).Result;
        Assert.Equal(ComputeLifecycleState.Bootstrapping, result.State);
        Assert.Equal(f.Runner.Id.ToString("D"), result.ResourceId);
        Assert.False(result.TeardownConfirmed);
    }

    [Fact]
    public async Task Dispatch_expiring_during_creation_cannot_activate_and_fresh_observation_only_recovers_identity()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        f.Runner.AfterCreate = () => f.Journal.Core.Time.Now = f.Claim.ExpiresAt;
        await f.Executor().ExecuteAsync(f.Packet, default);
        Assert.True(f.Runner.Exists); Assert.Equal("Off", f.Runner.PhysicalState);
        Assert.Empty(f.Runner.Actions);
        Assert.Equal(ComputeLifecycleState.Failed, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)).Result.State);
        var now = f.Journal.Core.Time.GetUtcNow();
        var claim = f.Claim with { DispatchId = Guid.NewGuid(), Mode = ComputeDispatchMode.Observe,
            IssuedAt = now, ExpiresAt = now.AddMinutes(1), Grants = [], TemplateDigest = null };
        var packet = f.Packet with { Authorization = await f.Journal.Core.Signing.SignAsync(claim, default), Template = null };
        var recovered = await f.Executor().ExecuteAsync(packet, default);
        Assert.Equal(f.Runner.Id, recovered.Id); Assert.Equal("Off", recovered.State);
        Assert.Empty(f.Runner.Actions);
        Assert.Single(f.Runner.Scripts, x => x == HyperVComputeDriver.CreateScript);
        Assert.Equal(f.Runner.Id.ToString("D"), Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default)).ResourceId);
        Assert.Equal(ComputeLifecycleState.Failed, Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default)).Result.State);
    }
}
