using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class WorkDispatchRecoveryTests
{
    [Fact]
    public async Task Waiting_card_is_repaired_then_moves_only_when_inbox_is_claimed()
    {
        await using var db = CreateDb();
        var state = await Seed(db, 1);
        var (ready, active) = await AddColumns(db, state);
        var item = state.Stage.ItemExecution!.WorkItem!;
        item.BoardColumnId = active;
        item.Status = WorkTaskStatus.Running; // Old scheduler's incorrect placement.
        state.Policy.BoardConcurrencyLimit = 0;
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var orchestrator = new WorkOrchestrator(db, inbox, null!, new Runtime(db), [],
            TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);

        await orchestrator.PulseAsync();
        Assert.Equal(ready, item.BoardColumnId);
        Assert.Equal(WorkTaskStatus.Ready, item.Status);
        Assert.Empty(db.AgentWorkItems);
        var revision = item.Revision;
        var events = await db.ApplicationRealtimeOutbox.CountAsync();
        Assert.True(events > 0);
        await orchestrator.PulseAsync();
        Assert.Equal(revision, item.Revision);
        Assert.Equal(events, await db.ApplicationRealtimeOutbox.CountAsync());

        await Dispatch(orchestrator, state);
        await orchestrator.PulseAsync();
        Assert.Equal(ready, item.BoardColumnId);
        Assert.Equal(WorkStageExecutionStatus.Dispatching, state.Stage.Status);
        var work = Assert.Single(db.AgentWorkItems);
        state.Stage.ItemExecution.CurrentStageKey = state.Stage.StageKey;
        await db.SaveChangesAsync();
        var claimed = await inbox.ClaimAsync(new McpAgentSession
        {
            AgentInstallationId = state.Installation.Id,
            OrganizationId = state.Execution.OrganizationId.ToString(),
            RuntimeInstanceId = Guid.NewGuid()
        }, CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(active, item.BoardColumnId); // Immediately, before the scheduler runs.
        Assert.Equal(WorkStageExecutionStatus.Running, state.Stage.Status);
        await db.SaveChangesAsync();
        await orchestrator.PulseAsync();
        Assert.Equal(active, item.BoardColumnId);
        Assert.Equal(WorkTaskStatus.Running, item.Status);
        Assert.Equal(WorkExecutionAttemptStatus.Running, Assert.Single(db.WorkExecutionAttempts).Status);

        // A returned/expired lease is queued again, not falsely shown as active.
        work.Status = AgentWorkStatus.Pending;
        await db.SaveChangesAsync();
        await orchestrator.PulseAsync();
        Assert.Equal(ready, item.BoardColumnId);
        Assert.Equal(WorkTaskStatus.Ready, item.Status);
    }

    [Fact]
    public async Task Dependency_wait_and_retry_stay_ready_but_later_review_keeps_its_column()
    {
        await using var db = CreateDb();
        var state = await Seed(db, 1);
        var (ready, active) = await AddColumns(db, state);
        var item = state.Stage.ItemExecution!.WorkItem!;
        foreach (var status in new[] { WorkStageExecutionStatus.Pending, WorkStageExecutionStatus.Backoff })
        {
            item.BoardColumnId = active;
            state.Stage.Status = status;
            WorkOrchestrationBoardState.SynchronizeAgentCard(state.Policy, state.Stage, DateTimeOffset.UtcNow);
            Assert.Equal(ready, item.BoardColumnId);
            Assert.Equal(WorkTaskStatus.Ready, item.Status);
        }
        state.Policy.Transitions.Clear(); // Review has no incoming queue stage.
        WorkOrchestrationBoardState.SynchronizeAgentCard(state.Policy, state.Stage, DateTimeOffset.UtcNow);
        Assert.Equal(active, item.BoardColumnId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Board_owner_uses_current_stage_assignment_instead_of_stale_ticket_owner(bool human)
    {
        await using var db = CreateDb();
        var state = await Seed(db, 1);
        state.Stage.ItemExecution!.CurrentStageKey = state.Stage.StageKey;
        var member = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = state.Execution.OrganizationId,
            DisplayName = "Current reviewer", AgentInstallationId = human ? null : state.Installation.Id };
        var wrongTenant = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(),
            DisplayName = "Wrong tenant", AgentInstallationId = state.Installation.Id };
        db.AddRange(member, wrongTenant);
        if (human)
        {
            state.Stage.PrincipalKind = WorkOrchestrationPrincipalKind.Human;
            state.Stage.OrganizationUserId = member.Id;
            state.Stage.AgentInstallationId = null;
        }
        await db.SaveChangesAsync();
        var item = state.Stage.ItemExecution.WorkItem!;
        var response = new WorkBoardItemResponse(item.Id, state.Execution.BoardId, Guid.NewGuid(), null, null,
            "Task", "Ticket", "", "Running", "Medium", null, 0, 1, null, default, default,
            AssignedDisplayName: "Previous developer");
        var service = new WorkBoardService(db, null!, null!);
        var result = Assert.Single(await service.ResolveCardOwnersAsync(state.Execution.OrganizationId,
            state.Execution.BoardId, [response], CancellationToken.None));
        Assert.Equal(member.DisplayName, result.AssignedDisplayName);
        Assert.Equal(member.Id, result.AssignedEmployeeId);
        Assert.Equal(human ? null : state.Installation.Id, result.AssignedInstallationId);
    }

    [Fact]
    public async Task Advancing_ready_queue_does_not_move_card_before_capacity_is_available()
    {
        await using var db = CreateDb();
        var state = await Seed(db, 1);
        var (ready, _) = await AddColumns(db, state);
        state.Stage.StageKey = "ready";
        state.Stage.StageType = WorkOrchestrationStageType.Queue;
        state.Stage.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        state.Stage.ItemExecution!.CurrentStageKey = "ready";
        state.Stage.ItemExecution.WorkItem!.BoardColumnId = ready;
        state.Policy.BoardConcurrencyLimit = 0;
        await db.SaveChangesAsync();
        var orchestrator = new WorkOrchestrator(db,
            new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System),
            null!, new Runtime(db), [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        await orchestrator.PulseAsync();
        Assert.Equal(2, await db.WorkStageExecutions.CountAsync());
        Assert.Equal(ready, state.Stage.ItemExecution.WorkItem.BoardColumnId);
        Assert.Equal(WorkTaskStatus.Ready, state.Stage.ItemExecution.WorkItem.Status);
        Assert.Empty(db.AgentWorkItems);
    }

    [Fact]
    public async Task Queued_dispatch_reserves_hard_column_capacity_while_card_is_still_ready()
    {
        await using var db = CreateDb();
        var state = await Seed(db, 1);
        var (ready, active) = await AddColumns(db, state);
        var column = await db.WorkBoardColumns.SingleAsync(x => x.Id == active);
        column.WipPolicy = WorkBoardWipPolicy.HardLimit;
        column.WipLimit = 1;
        state.Stage.Status = WorkStageExecutionStatus.Dispatching;
        state.Stage.ItemExecution!.WorkItem!.BoardColumnId = ready;
        await db.SaveChangesAsync();
        var candidate = new WorkStageExecution { ItemExecutionId = Guid.NewGuid(), StageKey = state.Stage.StageKey };
        var orchestrator = new WorkOrchestrator(db, null!, null!, new Runtime(db), [],
            TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        var capacity = (Task<bool>)typeof(WorkOrchestrator).GetMethod("HasCapacityAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(orchestrator, [state.Execution, state.Policy, candidate, CancellationToken.None])!;
        Assert.False(await capacity);
    }

    private static async Task<(Guid Ready, Guid Active)> AddColumns(
        CSweet.Infrastructure.Persistence.CSweetDbContext db, State state)
    {
        var ready = new WorkBoardColumn { Id = Guid.NewGuid(), BoardId = state.Execution.BoardId, Name = "Ready" };
        var active = new WorkBoardColumn { Id = Guid.NewGuid(), BoardId = state.Execution.BoardId,
            Name = "In Progress", Category = WorkBoardColumnCategory.InProgress };
        state.Policy.Stages.Single().ColumnId = active.Id;
        var queue = new WorkOrchestrationStage { Id = Guid.NewGuid(), PolicyRevisionId = state.Policy.Id,
            Key = "ready", Type = WorkOrchestrationStageType.Queue, ColumnId = ready.Id };
        var transition = new WorkOrchestrationTransition { Id = Guid.NewGuid(), PolicyRevisionId = state.Policy.Id,
            FromStageKey = queue.Key, ToStageKey = state.Stage.StageKey, OutcomeCode = "ready" };
        state.Policy.Stages.Add(queue);
        state.Policy.Transitions.Add(transition);
        db.AddRange(ready, active, queue, transition);
        await db.SaveChangesAsync();
        return (ready.Id, active.Id);
    }
}
