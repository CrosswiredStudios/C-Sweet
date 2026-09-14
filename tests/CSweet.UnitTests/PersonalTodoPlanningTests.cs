using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
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
    }

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
