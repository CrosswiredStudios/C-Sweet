using System.Reflection;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class WorkDispatchRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressBeyondOldCapCreatesNextAuthorForAgentAndHumanReview(bool manual)
    {
        await using var db = CreateDb(); var state = await Seed(db, 1);
        var stage = await PrepareRework(db, state, improving: true);
        var output = WorkReworkPolicy.CompletedOutcome(stage)!.Output;
        if (manual)
            await (Task)typeof(WorkOrchestrationService).GetMethod("CompleteStageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(new WorkOrchestrationService(db, TimeProvider.System), [stage, "Completed", "changes_requested", "Review",
                    output.GetRawText(), Guid.NewGuid(), "manual-progress", CancellationToken.None])!;
        else
        {
            await AdvanceRework(db, state, stage, output);
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        var item = await db.WorkItemExecutions.Include(x => x.Stages).SingleAsync();
        Assert.Equal(4, item.Traversal); Assert.Equal("specialist-execution", item.CurrentStageKey);
        Assert.Single(item.Stages, x => x.Traversal == 4);
        Assert.Empty(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task StagnationBlocksWithOneDurableManagerNotification()
    {
        await using var db = CreateDb(); var state = await Seed(db, 1);
        var stage = await PrepareRework(db, state, improving: false);
        var board = await db.WorkBoards.SingleAsync(); board.ManagerOrganizationUserId = Guid.NewGuid();
        await db.SaveChangesAsync();
        await AdvanceRework(db, state, stage, WorkReworkPolicy.CompletedOutcome(stage)!.Output);
        await db.SaveChangesAsync();
        Assert.Equal(WorkStageExecutionStatus.Blocked, stage.Status);
        Assert.Equal(3, stage.ItemExecution!.Traversal);
        Assert.Contains("without measurable progress", stage.LastError);
        var notification = Assert.Single(await db.UserNotifications.ToListAsync());
        Assert.Equal(board.ManagerOrganizationUserId, notification.RecipientOrganizationUserId);
        Assert.Single(await db.WorkOrchestrationEvents.Where(x => x.EventType == "stage.rework.stalled").ToListAsync());
        await AdvanceRework(db, state, stage, WorkReworkPolicy.CompletedOutcome(stage)!.Output);
        await db.SaveChangesAsync();
        Assert.Single(await db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task AuthorizedRetryResumesAcceptedLegacyReviewExactlyOnceWithoutReviewRedispatch()
    {
        await using var db = CreateDb(); var state = await Seed(db, 1);
        var stage = await PrepareRework(db, state, improving: true);
        var user = Guid.NewGuid(); var member = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = state.Execution.OrganizationId,
            ApplicationUserId = user, EmployeeType = EmployeeType.Human, IsActive = true };
        db.CoreOrganizationUsers.Add(member);
        (await db.WorkBoards.SingleAsync()).ManagerOrganizationUserId = member.Id;
        stage.Status = WorkStageExecutionStatus.Failed; stage.LastError = WorkReworkPolicy.LegacyLimitReason;
        stage.ItemExecution!.Status = WorkItemExecutionStatus.Failed;
        stage.ItemExecution.BlockedReason = stage.LastError;
        stage.ItemExecution.WorkItem!.Status = WorkTaskStatus.Failed;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var service = new WorkOrchestrationService(db, TimeProvider.System);
        var request = new WorkOrchestrationControlRequest(1, "recover-progress", "Continue the improving review.");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RetryAsync(state.Execution.OrganizationId,
            state.Execution.BoardId, stage.Id, Guid.NewGuid(), request));
        await service.RetryAsync(state.Execution.OrganizationId, state.Execution.BoardId, stage.Id, user, request);
        await service.RetryAsync(state.Execution.OrganizationId, state.Execution.BoardId, stage.Id, user, request);
        db.ChangeTracker.Clear();
        var item = await db.WorkItemExecutions.Include(x => x.Stages).Include(x => x.WorkItem).SingleAsync();
        Assert.Equal(4, item.Traversal); Assert.Equal("specialist-execution", item.CurrentStageKey);
        Assert.Null(item.BlockedReason); Assert.Null(item.WorkItem!.BlockReason);
        Assert.Single(item.Stages, x => x.Traversal == 4);
        Assert.Equal(WorkTaskStatus.Ready, item.WorkItem.Status);
        Assert.Single(await db.WorkOrchestrationEvents.Where(x => x.EventType == "stage.retry.requested").ToListAsync());
        Assert.Empty(await db.AgentWorkItems.ToListAsync());
        Assert.Single(await db.WorkExecutionAttempts.Where(x => x.StageExecutionId == stage.Id).ToListAsync());
    }

    private static async Task<WorkStageExecution> PrepareRework(CSweet.Infrastructure.Persistence.CSweetDbContext db, State state, bool improving)
    {
        var item = state.Stage.ItemExecution!;
        (await db.WorkBoards.SingleAsync()).ManagerOrganizationUserId = Guid.NewGuid();
        db.WorkStageExecutions.Remove(state.Stage); item.Stages.Clear();
        var author = state.Policy.Stages.Single();
        var quality = new WorkOrchestrationStage { Id = Guid.NewGuid(), PolicyRevisionId = state.Policy.Id,
            Key = "quality", Type = WorkOrchestrationStageType.AgentExecution };
        var transition = new WorkOrchestrationTransition { Id = Guid.NewGuid(), PolicyRevisionId = state.Policy.Id,
            FromStageKey = "quality", OutcomeCode = "changes_requested", ToStageKey = author.Key, MaximumTraversals = 3 };
        state.Policy.Stages.Add(quality); state.Policy.Transitions.Add(transition);
        db.AddRange(quality, transition);
        WorkStageExecution stage = null!;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            stage = WorkReworkPolicyTests.Add(item, WorkReworkPolicyTests.Review(cycle + 1, improving ? 8 - cycle : 8));
            db.WorkStageExecutions.Add(stage);
        }
        stage.Status = WorkStageExecutionStatus.Running;
        await db.SaveChangesAsync();
        return stage;
    }

    private static async Task AdvanceRework(CSweet.Infrastructure.Persistence.CSweetDbContext db, State state,
        WorkStageExecution stage, JsonElement output) => await (Task)typeof(WorkOrchestrator)
        .GetMethod("AdvanceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(new WorkOrchestrator(db, null!, null!, null!, [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance),
            [state.Execution, state.Policy, stage, "changes_requested", "Review", output.GetRawText(), DateTimeOffset.UtcNow, CancellationToken.None])!;
}
