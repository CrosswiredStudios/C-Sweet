using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class SprintCompletionWakeTests
{
    [Fact]
    public async Task Completing_delivery_persists_a_project_bound_wake_once_with_the_sprint()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var project = Guid.NewGuid();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project, Name = "Delivery" };
        var sprint = new WorkSprint { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, Name = "Sprint 1", Status = WorkSprintStatus.Active };
        var policy = new WorkOrchestrationPolicyRevision { Id = Guid.NewGuid(), BoardId = board.Id, OrganizationId = org, Name = "Delivery" };
        var task = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, Status = WorkTaskStatus.Completed };
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, SprintId = sprint.Id, PolicyRevisionId = policy.Id, Status = WorkSprintExecutionStatus.Active };
        var item = new WorkItemExecution { Id = Guid.NewGuid(), SprintExecutionId = execution.Id, WorkItemId = task.Id, Status = WorkItemExecutionStatus.Completed, CurrentStageKey = "done" };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = item.Id, StageKey = "done", StageType = WorkOrchestrationStageType.Terminal, Status = WorkStageExecutionStatus.Completed };
        db.AddRange(board, sprint, policy, task, execution, item, stage); await db.SaveChangesAsync();
        var orchestrator = new WorkOrchestrator(db, null!, null!, null!, [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        await orchestrator.PulseAsync(); await orchestrator.PulseAsync();
        Assert.Equal(WorkSprintStatus.Completed, (await db.WorkSprints.SingleAsync()).Status);
        Assert.Equal(WorkSprintExecutionStatus.Completed, (await db.WorkSprintExecutions.SingleAsync()).Status);
        var wake = Assert.Single(db.AgentPlatformEventOutbox);
        Assert.Equal(Shared.WorkstreamEventNames.SprintChangedV1, wake.EventType);
        var data = JsonSerializer.Deserialize<Shared.GenericResourceEvent>(wake.DataJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(project, data.Context.WorkstreamId); Assert.Equal(board.Id, data.Context.BoardId); Assert.Equal(sprint.Id, data.AggregateId);
        Assert.Equal(sprint.Revision, data.Revision);
    }
}
