using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ExecutionArtifactGrantLeaseServiceTests
{
    [Fact]
    public async Task GrantRejectsConcurrentAndCompletedReplayButAllowsInterruptedRetry()
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var nodeId = Guid.NewGuid();
        var assignment = new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(), ExecutionPoolId = Guid.NewGuid(), ExecutionNodeId = nodeId,
            AgentRuntimeInstanceId = Guid.NewGuid(), WorkloadKind = ExecutionWorkloadKind.Runtime,
            Status = ExecutionAssignmentStatus.Assigned, GuestImageDigest = Digest('a'),
            ArtifactDigest = Digest('b'), SpecificationDigest = Digest('c'),
            AssignmentTokenHash = new string('d', 64), FencingEpoch = 3,
            LeaseExpiresAt = now.AddMinutes(1), QueuedAt = now
        };
        db.ExecutionWorkloadAssignments.Add(assignment);
        await db.SaveChangesAsync();
        var service = new ExecutionArtifactGrantLeaseService(db, new FixedClock(now));
        var transfer = new string('e', 64);

        Assert.True(await service.ClaimAsync(nodeId, assignment.Id, 3, Digest('b'), new string('d', 64), transfer));
        Assert.False(await service.ClaimAsync(nodeId, assignment.Id, 3, Digest('b'), new string('d', 64), transfer));
        await service.ReleaseAsync(assignment.Id, transfer, consumed: false);
        Assert.True(await service.ClaimAsync(nodeId, assignment.Id, 3, Digest('b'), new string('d', 64), transfer));
        await service.ReleaseAsync(assignment.Id, transfer, consumed: true);
        Assert.False(await service.ClaimAsync(nodeId, assignment.Id, 3, Digest('b'), new string('d', 64), transfer));
    }

    [Fact]
    public async Task GrantIsBoundToNodeEpochDigestTokenAndTransfer()
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var nodeId = Guid.NewGuid();
        var assignment = new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(), ExecutionPoolId = Guid.NewGuid(), ExecutionNodeId = nodeId,
            AgentRuntimeInstanceId = Guid.NewGuid(), WorkloadKind = ExecutionWorkloadKind.Runtime,
            Status = ExecutionAssignmentStatus.Running, GuestImageDigest = Digest('a'),
            ArtifactDigest = Digest('b'), SpecificationDigest = Digest('c'),
            AssignmentTokenHash = new string('d', 64), FencingEpoch = 7,
            LeaseExpiresAt = now.AddMinutes(1), QueuedAt = now
        };
        db.ExecutionWorkloadAssignments.Add(assignment);
        await db.SaveChangesAsync();
        var service = new ExecutionArtifactGrantLeaseService(db, new FixedClock(now));

        Assert.False(await service.ClaimAsync(Guid.NewGuid(), assignment.Id, 7, Digest('b'), new string('d', 64), new string('e', 64)));
        Assert.False(await service.ClaimAsync(nodeId, assignment.Id, 8, Digest('b'), new string('d', 64), new string('e', 64)));
        Assert.False(await service.ClaimAsync(nodeId, assignment.Id, 7, Digest('f'), new string('d', 64), new string('e', 64)));
        Assert.False(await service.ClaimAsync(nodeId, assignment.Id, 7, Digest('b'), new string('0', 64), new string('e', 64)));
        Assert.True(await service.ClaimAsync(nodeId, assignment.Id, 7, Digest('b'), new string('d', 64), new string('e', 64)));
        await service.ReleaseAsync(assignment.Id, new string('e', 64), consumed: false);
        Assert.False(await service.ClaimAsync(nodeId, assignment.Id, 7, Digest('b'), new string('d', 64), new string('f', 64)));
    }

    private static string Digest(char value) => $"sha256:{new string(value, 64)}";
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
