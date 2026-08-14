using CSweet.ExecutionGateway;
using CSweet.SatelliteOffice.Contracts.ControlPlane;
using Google.Protobuf;
using Grpc.Core;

namespace CSweet.UnitTests;

public sealed class GrpcWorkloadTunnelStreamTests
{
    [Fact]
    public async Task EmptyOpeningFrame_AllowsHeadquartersToSendBootConfigurationBeforeGuestWrites()
    {
        var nodeId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();
        var opening = new WorkloadTunnelFrame
        {
            SatelliteOfficeId = nodeId.ToString("D"),
            AssignmentId = assignmentId.ToString("D"),
            FencingEpoch = 2,
            SessionEpoch = 3,
            Sequence = 0,
            Content = ByteString.Empty,
            Completed = false
        };
        var requests = new EndOfStreamReader(opening);
        var responses = new RecordingServerStreamWriter();
        await using var tunnel = new GrpcWorkloadTunnelStream(
            requests,
            responses,
            opening,
            _ => Task.FromResult((nodeId, assignmentId)),
            nodeId,
            assignmentId,
            opening.FencingEpoch,
            opening.SessionEpoch,
            CancellationToken.None);

        var boot = new byte[] { 1, 2, 3, 4 };
        await tunnel.WriteAsync(boot);

        var response = Assert.Single(responses.Frames);
        Assert.Equal(0, response.Sequence);
        Assert.Equal(boot, response.Content.ToByteArray());
        Assert.False(response.Completed);
        Assert.Equal(0, await tunnel.ReadAsync(new byte[1]));
    }

    private sealed class EndOfStreamReader(WorkloadTunnelFrame current) : IAsyncStreamReader<WorkloadTunnelFrame>
    {
        public WorkloadTunnelFrame Current { get; } = current;
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class RecordingServerStreamWriter : IServerStreamWriter<WorkloadTunnelFrame>
    {
        public List<WorkloadTunnelFrame> Frames { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(WorkloadTunnelFrame message)
        {
            Frames.Add(message.Clone());
            return Task.CompletedTask;
        }
    }
}
