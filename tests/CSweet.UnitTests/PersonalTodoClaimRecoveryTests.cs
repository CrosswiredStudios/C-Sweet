using System.Text.Json;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalCoordinationSaveDoesNotRestoreReviewDeadlineOnTrackedReadyOrClaimedWork(bool claimed)
    {
        await using var db = CreateDb();
        var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var item = await service.AddAsync(setup.Organization.Id, new(setup.FirstManager.Id, null), Add("Await planning", "terminal-save", setup.Agent.Id));
        var task = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        var now = DateTimeOffset.UtcNow;
        var session = new AgentCoordinationSession
        {
            Id = Guid.NewGuid(), OrganizationId = setup.Organization.Id, SourceKind = "Board", SourceBoardId = Guid.NewGuid(),
            InitiatorOrganizationUserId = setup.Agent.Id, InitiatorInstallationId = setup.Agent.AgentInstallationId!.Value,
            TargetOrganizationUserId = setup.UnrelatedAgent.Id, TargetInstallationId = setup.UnrelatedAgent.AgentInstallationId!.Value,
            Status = AgentCoordinationStatus.Active, CreatedAt = now, UpdatedAt = now
        };
        task.Status = WorkTaskStatus.Running; task.NextReviewAt = now.AddMinutes(15);
        task.PersonalWorkContextJson = JsonSerializer.Serialize(new Wire.PersonalTodoWorkContext(BoardId: session.SourceBoardId, CoordinationSessionId: session.Id), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        db.Add(session); await db.SaveChangesAsync();
        session.Status = AgentCoordinationStatus.Completed; session.CompletedAt = DateTimeOffset.UtcNow;
        if (claimed)
        {
            task.ClaimEventId = Guid.NewGuid(); task.ClaimExpiresAt = now.AddMinutes(5); task.NextReviewAt = null;
        }
        else await new WorkItemMutationEngine(db, TimeProvider.System).WakeCoordinationWaitsAsync(session, default);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        task = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        Assert.Null(task.NextReviewAt);
        Assert.Equal(claimed ? WorkTaskStatus.Running : WorkTaskStatus.Ready, task.Status);
        Assert.Equal(claimed, task.ClaimEventId.HasValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DueReviewNeverRequeuesActivelyClaimedWork(bool eventClaim)
    {
        await using var db = CreateDb();
        var setup = Seed(db); await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var item = await service.AddAsync(setup.Organization.Id, new(setup.FirstManager.Id, null), Add("Current execution", "active-claim", setup.Agent.Id));
        var task = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        task.Status = WorkTaskStatus.Running;
        task.ClaimEventId = eventClaim ? Guid.NewGuid() : null;
        task.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        task.NextReviewAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var revision = task.Revision; var claim = task.ClaimEventId;
        await service.ReconcileAsync();
        db.ChangeTracker.Clear();
        task = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Equal(revision, task.Revision); Assert.Equal(claim, task.ClaimEventId);
    }
}
