using System.IO.Pipes;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using Fixture = CSweet.UnitTests.ComputeHyperVExecutorTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ComputeGuestLifecycleTests
{
    [Fact]
    public async Task Lost_command_acknowledgment_survives_executor_restart_without_a_second_effect()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await fixture.Executor().ExecuteAsync(fixture.Packet, token);

        var marker = Path.Combine(fixture.Journal.Root, "guest-marker");
        var command = new ComputeGuestCommand(Guid.NewGuid(), "/usr/bin/touch", "/tmp", ["/tmp/guest-marker"], 5, 1024);
        var workload = new ComputeWorkload(command);
        var claim = fixture.Claim with
        {
            Action = InfrastructureActions.Execute,
            Generation = 2,
            DispatchId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            ResourceId = fixture.Runner.Id.ToString("D"),
            TemplateDigest = null,
            WorkloadDigest = ComputeProtocol.Digest(JsonSerializer.Serialize(workload, ComputeProtocol.Json)),
            Grants = [new(Guid.NewGuid(), 1, InfrastructureActions.Execute, fixture.Claim.ExpiresAt)]
        };
        var packet = new ComputeDispatchPacket(await fixture.Journal.Core.Signing.SignAsync(claim, token),
            fixture.Packet.Specification, null, workload);
        var pipeName = Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task RecordAndDisconnectAsync()
        {
            await server.WaitForConnectionAsync(token);
            var request = await ComputeGuestWire.ReadAsync<ComputeGuestWireRequest>(server, ComputeGuestWire.MaximumRequestBytes, token);
            Assert.Equal(command.RequestId, request.Command?.RequestId);
            await File.AppendAllLinesAsync(marker, [command.RequestId.ToString("D")], token);
            server.Disconnect();
        }
        async Task<Stream> ConnectAsync(Guid vmId, CancellationToken cancellation)
        {
            Assert.Equal(fixture.Runner.Id, vmId);
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await client.ConnectAsync(cancellation); return client; }
            catch { client.Dispose(); throw; }
        }
        await Task.WhenAll(RecordAndDisconnectAsync(), fixture.Executor(connectGuest: ConnectAsync).ExecuteAsync(packet, token));
        var restarted = fixture.Executor(connectGuest: (_, _) => throw new InvalidOperationException("Redelivery contacted the guest."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.ExecuteAsync(fixture.Packet, token));
        await restarted.ExecuteAsync(packet, token);
        var receipt = Assert.Single(await fixture.Journal.Journal().ListResultsAsync(100, token), x => x.Result.OperationId == claim.OperationId);
        Assert.Equal("outcome-unknown", receipt.Result.Workload?.ErrorCode);
        Assert.Equal(command.RequestId.ToString("D"), Assert.Single(await File.ReadAllLinesAsync(marker, token)));
        Assert.Single(fixture.Runner.Scripts, script => script == HyperVComputeDriver.CreateScript);
        Assert.Single(await fixture.Journal.Journal().ListReservationsAsync(null, 100, token));
    }

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
