using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CSweet.ExecutionGateway;
using CSweet.Infrastructure.Setup;
using CSweet.Domain.Setup;
using CSweet.Office.Contracts.ControlPlane;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class ExecutionWorkloadOrchestratorTests
{
    [Fact]
    public async Task GatewayRediscoversRetiredAttemptsEvenWhenLatestAttemptHasStopped()
    {
        await using var db = CreateDb();
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var other = Node(pool, Guid.NewGuid());
        var assignment = Assignment(pool.Id, node.Id, 1, 512);
        assignment.Status = ExecutionAssignmentStatus.Cancelled;
        assignment.FencingEpoch = 7;
        db.AddRange(pool, node, other, assignment);
        foreach (var epoch in new[] { 2L, 4L, 6L })
            db.Add(new ExecutionAssignmentAttempt { AssignmentId = assignment.Id, FencingEpoch = epoch,
                ExecutionNodeId = node.Id, ProviderId = assignment.ProviderId, AssignedAt = Now,
                StoppedAt = epoch == 4 ? null : Now });
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(Now);
        var gateway = new OfficeGatewayService(db, new ExecutionWorkloadOrchestrator(db, clock),
            null!, null!, null!, null!, clock, NullLogger<OfficeGatewayService>.Instance);
        var replies = new StopReplyStream();
        Assert.Equal(1, await gateway.ReconcileStopsAsync(node.Id, node.SessionEpoch, 0, replies, default));
        var hint = Assert.Single(replies.Messages).ReconcileAssignmentStop;
        Assert.Equal(4, hint.FencingEpoch);
        Assert.Equal(assignment.Id.ToString("D"), hint.AssignmentId);
        Assert.Null((await db.ExecutionAssignmentAttempts.SingleAsync(x => x.FencingEpoch == 4)).StoppedAt);
        var unauthorized = new StopReplyStream();
        await gateway.ReconcileStopsAsync(other.Id, other.SessionEpoch, 0, unauthorized, default);
        Assert.Empty(unauthorized.Messages);
        Assert.True(await new ExecutionWorkloadOrchestrator(db, clock).ReportStoppedAsync(node.Id,
            assignment.Id, 4, assignment.ProviderId, "", true));
        replies.Messages.Clear();
        await gateway.ReconcileStopsAsync(node.Id, node.SessionEpoch, 0, replies, default);
        Assert.Empty(replies.Messages);
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("missing-certificate")]
    [InlineData("stale-session")]
    [InlineData("revoked")]
    public async Task GatewayStopReceiptRequiresAuthenticatedCurrentOfficeSession(string scenario)
    {
        await using var db = CreateDb();
        using var key = ECDsa.Create();
        using var certificate = new CertificateRequest("CN=stop-test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        node.CertificateThumbprint = certificate.Thumbprint;
        node.CertificateSerialNumber = certificate.SerialNumber;
        node.CertificateExpiresAt = Now.AddYears(1);
        var assignment = Assignment(pool.Id, node.Id, 1, 512);
        assignment.StopEvidenceVersion = 1;
        assignment.Status = ExecutionAssignmentStatus.Cancelled;
        db.AddRange(pool, node, assignment, new ExecutionAssignmentAttempt
        {
            AssignmentId = assignment.Id, FencingEpoch = assignment.FencingEpoch,
            ExecutionNodeId = node.Id, ProviderId = assignment.ProviderId, AssignedAt = Now
        });
        if (scenario == "revoked") node.Status = ExecutionNodeStatus.Revoked;
        await db.SaveChangesAsync();
        var message = new OfficeControlMessage
        {
            OfficeId = node.Id.ToString("D"), ProtocolVersion = "1.0",
            SessionEpoch = node.SessionEpoch + (scenario == "stale-session" ? 1 : 0),
            AssignmentStopped = new AssignmentStopped
            {
                AssignmentId = assignment.Id.ToString("D"), FencingEpoch = assignment.FencingEpoch,
                ProviderId = assignment.ProviderId, ProviderInstanceId = "vm-1"
            }
        };
        var http = new DefaultHttpContext();
        if (scenario != "missing-certificate") http.Connection.ClientCertificate = certificate;
        var clock = new MutableTimeProvider(Now);
        var gateway = new OfficeGatewayService(db, new ExecutionWorkloadOrchestrator(db, clock),
            null!, null!, null!, null!, clock, NullLogger<OfficeGatewayService>.Instance);
        var replies = new StopReplyStream();
        var operation = gateway.Connect(new StopRequestStream(message), replies, new StopCallContext(http));
        if (scenario == "valid")
        {
            await operation;
            var receipt = Assert.Single(replies.Messages);
            Assert.True(receipt.AssignmentStopReceipt.Accepted);
            Assert.Equal(message.OfficeId, receipt.OfficeId);
            Assert.Equal(message.SessionEpoch, receipt.SessionEpoch);
            Assert.Equal(message.AssignmentStopped.FencingEpoch, receipt.AssignmentStopReceipt.FencingEpoch);
            Assert.NotNull((await db.ExecutionAssignmentAttempts.SingleAsync()).StoppedAt);
        }
        else
        {
            await Assert.ThrowsAsync<RpcException>(() => operation);
            Assert.Empty(replies.Messages);
            Assert.Null((await db.ExecutionAssignmentAttempts.SingleAsync()).StoppedAt);
        }
    }

    private sealed class StopRequestStream(OfficeControlMessage message) : IAsyncStreamReader<OfficeControlMessage>
    {
        private bool delivered;
        public OfficeControlMessage Current => message;
        public Task<bool> MoveNext(CancellationToken cancellationToken)
        { var available = !delivered; delivered = true; return Task.FromResult(available); }
    }

    private sealed class StopReplyStream : IServerStreamWriter<HeadquartersControlMessage>
    {
        public List<HeadquartersControlMessage> Messages { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(HeadquartersControlMessage message) { Messages.Add(message); return Task.CompletedTask; }
    }

    private sealed class StopCallContext : ServerCallContext
    {
        public StopCallContext(HttpContext http) => UserState["__HttpContext"] = http;
        protected override string MethodCore => "Connect";
        protected override string HostCore => "test";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore { get; } = [];
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new("test", new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
