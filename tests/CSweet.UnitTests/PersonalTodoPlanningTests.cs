using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Fact]
    public async Task PlanCannotUseAnotherOwnersClaimOrChangeAnAcceptedBacklog()
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var root = await service.AddAsync(setup.Organization.Id, actor, Add("Tetris", "plan", null));
        var stored = await db.CoreWorkTasks.SingleAsync();
        stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = Guid.NewGuid(); stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreatePlanAsync(setup.Organization.Id,
            new(setup.FirstManager.Id, null), Plan(root.Id)));
        await service.CreatePlanAsync(setup.Organization.Id, actor, Plan(root.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePlanAsync(setup.Organization.Id, actor,
            Plan(root.Id) with { EpicTitle = "Different MVP" }));
        var grant = await db.ScopedActionGrants.SingleAsync(x => x.SubjectId == actor.AgentInstallationId && x.Action == CSweet.Contracts.WorkManagement.PersonalTodoActions.ReportPlanTask);
        grant.RevokedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        var task = await db.CoreWorkTasks.FirstAsync(x => x.ParentWorkTaskId != null && x.Kind == WorkItemKind.Task);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReportPlanTaskAsync(setup.Organization.Id, actor,
            new(root.Id, task.Id, task.Revision, "Running", null, "revoked")));
        Assert.Equal(WorkTaskStatus.Backlog, task.Status);
    }

    [Fact]
    public async Task PlanCreatesEntireHierarchyBeforeWorkAndReplayPreservesIds()
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var root = await service.AddAsync(setup.Organization.Id, actor, Add("Tetris", "plan", null));
        var stored = await db.CoreWorkTasks.SingleAsync();
        stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = Guid.NewGuid(); stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        var request = Plan(root.Id);
        var plan = await service.CreatePlanAsync(setup.Organization.Id, actor, request);
        var replay = await service.CreatePlanAsync(setup.Organization.Id, actor, request);
        Assert.Equal(7, await db.CoreWorkTasks.CountAsync());
        Assert.Equal(plan.Items.Select(x => x.Id), replay.Items.Select(x => x.Id));
        Assert.Equal("Epic", plan.Items.Single(x => x.Id == root.Id).Kind);
        Assert.Equal("Tetris Clone MVP", plan.Items.Single(x => x.Id == root.Id).Title);
        var stories = plan.Items.Where(x => x.Kind == "Story").ToList();
        Assert.Equal(2, stories.Count);
        Assert.All(stories, story => Assert.Equal(root.Id, story.ParentItemId));
        var tasks = plan.Items.Where(x => x.Kind == "Task").ToList();
        Assert.Equal(4, tasks.Count);
        Assert.All(tasks, task => { Assert.Contains(stories, x => x.Id == task.ParentItemId); Assert.NotEmpty(task.AcceptanceCriteria); Assert.Equal("Backlog", task.Status); });
        Assert.True(await db.ApplicationRealtimeOutbox.CountAsync() >= 7);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ActivateAsync(setup.Organization.Id, actor,
            new(tasks[0].Id, tasks[0].Revision, "activate-child")));
    }

    [Fact]
    public async Task PlanEnforcesDependenciesEvidenceAndEpicCompletion()
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var root = await service.AddAsync(setup.Organization.Id, actor, Add("Tetris", "plan", null));
        var stored = await db.CoreWorkTasks.SingleAsync(); var eventId = Guid.NewGuid();
        stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = eventId; stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        var plan = await service.CreatePlanAsync(setup.Organization.Id, actor, Plan(root.Id));
        var tasks = plan.Items.Where(x => x.Kind == "Task").OrderBy(x => x.Rank).ToList();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReportPlanTaskAsync(setup.Organization.Id, actor,
            new(root.Id, tasks[1].Id, tasks[1].Revision, "Running", null, "out-of-order")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAsync(setup.Organization.Id, actor,
            new(root.Id, eventId, plan.RootRevision, "Done", "premature")));
        foreach (var task in tasks)
        {
            var started = await service.ReportPlanTaskAsync(setup.Organization.Id, actor, new(root.Id, task.Id, task.Revision, "Running", null, "start-" + task.Id));
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReportPlanTaskAsync(setup.Organization.Id, actor,
                new(root.Id, task.Id, started.Revision, "Completed", "", "empty-evidence")));
            var done = await service.ReportPlanTaskAsync(setup.Organization.Id, actor,
                new(root.Id, task.Id, started.Revision, "Completed", "Tests passed; source retained.", "done-" + task.Id));
            Assert.Equal("Completed", done.Status);
        }
        var completed = await service.CompleteAsync(setup.Organization.Id, actor,
            new(root.Id, eventId, plan.RootRevision, "Verified deployment URL", "done"));
        Assert.Equal("Completed", completed.Status);
        Assert.All((await service.ListAsync(setup.Organization.Id, actor)).Boards.Single().Items, x => Assert.Equal("Completed", x.Status));
    }

    [Fact]
    public async Task PlanRejectsInvalidPartialPlansAndExpiredClaims()
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var root = await service.AddAsync(setup.Organization.Id, actor, Add("Tetris", "plan", null));
        var invalid = Plan(root.Id) with { Stories = Plan(root.Id).Stories.Take(1).ToArray() };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePlanAsync(setup.Organization.Id, actor, invalid));
        Assert.Equal(1, await db.CoreWorkTasks.CountAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreatePlanAsync(setup.Organization.Id, actor, Plan(root.Id)));
        var stored = await db.CoreWorkTasks.SingleAsync();
        stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = Guid.NewGuid(); stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreatePlanAsync(setup.Organization.Id, actor, Plan(root.Id)));
        Assert.Equal(1, await db.CoreWorkTasks.CountAsync());
    }

    [Fact]
    public async Task FailedEpicMarksActiveStoryAndTaskBlocked()
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var root = await service.AddAsync(setup.Organization.Id, actor, Add("Tetris", "plan", null));
        var stored = await db.CoreWorkTasks.SingleAsync(); var eventId = Guid.NewGuid();
        stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = eventId; stored.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        var plan = await service.CreatePlanAsync(setup.Organization.Id, actor, Plan(root.Id));
        var task = plan.Items.First(x => x.Kind == "Task");
        await service.ReportPlanTaskAsync(setup.Organization.Id, actor, new(root.Id, task.Id, task.Revision, "Running", null, "start"));
        await service.BlockAsync(setup.Organization.Id, actor, new(root.Id, eventId, plan.RootRevision, "Test runner failed", "block"));
        Assert.Equal(WorkTaskStatus.Blocked, (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.Id)).Status);
        Assert.Equal(WorkTaskStatus.Blocked, (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.ParentItemId)).Status);
        var blockedRoot = await db.CoreWorkTasks.SingleAsync(x => x.Id == root.Id);
        await service.RequeueAsync(setup.Organization.Id, actor,
            new(root.Id, blockedRoot.Revision, "resume-plan"));
        Assert.Equal(WorkTaskStatus.Backlog, (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.Id)).Status);
        Assert.Equal(WorkTaskStatus.Backlog,
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.ParentItemId)).Status);
    }

    [Fact]
    public async Task ProviderUpdateAutomaticallyReopensBlockedEpicAndItsActivePlanTickets()
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var now = new DateTimeOffset(2026, 9, 14, 1, 54, 0, TimeSpan.Zero);
        db.AgentInstallations.Add(Installation(setup, now.AddMinutes(-1)));
        await db.SaveChangesAsync();
        var clock = new FixedTimeProvider(now);
        var service = new PersonalTodoService(db, clock);
        var actor = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var root = await service.AddAsync(setup.Organization.Id, actor, Add("Tetris", "provider-recovery", null));
        var stored = await db.CoreWorkTasks.SingleAsync(); var eventId = Guid.NewGuid();
        stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = eventId;
        stored.ClaimExpiresAt = now.AddMinutes(5); await db.SaveChangesAsync();
        var plan = await service.CreatePlanAsync(setup.Organization.Id, actor, Plan(root.Id));
        var task = plan.Items.First(x => x.Kind == "Task");
        await service.ReportPlanTaskAsync(setup.Organization.Id, actor,
            new(root.Id, task.Id, task.Revision, "Running", null, "start"));
        const string reason = "Development is blocked: The requested output-token budget must be between 1 and 32000 for the selected provider.";
        await service.BlockAsync(setup.Organization.Id, actor,
            new(root.Id, eventId, plan.RootRevision, reason, "block"));
        var providerId = Guid.NewGuid();
        db.LlmProviderProfiles.Add(new LlmProviderProfile
        {
            Id = providerId, Name = "vLLM", BaseUrl = "http://localhost:8000/v1",
            DefaultChatModel = "test", IsEnabled = true, CreatedAt = now,
            UpdatedAt = now
        });
        db.LlmProviderProfiles.Add(new LlmProviderProfile
        {
            Id = Guid.NewGuid(), Name = "Unrelated", BaseUrl = "http://localhost:9000/v1",
            DefaultChatModel = "other", IsEnabled = true, CreatedAt = now,
            UpdatedAt = now.AddMinutes(1)
        });
        db.AgentRunLogs.Add(new AgentRunLog
        {
            Id = Guid.NewGuid(), OrganizationId = setup.Organization.Id,
            EmployeeId = setup.Agent.Id, AgentInstallationId = setup.Agent.AgentInstallationId,
            AgentKey = "software-developer", ProviderProfileId = providerId, Model = "test",
            StartedAt = now, CompletedAt = now, Status = "Failed", PromptHash = "test",
            FailureMessage = reason
        });
        clock.Advance(TimeSpan.FromMinutes(2));
        await db.SaveChangesAsync();

        await service.ReconcileAsync();

        Assert.Equal(WorkTaskStatus.Blocked,
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == root.Id)).Status);
        (await db.LlmProviderProfiles.SingleAsync(x => x.Id == providerId)).UpdatedAt = now.AddMinutes(3);
        clock.Advance(TimeSpan.FromMinutes(2));
        await db.SaveChangesAsync();
        await service.ReconcileAsync();

        var reopenedRoot = await db.CoreWorkTasks.SingleAsync(x => x.Id == root.Id);
        Assert.Equal(WorkTaskStatus.Ready, reopenedRoot.Status);
        Assert.Null(reopenedRoot.BlockReason);
        var reopenedTask = await db.CoreWorkTasks.SingleAsync(x => x.Id == task.Id);
        Assert.Equal(WorkTaskStatus.Backlog, reopenedTask.Status);
        Assert.Null(reopenedTask.BlockReason);
        Assert.Equal(WorkTaskStatus.Backlog,
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.ParentItemId)).Status);
        Assert.Contains(await db.AgentPlatformEventOutbox.ToListAsync(), x =>
            x.Status == AgentPlatformEventOutboxStatus.Pending &&
            x.IdempotencyKey.StartsWith($"personal-todo-available:{root.Id:N}:"));
    }

    [Fact]
    public async Task AgentUpdateAutomaticallyReopensItsOlderDevelopmentBlockAndPlanChildren()
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var now = new DateTimeOffset(2026, 9, 14, 1, 54, 0, TimeSpan.Zero);
        db.AgentInstallations.Add(Installation(setup, now.AddMinutes(-1)));
        await db.SaveChangesAsync();
        var clock = new FixedTimeProvider(now);
        var service = new PersonalTodoService(db, clock);
        var actor = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var root = await service.AddAsync(setup.Organization.Id, actor, Add("Tetris", "agent-update-recovery", null));
        var stored = await db.CoreWorkTasks.SingleAsync(); var eventId = Guid.NewGuid();
        stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = eventId;
        stored.ClaimExpiresAt = now.AddMinutes(5); await db.SaveChangesAsync();
        var plan = await service.CreatePlanAsync(setup.Organization.Id, actor, Plan(root.Id));
        var task = plan.Items.First(x => x.Kind == "Task");
        await service.ReportPlanTaskAsync(setup.Organization.Id, actor,
            new(root.Id, task.Id, task.Revision, "Running", null, "start"));
        await service.BlockAsync(setup.Organization.Id, actor,
            new(root.Id, eventId, plan.RootRevision,
                "Development is blocked: Application tests failed.", "block"));

        await service.ReconcileAsync();
        Assert.Equal(WorkTaskStatus.Blocked,
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == root.Id)).Status);

        clock.Advance(TimeSpan.FromMinutes(2));
        (await db.AgentInstallations.SingleAsync(x => x.Id == setup.Agent.AgentInstallationId)).UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.ReconcileAsync();

        var reopenedRoot = await db.CoreWorkTasks.SingleAsync(x => x.Id == root.Id);
        Assert.Equal(WorkTaskStatus.Ready, reopenedRoot.Status);
        Assert.Null(reopenedRoot.BlockReason);
        Assert.Equal(WorkTaskStatus.Backlog,
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.Id)).Status);
        Assert.Equal(WorkTaskStatus.Backlog,
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.ParentItemId)).Status);
        Assert.Contains(await db.AgentPlatformEventOutbox.ToListAsync(), x =>
            x.Status == AgentPlatformEventOutboxStatus.Pending &&
            x.IdempotencyKey.StartsWith($"personal-todo-available:{root.Id:N}:"));
    }

    [Theory]
    [InlineData("agent-failure:v1;code=runtime.transport;retryable=true;diagnosticId=test")]
    [InlineData("agent-failure:v1;code=agent.invalid_operation;diagnosticId=test")]
    [InlineData("agent-failure:v1;code=agent.payload_invalid;diagnosticId=test")]
    public async Task DueAttentionReviewAutomaticallyReopensRecoverableAgentFailureAndPlanChildren(string failure)
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var now = new DateTimeOffset(2026, 9, 14, 3, 20, 0, TimeSpan.Zero);
        db.AgentInstallations.Add(Installation(setup, now.AddMinutes(-10)));
        await db.SaveChangesAsync();
        var clock = new FixedTimeProvider(now);
        var service = new PersonalTodoService(db, clock);
        var actor = new PersonalTodoActor(setup.Agent.Id, setup.Agent.AgentInstallationId);
        var root = await service.AddAsync(setup.Organization.Id, actor, Add("Tetris", "transport-recovery", null));
        var stored = await db.CoreWorkTasks.SingleAsync(); var eventId = Guid.NewGuid();
        stored.Status = WorkTaskStatus.Running; stored.ClaimEventId = eventId;
        stored.ClaimExpiresAt = now.AddMinutes(5); await db.SaveChangesAsync();
        var plan = await service.CreatePlanAsync(setup.Organization.Id, actor, Plan(root.Id));
        var task = plan.Items.First(x => x.Kind == "Task");
        await service.ReportPlanTaskAsync(setup.Organization.Id, actor,
            new(root.Id, task.Id, task.Revision, "Running", null, "start"));
        await service.BlockAsync(setup.Organization.Id, actor,
            new(root.Id, eventId, plan.RootRevision,
                "This task stopped after an execution failure exhausted automatic recovery or could not be retried.", "block"));

        // Repeated failures increase the delay but must never permanently consume recovery.
        for (var index = 0; index < 3; index++)
        {
            var historicalAt = now.AddHours(-1 - index);
            db.AgentWorkItems.Add(new AgentWorkItem
            {
                Id = Guid.NewGuid(), OrganizationId = setup.Organization.Id.ToString("D"),
                AgentInstallationId = setup.Agent.AgentInstallationId!.Value,
                Kind = AgentWorkKind.Event, Name = Wire.PersonalTodoEvents.Available,
                PayloadHash = "historical", CorrelationId = "historical-failure",
                IdempotencyKey = $"personal-todo-available:{root.Id:N}:historical-{index}",
                Status = AgentWorkStatus.DeadLetter, AvailableAt = historicalAt,
                DeadlineAt = historicalAt.AddMinutes(1), MaximumAttempts = 3, AttemptCount = 3,
                CreatedAt = historicalAt, CompletedAt = historicalAt.AddMinutes(1), LastError = failure
            });
        }
        var failedAt = now.AddMinutes(1);
        db.AgentWorkItems.Add(new AgentWorkItem
        {
            Id = Guid.NewGuid(), OrganizationId = setup.Organization.Id.ToString("D"),
            AgentInstallationId = setup.Agent.AgentInstallationId!.Value,
            Kind = AgentWorkKind.Event, Name = Wire.PersonalTodoEvents.Available,
            PayloadHash = "payload", CorrelationId = "transport-failure",
            IdempotencyKey = $"personal-todo-available:{root.Id:N}:failed",
            Status = AgentWorkStatus.DeadLetter, AvailableAt = now, DeadlineAt = failedAt,
            MaximumAttempts = 3, AttemptCount = 3, CreatedAt = now, CompletedAt = failedAt,
            LastError = failure
        });
        db.AgentSchedules.Add(new AgentSchedule
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = setup.Agent.AgentInstallationId.Value,
            ActivationMode = ActivationMode.OnDemand, TickFrequencySeconds = 3600,
            NextAttentionReviewAt = failedAt.AddMinutes(1), MaxRuntimeSeconds = 600,
            MaxRetriesPerTick = 3, OverlapPolicy = OverlapPolicy.Skip, IsEnabled = true
        });
        await db.SaveChangesAsync();

        await service.ReconcileAsync();
        var waitingRoot = await db.CoreWorkTasks.SingleAsync(x => x.Id == root.Id);
        Assert.Equal(WorkTaskStatus.Blocked, waitingRoot.Status);
        Assert.Equal(failedAt.AddMinutes(8), waitingRoot.NextReviewAt);
        Assert.Contains("retry this task automatically", waitingRoot.BlockReason);

        clock.Advance(TimeSpan.FromMinutes(9));

        await service.ReconcileAsync();

        var reopenedRoot = await db.CoreWorkTasks.SingleAsync(x => x.Id == root.Id);
        Assert.Equal(WorkTaskStatus.Ready, reopenedRoot.Status);
        Assert.Null(reopenedRoot.BlockReason);
        Assert.Equal(WorkTaskStatus.Backlog,
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.Id)).Status);
        Assert.Equal(WorkTaskStatus.Backlog,
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == task.ParentItemId)).Status);
        Assert.Contains(await db.AgentPlatformEventOutbox.ToListAsync(), x =>
            x.Status == AgentPlatformEventOutboxStatus.Pending &&
            x.IdempotencyKey.StartsWith($"personal-todo-available:{root.Id:N}:"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private static AgentInstallation Installation(Setup setup, DateTimeOffset updatedAt) => new()
    {
        Id = setup.Agent.AgentInstallationId!.Value,
        PackageVersionId = Guid.NewGuid(),
        BusinessId = setup.Organization.Id.ToString("D"),
        IsEnabled = true,
        RevisionStatus = PluginRevisionStatus.Active,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt
    };

    private static Wire.CreatePersonalWorkPlanRequest Plan(Guid root) => new(root, "Tetris Clone MVP",
    [
        new("rules", "Playable rules", "Implement the game rules", ["Pieces move, rotate and clear lines"],
        [new("grid", "Grid and pieces", "Implement the grid", ["Grid has 10x20 cells"]),
         new("rules-tests", "Rules tests", "Exercise rotations and line clearing", ["Rules tests pass"])]),
        new("delivery", "Verified browser game", "Validate and deploy", ["Game is playable at the returned URL"],
        [new("integration", "Integration checks", "Run game and UI regression tests", ["Integration tests pass"], "Validation"),
         new("deploy", "Deploy game", "Build and health-check isolated deployment", ["URL health check passes"], "Deployment")])
    ], "plan");
}
