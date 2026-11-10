using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class WorkBoardServiceTests
{
    [Theory]
    [InlineData(OrganizationPermissionLevel.Owner, true)]
    [InlineData(OrganizationPermissionLevel.Manager, true)]
    public async Task BoardReadShowsPersonalCommitmentsBoundToItForHumanManagers(
        OrganizationPermissionLevel permission, bool expected)
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var boardId = Assert.Single((await service.ListDirectoryAsync(
            setup.OrganizationId, setup.ApplicationUserId, new())).Boards).Id;
        var viewer = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == setup.OrganizationUserId);
        viewer.PermissionLevel = permission;
        var agentId = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new OrganizationUser { Id = agentId, OrganizationId = setup.OrganizationId,
            DisplayName = "Marcus Vale", EmployeeType = EmployeeType.Agent, CreatedAt = DateTimeOffset.UtcNow, IsActive = true });
        var agenda = Board(setup.OrganizationId, "Marcus Vale's agenda");
        agenda.Kind = WorkBoardKind.Personal; agenda.OwnerOrganizationUserId = agentId; agenda.Key = "AGENDA";
        var otherTeamBoard = Board(setup.OrganizationId, "Another team board"); otherTeamBoard.Key = "OTHER";
        db.WorkBoards.AddRange(agenda, otherTeamBoard);
        string Context(Guid board) => JsonSerializer.Serialize(new { teamId = (Guid?)null, boardId = board, coordinationSessionId = (Guid?)null });
        WorkTask Card(string title, Guid holder, Guid bound, WorkTaskStatus status) => new()
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = holder, Title = title, Status = status,
            AssignedEmployeeId = agentId, PersonalWorkContextJson = Context(bound), UpdatedAt = DateTimeOffset.UtcNow
        };
        var blocked = Card("Get the Producer's project proposal approved", agenda.Id, boardId, WorkTaskStatus.Blocked);
        blocked.BlockReason = "The Producer's project proposal is blocked.";
        var elsewhere = Card("Bound to another board", agenda.Id, otherTeamBoard.Id, WorkTaskStatus.Ready);
        var archived = Card("Archived commitment", agenda.Id, boardId, WorkTaskStatus.Ready);
        archived.ArchivedAt = DateTimeOffset.UtcNow;
        var cancelled = Card("Cancelled commitment", agenda.Id, boardId, WorkTaskStatus.Cancelled);
        var notPersonal = Card("Team ticket that only mentions the board", otherTeamBoard.Id, boardId, WorkTaskStatus.Ready);
        db.CoreWorkTasks.AddRange(blocked, elsewhere, archived, cancelled, notPersonal);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var detail = (await service.GetAsync(setup.OrganizationId, boardId, setup.ApplicationUserId))!;

        Assert.Equal(expected, detail.LinkedCommitments.Count == 1);
        var linked = Assert.Single(detail.LinkedCommitments);
        Assert.Equal(blocked.Id, linked.Id);
        Assert.Equal("Marcus Vale", linked.OwnerDisplayName);
        Assert.Equal("Blocked", linked.Status);
        Assert.Equal(blocked.BlockReason, linked.BlockReason);
        Assert.Null(linked.ConversationId);
        // A commitment is a projection only: it never appears as a ticket on the board.
        Assert.DoesNotContain(detail.Items, x => x.Id == blocked.Id);
    }

    [Theory]
    [InlineData(CSweet.Domain.Communications.AgentCoordinationStatus.Blocked, true, false)]
    [InlineData(CSweet.Domain.Communications.AgentCoordinationStatus.Failed, true, false)]
    [InlineData(CSweet.Domain.Communications.AgentCoordinationStatus.Active, false, true)]
    [InlineData(CSweet.Domain.Communications.AgentCoordinationStatus.Completed, false, false)]
    public async Task LinkedCommitmentOffersRetryAndStopFromItsCollaborationState(
        CSweet.Domain.Communications.AgentCoordinationStatus status, bool canRetry, bool canStop)
    {
        await using var db = CreateDb();
        var setup = SeedOrganization(db, OrganizationPermissionLevel.Owner);
        await db.SaveChangesAsync();
        var service = CreateService(db, new TestAuditEventWriter());
        var boardId = Assert.Single((await service.ListDirectoryAsync(
            setup.OrganizationId, setup.ApplicationUserId, new())).Boards).Id;
        var agentId = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new OrganizationUser { Id = agentId, OrganizationId = setup.OrganizationId,
            DisplayName = "Marcus Vale", EmployeeType = EmployeeType.Agent, CreatedAt = DateTimeOffset.UtcNow, IsActive = true });
        var agenda = Board(setup.OrganizationId, "Marcus Vale's agenda");
        agenda.Kind = WorkBoardKind.Personal; agenda.OwnerOrganizationUserId = agentId; agenda.Key = "AGENDA";
        db.WorkBoards.Add(agenda);
        var session = new CSweet.Domain.Communications.AgentCoordinationSession
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, ConversationId = Guid.NewGuid(),
            SourceKind = "Board", SourceBoardId = boardId, InitiatorOrganizationUserId = agentId,
            InitiatorInstallationId = Guid.NewGuid(), TargetOrganizationUserId = Guid.NewGuid(),
            TargetInstallationId = Guid.NewGuid(), Subject = "Project proposal", Objective = "Prepare the proposal.",
            Status = status, Revision = 4, IdempotencyKey = "session-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.AgentCoordinationSessions.Add(session);
        db.CoreWorkTasks.Add(new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = agenda.Id,
            Title = "Get the Producer's project proposal approved", Status = WorkTaskStatus.Blocked,
            AssignedEmployeeId = agentId, UpdatedAt = DateTimeOffset.UtcNow,
            PersonalWorkContextJson = JsonSerializer.Serialize(new { boardId, coordinationSessionId = session.Id })
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var linked = Assert.Single((await service.GetAsync(setup.OrganizationId, boardId, setup.ApplicationUserId))!.LinkedCommitments);

        Assert.Equal(session.Id, linked.CoordinationSessionId);
        Assert.Equal(session.ConversationId, linked.ConversationId);
        Assert.Equal(4, linked.CoordinationRevision);
        Assert.Equal(canRetry, linked.CanRetry);
        Assert.Equal(canStop, linked.CanStop);
    }
}
