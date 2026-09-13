using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeDispatchIntakeTests
{
    [Fact]
    public async Task Discovery_claim_execution_and_replay_preserve_one_physical_machine()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var intake = new ComputeDispatchIntake(f.Verifier); var claims = 0;
        Task<ComputeProviderWorkPage> Discover(Guid? cursor, CancellationToken token) =>
            Task.FromResult(new ComputeProviderWorkPage([f.Claim.OperationId], null));
        Task<ComputeDispatchPacket?> Claim(Guid operationId, CancellationToken token)
        { Assert.Equal(f.Claim.OperationId, operationId); claims++; return Task.FromResult<ComputeDispatchPacket?>(f.Packet); }
        async Task Execute(ComputeDispatchPacket packet, CancellationToken token) => await f.Executor().ExecuteAsync(packet, token);
        for (var i = 0; i < 2; i++)
        {
            var pass = await intake.RunOnceAsync(null, Discover, Claim, Execute, default);
            Assert.Equal(new ComputeDispatchIntakePass(1, 1, 0, null), pass);
        }
        Assert.Equal(2, claims); Assert.Single(f.Runner.Scripts, x => x == HyperVComputeDriver.CreateScript);
        Assert.Equal(InfrastructureActions.Start, Assert.Single(f.Runner.Actions));
        Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
    }

    [Fact]
    public async Task Discovered_but_unclaimed_work_cannot_execute()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var pass = await new ComputeDispatchIntake(f.Verifier).RunOnceAsync(null,
            (_, _) => Task.FromResult(new ComputeProviderWorkPage([f.Claim.OperationId], null)),
            (_, _) => Task.FromResult<ComputeDispatchPacket?>(null), (_, _) => throw new Exception("Must not execute"), default);
        Assert.Equal(new ComputeDispatchIntakePass(1, 0, 1, null), pass);
    }

    [Theory]
    [InlineData("wrong-operation")]
    [InlineData("bad-signature")]
    [InlineData("oversized-page")]
    [InlineData("bad-cursor")]
    public async Task Invalid_discovery_or_claim_never_reaches_physical_execution(string scenario)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var ids = scenario == "oversized-page" ? Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).Order().ToArray()
            : new[] { scenario == "wrong-operation" ? Guid.NewGuid() : f.Claim.OperationId };
        var packet = scenario == "bad-signature" ? f.Packet with { Authorization = f.Packet.Authorization with { SignatureBase64 = "bad" } } : f.Packet;
        var executed = false;
        Task Run() => new ComputeDispatchIntake(f.Verifier).RunOnceAsync(null,
            (_, _) => Task.FromResult(new ComputeProviderWorkPage(ids, scenario == "bad-cursor" ? ids[^1] : null)),
            (_, _) => Task.FromResult<ComputeDispatchPacket?>(packet), (_, _) => { executed = true; return Task.CompletedTask; }, default);
        if (scenario is "oversized-page" or "bad-cursor") await Assert.ThrowsAsync<InvalidDataException>(Run);
        else await Assert.ThrowsAsync<UnauthorizedAccessException>(Run);
        Assert.False(executed);
    }
}
