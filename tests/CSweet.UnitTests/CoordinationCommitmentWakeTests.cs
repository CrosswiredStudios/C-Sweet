using System.Text.Json;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Theory]
    [InlineData("board-ticket")]
    [InlineData("board-ticket-missed")]
    [InlineData("board-ticket-wrong-key")]
    [InlineData("board-ticket-wrong-speaker")]
    [InlineData("board-ticket-wrong-kind")]
    [InlineData("board-ticket-other-item")]
    [InlineData("completed")]
    [InlineData("blocked")]
    [InlineData("missed-wake")]
    [InlineData("waiting-other")]
    [InlineData("foreign-org")]
    [InlineData("foreign-board")]
    [InlineData("foreign-owner")]
    [InlineData("foreign-session")]
    [InlineData("foreign-project")]
    [InlineData("claimed")]
    [InlineData("inactive")]
    [InlineData("old-session")]
    [InlineData("active-session")]
    [InlineData("completed-task")]
    [InlineData("archived-task")]
    public async Task Terminal_coordination_wakes_only_current_owned_waits_and_persists_one_outbox(string scenario)
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var item = await service.AddAsync(setup.Organization.Id, new(setup.FirstManager.Id, null), Add("Await readiness", "wait", setup.Agent.Id));
        foreach (var wake in db.AgentPlatformEventOutbox) wake.Status = AgentPlatformEventOutboxStatus.Published;
        var task = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        var sourceBoard = Guid.NewGuid(); var project = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var session = new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = setup.Organization.Id,
            InitiatorOrganizationUserId = setup.Agent.Id, InitiatorInstallationId = setup.Agent.AgentInstallationId!.Value,
            SourceBoardId = sourceBoard, WorkstreamId = project, SourceKind = "Board",
            Status = scenario == "blocked" ? AgentCoordinationStatus.Blocked : AgentCoordinationStatus.Completed,
            CreatedAt = now.AddMinutes(-3), UpdatedAt = now, CompletedAt = now };
        var context = new Wire.PersonalTodoWorkContext { BoardId = sourceBoard, WorkstreamId = project };
        task.Status = WorkTaskStatus.Running; task.NextReviewAt = now.AddMinutes(15); task.WaitingReason = "Await assessment";
        task.UpdatedAt = now.AddMinutes(-2);
        switch (scenario)
        {
            case "foreign-org": session.OrganizationId = Guid.NewGuid(); break;
            case "foreign-board": session.SourceBoardId = Guid.NewGuid(); break;
            case "foreign-owner": session.InitiatorOrganizationUserId = setup.UnrelatedAgent.Id; break;
            case "foreign-session": context = context with { CoordinationSessionId = Guid.NewGuid() }; break;
            case "foreign-project": session.WorkstreamId = Guid.NewGuid(); break;
            case "waiting-other": task.WaitingOnOrganizationUserId = Guid.NewGuid(); break;
            case "claimed": task.ClaimEventId = Guid.NewGuid(); break;
            case "inactive": setup.Agent.IsActive = false; break;
            case "old-session": session.CompletedAt = now.AddHours(-1); break;
            case "active-session": session.Status = AgentCoordinationStatus.Active; break;
            case "completed-task": task.Status = WorkTaskStatus.Completed; break;
            case "archived-task": task.ArchivedAt = now; break;
        }
        if (scenario.StartsWith("board-ticket", StringComparison.Ordinal))
        {
            context = context with { WorkItemId = Guid.NewGuid(), SourceFingerprint = "exact-owner-amendment" };
            db.AgentCoordinationTurns.Add(new AgentCoordinationTurn { Id = Guid.NewGuid(), SessionId = session.Id,
                SpeakerOrganizationUserId = scenario == "board-ticket-wrong-speaker" ? Guid.NewGuid() : session.InitiatorOrganizationUserId,
                ArtifactKey = scenario == "board-ticket-wrong-key" ? "unrelated-planning" : context.SourceFingerprint,
                Ordinal = 0, CreatedAt = now.AddMinutes(-3) });
            if (scenario == "board-ticket-wrong-kind") session.SourceKind = "Chat";
            if (scenario == "board-ticket-other-item") session.SourceWorkItemId = Guid.NewGuid();
        }
        task.PersonalWorkContextJson = JsonSerializer.Serialize(context, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        db.AgentCoordinationSessions.Add(session); await db.SaveChangesAsync();
        var beforeStatus = task.Status; var revision = task.Revision;
        var engine = new WorkItemMutationEngine(db, TimeProvider.System);
        if (scenario is "missed-wake" or "board-ticket-missed") await engine.RecoverCoordinationWaitsAsync(default);
        else await engine.WakeCoordinationWaitsAsync(session, default);
        await engine.WakeCoordinationWaitsAsync(session, default);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        task = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        var pending = await db.AgentPlatformEventOutbox.Where(x => x.Status == AgentPlatformEventOutboxStatus.Pending).ToListAsync();
        if (scenario is "completed" or "blocked" or "missed-wake" or "board-ticket" or "board-ticket-missed")
        {
            Assert.Equal(WorkTaskStatus.Ready, task.Status); Assert.Null(task.NextReviewAt); Assert.Null(task.WaitingReason);
            Assert.Equal(revision + 1, task.Revision);
            Assert.Equal(setup.Agent.AgentInstallationId, Assert.Single(pending).TargetInstallationId);
        }
        else { Assert.Equal(beforeStatus, task.Status); Assert.Equal(revision, task.Revision); Assert.Empty(pending); }
    }
}

