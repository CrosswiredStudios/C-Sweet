using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using FleetExecutionNode = CSweet.Domain.Setup.ExecutionNode;

namespace CSweet.UnitTests;

public sealed class ExecutionWorkloadOrchestratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SchedulerUsesDeterministicDominantResourcePlacement()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        var pool = Pool();
        var busier = Node(pool, Guid.Parse("10000000-0000-0000-0000-000000000000"));
        var quieter = Node(pool, Guid.Parse("20000000-0000-0000-0000-000000000000"));
        db.AddRange(pool, busier, quieter);
        db.ExecutionWorkloadAssignments.Add(Assignment(pool.Id, busier.Id, 3, 3072));
        var buildId = Guid.NewGuid();
        db.AgentBuildJobs.Add(new AgentBuildJob { Id = buildId, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);
        var queued = await scheduler.SubmitAsync(Request(buildId, pool.Id));

        Assert.Equal(1, await scheduler.AssignPendingAsync());

        var assignment = await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == queued.AssignmentId);
        Assert.Equal(quieter.Id, assignment.ExecutionNodeId);
        Assert.Equal(ExecutionAssignmentStatus.Assigned, assignment.Status);
    }

    [Fact]
    public async Task DevelopmentPostureRequiresTwoSidedOptInAndCertifiedProvider()
    {
        await using var db = CreateDb();
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        node.LabelsJson = """
            {"csweet.security.profile":"development","csweet.security.development-assignments":"true"}
            """;
        var firstBuild = Guid.NewGuid();
        var secondBuild = Guid.NewGuid();
        db.AddRange(pool, node,
            new AgentBuildJob { Id = firstBuild, PackageVersionId = Guid.NewGuid() },
            new AgentBuildJob { Id = secondBuild, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, new MutableTimeProvider(Now));

        await scheduler.SubmitAsync(Request(firstBuild, pool.Id));
        Assert.Equal(0, await scheduler.AssignPendingAsync());

        await scheduler.SubmitAsync(Request(secondBuild, pool.Id) with { AllowDevelopmentSecurityPosture = true });
        Assert.Equal(1, await scheduler.AssignPendingAsync());
        var assigned = await db.ExecutionWorkloadAssignments.SingleAsync(x => x.AgentBuildJobId == secondBuild);
        Assert.Equal(node.Id, assigned.ExecutionNodeId);
        Assert.Contains("\"allowDevelopmentSecurityPosture\":true", assigned.SpecificationJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmissionUsesTheSharedCanonicalAssignmentDigest()
    {
        await using var db = CreateDb();
        var pool = Pool();
        var buildId = Guid.NewGuid();
        db.AddRange(pool, new AgentBuildJob { Id = buildId, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, new MutableTimeProvider(Now));

        var reference = await scheduler.SubmitAsync(Request(buildId, pool.Id));

        var assignment = await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == reference.AssignmentId);
        Assert.Equal(
            CSweet.Office.Contracts.Security.AssignmentEnvelope.Digest(assignment.SpecificationJson),
            assignment.SpecificationDigest);
    }

    [Fact]
    public async Task DispatchHashesTheExactPersistedJsonRepresentationSentToTheNode()
    {
        await using var db = CreateDb();
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var specification = "{\"kind\":\"builder\"}";
        var canonical = CSweet.Office.Contracts.Security.AssignmentEnvelope.Digest(specification);
        var assignment = Assignment(pool.Id, node.Id, 1, 512);
        assignment.SpecificationJson = specification;
        assignment.SpecificationDigest = canonical;
        db.AddRange(pool, node, assignment);
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, new MutableTimeProvider(Now));

        var lease = Assert.Single(await scheduler.GetNodeAssignmentsAsync(node.Id, node.SessionEpoch));

        Assert.Equal(canonical, lease.SpecificationDigest);
        Assert.Equal(canonical, assignment.SpecificationDigest);
        assignment.SpecificationJson = "{ \"kind\": \"builder\" }";
        await db.SaveChangesAsync();
        var currentLease = Assert.Single(await scheduler.GetNodeAssignmentsAsync(node.Id, node.SessionEpoch));
        Assert.Equal(
            CSweet.Office.Contracts.Security.AssignmentEnvelope.Digest(assignment.SpecificationJson),
            currentLease.SpecificationDigest);
        Assert.NotEqual(canonical, currentLease.SpecificationDigest);
    }

    [Fact]
    public void BuildProgressExposesDispatchAndRetryState()
    {
        var assignment = new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(),
            Status = ExecutionAssignmentStatus.Pending,
            Attempt = 2,
            FailureCode = "assignment-lease-expired",
            SanitizedFailure = "The Office did not renew its lease."
        };

        var pending = FleetAgentBuildExecutor.ProgressState(assignment);

        Assert.Contains("Retry attempt 2", pending.Detail, StringComparison.Ordinal);
        Assert.Contains("did not renew", pending.Detail, StringComparison.Ordinal);
        assignment.Status = ExecutionAssignmentStatus.Assigned;
        assignment.ExecutionNodeId = Guid.NewGuid();
        assignment.ProviderId = "hyperv-gen2";
        assignment.ExecutionNode = new FleetExecutionNode
        {
            Id = assignment.ExecutionNodeId.Value,
            MachineName = "satellite-1",
            NodeVersion = "1.0.2"
        };

        var dispatched = FleetAgentBuildExecutor.ProgressState(assignment);

        Assert.Contains("satellite-1 (version 1.0.2)", dispatched.Detail, StringComparison.Ordinal);
        Assert.Contains("accept the signed assignment", dispatched.Detail, StringComparison.Ordinal);

        assignment.Status = ExecutionAssignmentStatus.Running;
        var running = FleetAgentBuildExecutor.ProgressState(assignment);

        Assert.False(running.Succeeded);
        Assert.Contains("builder VM started", running.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("waiting for its authenticated guest channel", running.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredLeaseIncrementsEpochAndRejectsLateResult()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var buildId = Guid.NewGuid();
        db.AddRange(pool, node, new AgentBuildJob { Id = buildId, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);
        var reference = await scheduler.SubmitAsync(Request(buildId, pool.Id));
        await scheduler.AssignPendingAsync();
        var assigned = await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == reference.AssignmentId);
        var oldEpoch = assigned.FencingEpoch;
        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(1, await scheduler.FenceExpiredAsync());
        Assert.False(await scheduler.ReportStatusAsync(node.Id, assigned.Id, oldEpoch,
            ExecutionAssignmentStatus.Completed, null, null, null));
        Assert.Equal(oldEpoch + 1, assigned.FencingEpoch);
        Assert.Equal(ExecutionAssignmentStatus.Pending, assigned.Status);
    }

    [Fact]
    public async Task ThirdUnacknowledgedDeliveryFailsWithActionableNodeDiagnostic()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var assignment = Assignment(pool.Id, node.Id, 1, 512);
        assignment.Status = ExecutionAssignmentStatus.Assigned;
        assignment.Attempt = 3;
        assignment.LeaseExpiresAt = Now.AddSeconds(30);
        db.AddRange(pool, node, assignment);
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(1, await scheduler.FenceExpiredAsync());

        Assert.Equal(ExecutionAssignmentStatus.Failed, assignment.Status);
        Assert.Equal("assignment-not-acknowledged", assignment.FailureCode);
        Assert.Contains(assignment.Id.ToString("D"), assignment.SanitizedFailure, StringComparison.Ordinal);
        Assert.Contains(node.Id.ToString("D"), assignment.SanitizedFailure, StringComparison.Ordinal);
        Assert.Contains("CSweet.Office.Node", assignment.SanitizedFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NodeStatusCannotOverwriteControlPlaneArtifactResult()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var buildId = Guid.NewGuid();
        db.AddRange(pool, node, new AgentBuildJob { Id = buildId, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);
        var reference = await scheduler.SubmitAsync(Request(buildId, pool.Id));
        await scheduler.AssignPendingAsync();
        var assignment = await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == reference.AssignmentId);
        assignment.ResultArtifactDigest = Digest;
        assignment.ResultArtifactLocator = "control-plane-locator";
        await db.SaveChangesAsync();

        Assert.True(await scheduler.ReportStatusAsync(node.Id, assignment.Id, assignment.FencingEpoch,
            ExecutionAssignmentStatus.Starting, null, null, null));
        Assert.True(await scheduler.ReportStatusAsync(node.Id, assignment.Id, assignment.FencingEpoch,
            ExecutionAssignmentStatus.Running, null, null,
            new ExecutionWorkloadResult("provider-instance", "bounded log")));
        Assert.True(await scheduler.ReportStatusAsync(node.Id, assignment.Id, assignment.FencingEpoch,
            ExecutionAssignmentStatus.Failed, "guest-exited", "The guest exited.",
            new ExecutionWorkloadResult("provider-instance", string.Empty)));

        Assert.Equal(Digest, assignment.ResultArtifactDigest);
        Assert.Equal("control-plane-locator", assignment.ResultArtifactLocator);
        Assert.Equal("provider-instance", assignment.ProviderInstanceId);
        Assert.Equal("bounded log", assignment.ResultLogExcerpt);
    }

    [Fact]
    public async Task StatusReportReloadsAfterArtifactGrantConcurrencyChange()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var controlDb = new CSweetDbContext(options);
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var assignment = Assignment(pool.Id, node.Id, 1, 512);
        assignment.Status = ExecutionAssignmentStatus.Assigned;
        assignment.ArtifactDigest = Evidence;
        assignment.LeaseExpiresAt = Now.AddMinutes(1);
        controlDb.AddRange(pool, node, assignment,
            new AgentBuildJob { Id = assignment.AgentBuildJobId!.Value, PackageVersionId = Guid.NewGuid() });
        await controlDb.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(controlDb, new MutableTimeProvider(Now));

        Assert.NotNull(await scheduler.IssueArtifactReadGrantAsync(
            node.Id, assignment.Id, assignment.FencingEpoch));
        await using (var artifactTransferDb = new CSweetDbContext(options))
        {
            var transferred = await artifactTransferDb.ExecutionWorkloadAssignments.SingleAsync();
            transferred.AssignmentTokenHash = new string('d', 64);
            transferred.ArtifactGrantTransferHash = new string('e', 64);
            await artifactTransferDb.SaveChangesAsync();
        }

        Assert.True(await scheduler.ReportStatusAsync(
            node.Id, assignment.Id, assignment.FencingEpoch,
            ExecutionAssignmentStatus.Failed, "artifact-cache-failed", "The artifact cache failed.", null));

        await using var verificationDb = new CSweetDbContext(options);
        var updated = await verificationDb.ExecutionWorkloadAssignments.SingleAsync();
        Assert.Equal(ExecutionAssignmentStatus.Failed, updated.Status);
        Assert.Equal("artifact-cache-failed", updated.FailureCode);
    }

    [Fact]
    public async Task CancellationFencesTheNodeAndRejectsLateCompletion()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var buildId = Guid.NewGuid();
        db.AddRange(pool, node, new AgentBuildJob { Id = buildId, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);
        var reference = await scheduler.SubmitAsync(Request(buildId, pool.Id));
        await scheduler.AssignPendingAsync();
        var assignment = await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == reference.AssignmentId);
        var nodeEpoch = assignment.FencingEpoch;

        Assert.True(await scheduler.CancelAsync(assignment.Id, "administrator cancellation"));
        Assert.False(await scheduler.ReportStatusAsync(node.Id, assignment.Id, nodeEpoch,
            ExecutionAssignmentStatus.Completed, null, null, null));
        Assert.Equal(nodeEpoch + 1, assignment.FencingEpoch);
        Assert.Equal(ExecutionAssignmentStatus.Cancelled, assignment.Status);
        Assert.Equal("control-plane-cancelled", assignment.FailureCode);
    }

    [Fact]
    public async Task ArtifactReadGrantIsHashedAndExpiresWithAssignmentLease()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        var buildId = Guid.NewGuid();
        db.AddRange(pool, node, new AgentBuildJob { Id = buildId, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);
        var request = Request(buildId, pool.Id) with { ArtifactDigest = Evidence };
        var reference = await scheduler.SubmitAsync(request);
        await scheduler.AssignPendingAsync();
        var assignment = await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == reference.AssignmentId);

        var grant = await scheduler.IssueArtifactReadGrantAsync(
            node.Id, assignment.Id, assignment.FencingEpoch);

        Assert.NotNull(grant);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(grant))), assignment.AssignmentTokenHash);
        var replacement = await scheduler.IssueArtifactReadGrantAsync(
            node.Id, assignment.Id, assignment.FencingEpoch);
        Assert.NotNull(replacement);
        Assert.NotEqual(grant, replacement);
        Assert.NotEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(grant))), assignment.AssignmentTokenHash);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(await scheduler.IssueArtifactReadGrantAsync(
            node.Id, assignment.Id, assignment.FencingEpoch));
    }

    [Fact]
    public async Task PoolBusinessAllowlistAndRequiredLabelsAreHardPlacementFilters()
    {
        await using var db = CreateDb();
        var clock = new MutableTimeProvider(Now);
        var pool = Pool();
        pool.RequiredLabelsJson = "{\"region\":\"west\"}";
        pool.AllowedBusinessIdsJson = "[\"business-a\"]";
        var node = Node(pool, Guid.NewGuid());
        node.LabelsJson = "{\"region\":\"east\"}";
        var allowedBuild = Guid.NewGuid();
        var deniedBuild = Guid.NewGuid();
        db.AddRange(pool, node,
            new AgentBuildJob { Id = allowedBuild, PackageVersionId = Guid.NewGuid() },
            new AgentBuildJob { Id = deniedBuild, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, clock);
        var allowed = await scheduler.SubmitAsync(Request(allowedBuild, pool.Id) with { BusinessId = "business-a" });

        Assert.Equal(0, await scheduler.AssignPendingAsync());
        node.LabelsJson = "{\"region\":\"west\"}";
        await db.SaveChangesAsync();
        Assert.Equal(1, await scheduler.AssignPendingAsync());

        var denied = await scheduler.SubmitAsync(Request(deniedBuild, pool.Id) with { BusinessId = "business-b" });
        Assert.Equal(0, await scheduler.AssignPendingAsync());
        Assert.Equal(ExecutionAssignmentStatus.Assigned,
            (await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == allowed.AssignmentId)).Status);
        Assert.Equal(ExecutionAssignmentStatus.Pending,
            (await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == denied.AssignmentId)).Status);
    }

    [Fact]
    public async Task SchedulerAcceptsCurrentDevelopmentOfficeVersion()
    {
        await using var db = CreateDb();
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        node.NodeVersion = "0.1.0";
        var buildId = Guid.NewGuid();
        db.AddRange(pool, node, new AgentBuildJob { Id = buildId, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, new MutableTimeProvider(Now));
        var reference = await scheduler.SubmitAsync(Request(buildId, pool.Id));

        Assert.Equal(1, await scheduler.AssignPendingAsync());
        Assert.Equal(ExecutionAssignmentStatus.Assigned,
            (await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == reference.AssignmentId)).Status);
    }

    [Fact]
    public async Task SchedulerRejectsOfficeBelowCurrentDevelopmentVersion()
    {
        await using var db = CreateDb();
        var pool = Pool();
        var node = Node(pool, Guid.NewGuid());
        node.NodeVersion = "0.0.9";
        var buildId = Guid.NewGuid();
        db.AddRange(pool, node, new AgentBuildJob { Id = buildId, PackageVersionId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, new MutableTimeProvider(Now));
        var reference = await scheduler.SubmitAsync(Request(buildId, pool.Id));

        Assert.Equal(0, await scheduler.AssignPendingAsync());
        Assert.Equal(ExecutionAssignmentStatus.Pending,
            (await db.ExecutionWorkloadAssignments.SingleAsync(x => x.Id == reference.AssignmentId)).Status);
    }

    private static ExecutionPool Pool() => new()
    {
        Id = Guid.NewGuid(), Name = "Default", IsDefaultBuildPool = true,
        IsDefaultRuntimePool = true, IsEnabled = true, MaximumActiveWorkloads = 100,
        CreatedAt = Now, UpdatedAt = Now
    };

    private static FleetExecutionNode Node(ExecutionPool pool, Guid id)
    {
        var node = new FleetExecutionNode
        {
            Id = id, ExecutionPoolId = pool.Id, ExecutionPool = pool, Name = id.ToString("N"),
            MachineName = "machine", OperatingSystem = "linux", Architecture = "x64",
            NodeVersion = "0.1.0", ProtocolVersion = "1.0", Status = ExecutionNodeStatus.Ready,
            CertificateThumbprint = id.ToString("N"), CertificateExpiresAt = Now.AddDays(1),
            AllocatableCpuCount = 4, AllocatableMemoryMb = 4096, AllocatableDiskMb = 32768,
            MaximumConcurrentWorkloads = 4, SessionEpoch = 7, LastHeartbeatAt = Now,
            ApprovedAt = Now, CreatedAt = Now, UpdatedAt = Now
        };
        node.Providers.Add(new ExecutionNodeProvider
        {
            Id = Guid.NewGuid(), ExecutionNodeId = id, ProviderId = "firecracker-kvm",
            ProviderVersion = "1.0.0", BrokerProtocolVersion = "1.0", GuestImageDigest = Digest,
            CertificationSuiteVersion = "production-v1", CertificationEvidenceDigest = Evidence,
            CertifiedAt = Now.AddDays(-1), SupportsBuilderWorkloads = true,
            SupportsRuntimeWorkloads = true, IsAvailable = true, UpdatedAt = Now
        });
        return node;
    }

    private static ExecutionWorkloadAssignment Assignment(Guid poolId, Guid nodeId, int cpu, int memory) => new()
    {
        Id = Guid.NewGuid(), ExecutionPoolId = poolId, ExecutionNodeId = nodeId,
        AgentBuildJobId = Guid.NewGuid(), WorkloadKind = ExecutionWorkloadKind.Builder,
        Status = ExecutionAssignmentStatus.Running, ProviderId = "firecracker-kvm",
        GuestImageDigest = Digest, SpecificationJson = "{}", SpecificationDigest = Evidence,
        AssignmentTokenHash = new string('c', 64), FencingEpoch = 1, Attempt = 1,
        ReservedCpuCount = cpu, ReservedMemoryMb = memory, ReservedDiskMb = 1024,
        LeaseExpiresAt = Now.AddSeconds(30), QueuedAt = Now, AssignedAt = Now
    };

    private static ExecutionWorkloadRequest Request(Guid buildId, Guid poolId) => new(
        ExecutionWorkloadKind.Builder, buildId, null, poolId, null, null, Digest, null,
        1, 512, 1024, "{\"kind\":\"builder\"}");

    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Evidence = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}
