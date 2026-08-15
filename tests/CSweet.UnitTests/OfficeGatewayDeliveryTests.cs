using CSweet.AgentBroker;
using CSweet.ExecutionGateway;
using CSweet.Domain.Setup;
using CSweet.Office.Contracts.Workloads;
using Grpc.Core;
using System.Text.Json;

namespace CSweet.UnitTests;

public sealed class OfficeGatewayDeliveryTests
{
    [Fact]
    public void RetryWithNewFencingEpochIsDeliveredEvenWhenAssignmentIdIsUnchanged()
    {
        var assignmentId = Guid.NewGuid();
        IReadOnlyDictionary<Guid, long> delivered = new Dictionary<Guid, long>
        {
            [assignmentId] = 1
        };

        Assert.False(OfficeGatewayService.ShouldDeliver(delivered, assignmentId, 1));
        Assert.True(OfficeGatewayService.ShouldDeliver(delivered, assignmentId, 2));
    }

    [Theory]
    [InlineData(ExecutionWorkloadKind.Builder)]
    [InlineData(ExecutionWorkloadKind.Runtime)]
    public void SignedAssignmentUsesWorkloadIdentifierFromExactSpecification(
        ExecutionWorkloadKind workloadKind)
    {
        var workloadId = Guid.NewGuid();
        var guest = new GuestImageReference(
            "guest", "1.0", "sha256:" + new string('a', 64), "linux", "x64");
        var limits = new WorkloadResourceLimits(
            1, 100, 512, 512, 100, 1024, TimeSpan.FromMinutes(1));
        var artifactDigest = "sha256:" + new string('b', 64);
        var lease = new BrokerChannelLease(
            Guid.NewGuid(), "1.0", "a-sufficiently-long-boot-token",
            guest.Digest, artifactDigest, DateTimeOffset.UtcNow.AddMinutes(1));
        WorkloadSpecification workload = workloadKind == ExecutionWorkloadKind.Builder
            ? new BuilderWorkloadSpecification(
                workloadId, guest, limits, lease,
                new RepositoryDescriptor(
                    "https://example.invalid/repository.git", new string('c', 40), false, "test-profile", "1.0"),
                1024 * 1024)
            : new RuntimeWorkloadSpecification(
                workloadId, guest, limits, lease,
                new AgentArtifactReference(artifactDigest, "signature", "1.0", "linux", "x64"),
                new RuntimeAgentIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid()),
                ["/app/agent"]);
        var json = JsonSerializer.Serialize(workload, workload.GetType());

        var resolved = OfficeGatewayService.ResolveAuthorizedWorkloadId(workloadKind, json);

        Assert.Equal(workloadId, resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not-json")]
    public void InvalidWorkloadSpecificationIsNeverEligibleForSigning(string specificationJson)
    {
        Assert.Throws<InvalidDataException>(() =>
            OfficeGatewayService.ResolveAuthorizedWorkloadId(
                ExecutionWorkloadKind.Builder, specificationJson));
    }

    [Fact]
    public void BrokerProtocolFailureIsActionableWithoutLeakingAStackTrace()
    {
        var result = OfficeGatewayService.BrokerTunnelFailure(
            new InvalidDataException("The runtime diagnostic stream is invalid."));

        Assert.Equal(StatusCode.FailedPrecondition, result.StatusCode);
        Assert.Contains("broker protocol was rejected", result.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime diagnostic stream", result.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.IO", result.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UnexpectedBrokerFailureDoesNotExposeSensitiveExceptionText()
    {
        var result = OfficeGatewayService.BrokerTunnelFailure(
            new InvalidOperationException("password=do-not-leak"));

        Assert.Equal(StatusCode.FailedPrecondition, result.StatusCode);
        Assert.DoesNotContain("do-not-leak", result.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void GuestExitDoesNotPutGuestControlledLogsInGrpcStatus()
    {
        var result = OfficeGatewayService.BrokerTunnelFailure(
            new GuestWorkloadExitedException(17, "startup-failed", "token=do-not-leak"));

        Assert.Equal(StatusCode.FailedPrecondition, result.StatusCode);
        Assert.Contains("startup-failed", result.Status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-leak", result.Status.Detail, StringComparison.Ordinal);
    }
}
