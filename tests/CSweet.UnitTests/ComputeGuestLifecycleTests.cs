using System.IO.Pipes;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeGuestLifecycleTests
{
    [Theory]
    [InlineData("ready", ComputeLifecycleState.Ready)]
    [InlineData("starting", ComputeLifecycleState.Bootstrapping)]
    [InlineData("stopped-during-probe", ComputeLifecycleState.Failed)]
    public async Task Lifecycle_readiness_requires_matching_guest_and_running_owned_vm(string scenario, ComputeLifecycleState expected)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var name = "csweet-lifecycle-" + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        async Task Respond()
        {
            var request = await ComputeGuestReadiness.ReadAsync<ComputeGuestReadinessRequest>(server, timeout.Token);
            if (scenario == "stopped-during-probe") f.Runner.PhysicalState = "Off";
            await ComputeGuestReadiness.WriteAsync(server, new ComputeGuestReadinessResponse(1, request.Challenge,
                "linux", "x64", scenario != "starting"), timeout.Token);
        }
        var responding = Respond(); var connections = 0;
        try
        {
            await f.Executor(connectGuest: (id, token) =>
            { Assert.Equal(f.Runner.Id, id); connections++; return Task.FromResult<Stream>(client); }).ExecuteAsync(f.Packet, timeout.Token);
        }
        finally { await responding; }
        Assert.Equal(1, connections);
        var row = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default));
        Assert.Equal(expected, row.Result.State); Assert.Equal(f.Runner.Id.ToString("D"), row.Result.ResourceId);
        Assert.False(row.Result.TeardownConfirmed);
        Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
    }

    [Fact]
    public async Task Unreachable_guest_preserves_bootstrapping_and_physical_identity()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Executor(connectGuest: (_, _) => throw new IOException("Guest unavailable")).ExecuteAsync(f.Packet, default);
        var row = Assert.Single(await f.Journal.Journal().ListResultsAsync(100, default));
        Assert.Equal(ComputeLifecycleState.Bootstrapping, row.Result.State);
        Assert.Equal(f.Runner.Id.ToString("D"), row.Result.ResourceId);
    }
}
