using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class ExecutionWorkloadOrchestratorTests
{
    [Theory]
    [InlineData("Completed", false)] [InlineData("Failed", false)] [InlineData("Failed", true)]
    public async Task ManagedRuntimeClosesOnlyAfterConfirmedStopAndNeverWhileRetryIsPending(string terminal, bool retry)
    {
        await using var db = CreateDb(); var pool = Pool(); var node = Node(pool, Guid.NewGuid());
        var build = DeliveryBuild(terminal == "Completed" ? W.DeliveryBuildStatuses.Succeeded : W.DeliveryBuildStatuses.Exhausted, 1, 3);
        var assignment = ToolchainAssignment(pool.Id, node.Id, build.Id, 1);
        assignment.Status = retry ? ExecutionAssignmentStatus.Pending : Enum.Parse<ExecutionAssignmentStatus>(terminal);
        var runtime = new AgentRuntimeInstance { Id = assignment.AgentRuntimeInstanceId!.Value, TickId = Guid.NewGuid(),
            AgentInstallationId = build.ProviderInstallationId, DeliveryBuildId = build.Id, QueuedAt = Now };
        var execution = new ExecutionAssignmentAttempt { AssignmentId = assignment.Id, FencingEpoch = assignment.FencingEpoch,
            ExecutionNodeId = node.Id, ProviderId = assignment.ProviderId, AssignedAt = Now, ProviderInstanceId = "build-vm" };
        if (retry) assignment.FencingEpoch++;
        db.AddRange(pool, node, build, assignment, runtime, execution); await db.SaveChangesAsync();
        var scheduler = new ExecutionWorkloadOrchestrator(db, new MutableTimeProvider(Now));
        Assert.True(AgentRuntimeInstance.IsActive(runtime.Status));
        Assert.True(await scheduler.ReportStoppedAsync(node.Id, assignment.Id, execution.FencingEpoch, assignment.ProviderId, "build-vm", false));
        Assert.Equal(retry ? AgentRuntimeStatus.Queued : terminal == "Completed" ? AgentRuntimeStatus.Completed : AgentRuntimeStatus.Failed, runtime.Status);
    }
}
