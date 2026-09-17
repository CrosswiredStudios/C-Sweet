using System.Text.Json;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class WorkExecutionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Clock Clock { get; } = new();
        public CSweetDbContext Db { get; }
        public Guid Org = Guid.NewGuid(), Installation = Guid.NewGuid(), Project = Guid.NewGuid(), Event = Guid.NewGuid();
        public WorkTask Epic = null!, Story = null!, Task1 = null!, Task2 = null!;
        public AgentWorkItem Work = null!;
        public AgentWorkAttempt Attempt = null!;
        public Fixture() => Db = new(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, executionClock: Clock);
        public async Task Seed()
        {
            var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = Org, Kind = WorkBoardKind.Personal, WorkstreamId = Project };
            Db.Workstreams.Add(new() { Id = Project, OrganizationId = Org, Name = "Project", CreatedAt = Clock.Now });
            Epic = Item("Epic", WorkItemKind.Epic, null); Epic.Status = WorkTaskStatus.Running;
            Epic.ClaimEventId = Event; Epic.ClaimExpiresAt = Clock.Now.AddHours(3);
            Story = Item("Story", WorkItemKind.Story, Epic.Id);
            Task1 = Item("First", WorkItemKind.Task, Story.Id); Task2 = Item("Second", WorkItemKind.Task, Story.Id);
            Db.AddRange(board, Epic, Story, Task1, Task2);
            await Db.SaveChangesAsync();
            Work = new() { Id = Guid.NewGuid(), OrganizationId = Org.ToString(), AgentInstallationId = Installation,
                Kind = AgentWorkKind.Event, SourceId = Event.ToString(), Status = AgentWorkStatus.Leased, DeadlineAt = Clock.Now.AddHours(3) };
            Attempt = new() { Id = Guid.NewGuid(), AgentWorkItem = Work, AgentWorkItemId = Work.Id, Attempt = 1,
                ClaimedAt = Clock.Now, LastConfirmedAt = Clock.Now, LeaseExpiresAt = Clock.Now.AddHours(1) };
            Db.AddRange(Work, Attempt); await Db.SaveChangesAsync();
            WorkTask Item(string title, WorkItemKind kind, Guid? parent) => new()
            {
                Id = Guid.NewGuid(), OrganizationId = Org, Board = board, BoardId = board.Id,
                Title = title, Kind = kind, ParentWorkTaskId = parent, AssignedAgentInstallationId = Installation,
                CreatedAt = Clock.Now, UpdatedAt = Clock.Now,
                PlanningSpecificationJson = parent.HasValue ? JsonSerializer.Serialize(new { personalPlan = new { rootItemId = Epic.Id, order = 1, execution = "Implementation" } }) : null
            };
        }
        public async Task Change(WorkTask item, WorkTaskStatus status, int minutes = 0)
        { Clock.Now = Clock.Now.AddMinutes(minutes); item.Status = status; item.UpdatedAt = Clock.Now; item.Revision++; await Db.SaveChangesAsync(); }
        public async Task<AgentRunLog> Call()
        {
            var log = WorkEfficiencyTests.Call(Org); log.StartedAt = Clock.Now; log.ProviderStartedAt = Clock.Now;
            log.AgentInstallationId = Installation;
            await InferenceAttribution.CaptureAsync(Db, log, Work.Id, attemptNumber: 1);
            Db.AgentRunLogs.Add(log); await Db.SaveChangesAsync(); return log;
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task EpicPlanningAndTaskExecution_ReconcileThroughStoryAndProject_AndCaptureOwnership()
    {
        await using var f = new Fixture(); await f.Seed();
        var planning = await f.Call(); Assert.Equal(f.Epic.Id, planning.WorkItemId);
        await f.Change(f.Task1, WorkTaskStatus.Running, 2); var first = await f.Call();
        await f.Change(f.Task1, WorkTaskStatus.Completed, 3);
        await f.Change(f.Task2, WorkTaskStatus.Running); var second = await f.Call();
        await f.Change(f.Task2, WorkTaskStatus.Completed, 5);
        await f.Change(f.Epic, WorkTaskStatus.Completed);
        f.Attempt.FinishedAt = f.Attempt.LastConfirmedAt = f.Clock.Now; f.Work.Status = AgentWorkStatus.Completed;
        await f.Db.SaveChangesAsync(); await f.Db.SaveChangesAsync();
        Assert.Equal(f.Task1.Id, first.WorkItemId); Assert.Equal(f.Task2.Id, second.WorkItemId);
        Assert.Equal(f.Attempt.Id, first.AgentWorkAttemptId);
        var report = await new WorkEfficiencyService(f.Db, f.Clock).GetAsync(f.Org);
        var epic = report.WorkItems.Single(x => x.Id == f.Epic.Id);
        Assert.Equal(1, epic.Direct.ModelCalls); Assert.Equal(3, epic.Total.ModelCalls);
        Assert.Equal(120_000, epic.Direct.ActiveAgentTimeMs); Assert.Equal(600_000, epic.Total.ActiveAgentTimeMs);
        Assert.Equal(480_000, report.WorkItems.Single(x => x.Id == f.Story.Id).Total.ActiveAgentTimeMs);
        Assert.Equal(180_000, report.WorkItems.Single(x => x.Id == f.Task1.Id).Direct.ActiveAgentTimeMs);
        Assert.Equal("Confirmed", report.WorkItems.Single(x => x.Id == f.Task1.Id).TimingCoverage);
        Assert.Equal(600_000, epic.Lifecycle.ElapsedTimeMs);
        Assert.Equal(600_000, report.Projects.Single().Total.ActiveAgentTimeMs);
        f.Task1.ParentWorkTaskId = null; await f.Db.SaveChangesAsync();
        Assert.Contains(f.Story.Id.ToString(), first.AncestorWorkItemIdsJson);
        var activity = await new WorkEfficiencyService(f.Db, f.Clock).GetActivityAsync(f.Org, f.Epic.Id, null, 0, 25, subtree: true);
        Assert.Equal(3, activity.Calls.Count);
        Assert.Single((await new WorkEfficiencyService(f.Db, f.Clock).GetActivityAsync(f.Org, f.Epic.Id, null, 0, 25)).Calls);
        Assert.All(await f.Db.WorkExecutionIntervals.ToListAsync(), x => Assert.NotNull(x.EndedAt));
    }

    [Fact]
    public async Task QueueAndDeferredTimeAreExcluded_LeaseLossStopsAtLastEvidence_AfterRestart()
    {
        await using var f = new Fixture(); await f.Seed(); await f.Change(f.Task1, WorkTaskStatus.Running);
        f.Clock.Now = f.Clock.Now.AddMinutes(2);
        var queue = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.Org, AgentWorkItemId = f.Work.Id,
            StartedAt = f.Clock.Now, MeasurementKind = "Queue", Status = "Queued" };
        f.Db.AgentRunLogs.Add(queue); await f.Db.SaveChangesAsync();
        f.Clock.Now = f.Clock.Now.AddMinutes(4); queue.Status = "Generating"; await f.Db.SaveChangesAsync();
        f.Clock.Now = f.Clock.Now.AddMinutes(3); f.Epic.ClaimEventId = null; f.Epic.NextReviewAt = f.Clock.Now.AddHours(1);
        f.Epic.UpdatedAt = f.Clock.Now; await f.Db.SaveChangesAsync();
        Assert.Equal(300_000, WorkEfficiencyService.Effort(await f.Db.WorkExecutionIntervals.ToListAsync(), null, null, f.Clock.Now));
        f.Clock.Now = f.Clock.Now.AddMinutes(10); f.Epic.ClaimEventId = f.Event; f.Epic.NextReviewAt = null;
        await f.Db.SaveChangesAsync();
        f.Clock.Now = f.Clock.Now.AddMinutes(1); f.Attempt.LastConfirmedAt = f.Clock.Now; f.Attempt.LeaseExpiresAt = f.Clock.Now.AddMinutes(1);
        await f.Db.SaveChangesAsync();
        var lastSeen = f.Clock.Now; f.Clock.Now = f.Clock.Now.AddHours(5);
        f.Db.ChangeTracker.Clear(); var expired = await f.Db.AgentWorkAttempts.SingleAsync();
        expired.FinishedAt = f.Clock.Now; expired.Error = "lease_expired"; await f.Db.SaveChangesAsync();
        var intervals = await f.Db.WorkExecutionIntervals.ToListAsync();
        Assert.Equal(lastSeen, intervals.Single(x => x.EndReason == "LeaseLost").EndedAt);
        Assert.Equal(360_000, WorkEfficiencyService.Effort(intervals, null, null, f.Clock.Now));
        var log = await f.Call(); Assert.Null(log.WorkItemId); Assert.Equal("Unknown", log.AttributionKind);
    }

    [Fact]
    public async Task AmbiguousChildrenAndOtherBusinessCannotTakeOwnership()
    {
        await using var f = new Fixture(); await f.Seed();
        f.Task1.Status = f.Task2.Status = WorkTaskStatus.Running; await f.Db.SaveChangesAsync();
        Assert.Null((await f.Call()).WorkItemId);
        var other = WorkEfficiencyTests.Call(Guid.NewGuid()); other.AgentInstallationId = f.Installation;
        await InferenceAttribution.CaptureAsync(f.Db, other, f.Work.Id);
        Assert.Null(other.AgentWorkAttemptId); Assert.Null(other.WorkItemId); Assert.Equal("Unknown", other.AttributionKind);
    }

    [Fact]
    public void ParallelEffortIsAdditive_OverlapsWithinAttemptAreDeduplicated_AndWindowClips()
    {
        var now = DateTimeOffset.UtcNow; var a = Guid.NewGuid(); var b = Guid.NewGuid();
        WorkExecutionInterval Interval(Guid attempt, int start, int end) => new() { Id = Guid.NewGuid(), AgentWorkAttemptId = attempt,
            StartedAt = now.AddMinutes(start), ConfirmedThrough = now.AddMinutes(end) };
        var rows = new[] { Interval(a, 0, 10), Interval(a, 5, 10), Interval(b, 0, 10) };
        Assert.Equal(1_200_000, WorkEfficiencyService.Effort(rows, null, null, now.AddMinutes(10)));
        Assert.Equal(600_000, WorkEfficiencyService.Effort(rows, now.AddMinutes(2), now.AddMinutes(7), now.AddMinutes(10)));
    }

    [Fact]
    public void RunningReopenedAndCancelledElapsedKeepOriginalStart()
    {
        var now = DateTimeOffset.UtcNow;
        var events = new[] { "Backlog", "Running", "Completed", "Running", "Cancelled" }.Select((state, i) =>
            new WorkLifecycleEvent { Status = state, OccurredAt = now.AddMinutes(i), PreviousStatus = i == 0 ? null : "Running" }).ToArray();
        var running = WorkEfficiencyService.Lifecycle(events.Take(4), now, now.AddMinutes(10));
        Assert.Equal(540_000, running.ElapsedTimeMs); Assert.True(running.IsOpen);
        var stopped = WorkEfficiencyService.Lifecycle(events, now, now.AddMinutes(10));
        Assert.Equal(180_000, stopped.ElapsedTimeMs); Assert.False(stopped.IsOpen); Assert.Null(stopped.CompletedAt);
    }

    [Fact]
    public async Task HistoricalClaimRecoversStartOnly_NeverInventsCompletionOrActiveEffort()
    {
        await using var db = WorkEfficiencyTests.Database(); var org = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, Title = "Historical", CreatedAt = now.AddDays(-4),
            UpdatedAt = now, Status = WorkTaskStatus.Completed };
        db.CoreWorkTasks.Add(item); await db.SaveChangesAsync();
        db.WorkItemActivities.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, WorkItemId = item.Id,
            EventType = "agent.ticket.claimed", OccurredAt = now.AddDays(-2) }); await db.SaveChangesAsync();
        var row = (await new WorkEfficiencyService(db, new WorkEfficiencyTests.Clock(now)).GetAsync(org)).WorkItems.Single();
        Assert.Equal(now.AddDays(-2), row.Lifecycle.StartedAt);
        Assert.Null(row.Lifecycle.ElapsedTimeMs); Assert.Equal(0, row.Total.ActiveAgentTimeMs);
        Assert.Equal("History incomplete", row.TimingCoverage);
    }
    [Theory]
    [InlineData(WorkTaskStatus.Blocked)]
    [InlineData(WorkTaskStatus.Ready)]
    [InlineData(WorkTaskStatus.Cancelled)]
    public async Task PausesAndStopsCloseIntervalsWithoutAccumulatingWaiting(WorkTaskStatus status)
    {
        await using var f = new Fixture(); await f.Seed(); await f.Change(f.Task1, WorkTaskStatus.Running);
        await f.Change(f.Epic, status, 2);
        f.Clock.Now = f.Clock.Now.AddMinutes(30); await f.Db.SaveChangesAsync();
        var intervals = await f.Db.WorkExecutionIntervals.ToListAsync();
        Assert.All(intervals, x => Assert.NotNull(x.EndedAt));
        Assert.Equal(120_000, WorkEfficiencyService.Effort(intervals, null, null, f.Clock.Now));
        Assert.Null((await f.Call()).WorkItemId);
    }

    [Fact]
    public async Task DispatchRejectsExpiredPersonalClaimEvenWhenDeliveryLeaseIsLive()
    {
        await using var f = new Fixture(); await f.Seed(); await f.Change(f.Task1, WorkTaskStatus.Running);
        f.Epic.ClaimExpiresAt = f.Clock.Now.AddMinutes(1); await f.Db.SaveChangesAsync();
        f.Clock.Now = f.Clock.Now.AddMinutes(2);
        Assert.Null((await f.Call()).WorkItemId);
    }

    [Fact]
    public async Task TeamStageRetainsItsOwnTicketWithoutAPersonalClaim()
    {
        await using var f = new Fixture(); await f.Seed();
        f.Epic.Board!.Kind = WorkBoardKind.Standard;
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = f.Task1.Id };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecution = execution, ItemExecutionId = execution.Id,
            AgentInstallationId = f.Installation };
        var delivery = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id, StageExecution = stage,
            AgentWorkItemId = f.Work.Id, Status = WorkExecutionAttemptStatus.Running };
        f.Work.SourceType = "WorkStageExecution"; f.Work.SourceId = stage.Id.ToString();
        f.Db.AddRange(execution, stage, delivery); await f.Db.SaveChangesAsync();
        f.Attempt.LastConfirmedAt = f.Clock.Now.AddSeconds(1); f.Clock.Now = f.Clock.Now.AddSeconds(1);
        await f.Db.SaveChangesAsync();
        Assert.Equal(f.Task1.Id, (await f.Call()).WorkItemId);
    }

    [Fact]
    public async Task RetryStartsANewIntervalAndExcludesTheDelayBetweenAttempts()
    {
        await using var f = new Fixture(); await f.Seed(); await f.Change(f.Task1, WorkTaskStatus.Running);
        f.Clock.Now = f.Clock.Now.AddMinutes(2); f.Attempt.FinishedAt = f.Attempt.LastConfirmedAt = f.Clock.Now;
        f.Work.Status = AgentWorkStatus.Pending; await f.Db.SaveChangesAsync();
        f.Clock.Now = f.Clock.Now.AddMinutes(10); f.Work.Status = AgentWorkStatus.Leased;
        var retry = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = f.Work.Id, AgentWorkItem = f.Work,
            Attempt = 2, ClaimedAt = f.Clock.Now, LastConfirmedAt = f.Clock.Now, LeaseExpiresAt = f.Clock.Now.AddHours(1) };
        f.Db.Add(retry); await f.Db.SaveChangesAsync();
        f.Clock.Now = f.Clock.Now.AddMinutes(3); retry.FinishedAt = retry.LastConfirmedAt = f.Clock.Now;
        f.Work.Status = AgentWorkStatus.Completed; await f.Db.SaveChangesAsync();
        var row = (await new WorkEfficiencyService(f.Db, f.Clock).GetAsync(f.Org)).WorkItems.Single(x => x.Id == f.Task1.Id);
        Assert.Equal(300_000, row.Total.ActiveAgentTimeMs); Assert.Equal(900_000, row.Lifecycle.ElapsedTimeMs);
    }

    [Fact]
    public async Task RecoveryClosesExpiredIntervalsWithoutAReplacementAgent_AndIsIdempotent()
    {
        await using var f = new Fixture(); await f.Seed(); await f.Change(f.Task1, WorkTaskStatus.Running);
        f.Clock.Now = f.Clock.Now.AddMinutes(2); f.Attempt.LastConfirmedAt = f.Clock.Now;
        f.Attempt.LeaseExpiresAt = f.Clock.Now.AddMinutes(1); await f.Db.SaveChangesAsync();
        f.Clock.Now = f.Clock.Now.AddHours(2); f.Db.ChangeTracker.Clear();
        Assert.Equal(1, await WorkExecutionRecoveryWorker.RecoverAsync(f.Db, f.Clock.Now));
        var count = await f.Db.AuditOutbox.CountAsync();
        Assert.Equal(0, await WorkExecutionRecoveryWorker.RecoverAsync(f.Db, f.Clock.Now));
        Assert.Equal(count, await f.Db.AuditOutbox.CountAsync());
        var rows = await f.Db.WorkExecutionIntervals.ToListAsync();
        Assert.All(rows, x => Assert.NotNull(x.EndedAt));
        Assert.Equal(120_000, WorkEfficiencyService.Effort(rows, null, null, f.Clock.Now));
        Assert.Null((await f.Db.WorkExecutionContexts.SingleAsync()).WorkItemId);
    }

}
