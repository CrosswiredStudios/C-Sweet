using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class WorkManagementCapabilityHandlerTests
{
    [Fact]
    public async Task BoardReadPreservesTheSamePlanningAndDeliveryAsIndividualItemRead()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var request = await SeedApprovalAsync(db, setup, true);
        Grant(db, setup, WorkBoardActions.Read, GrantScopeKind.Board, request.BoardId);
        Grant(db, setup, WorkItemActions.Read, GrantScopeKind.Board, request.BoardId);
        var board = await db.WorkBoards.SingleAsync();
        board.Key = "GAME"; board.ProfileKey = "game-profile";
        var item = await db.CoreWorkTasks.SingleAsync();
        item.BoardColumnId = Guid.NewGuid(); item.PlanningRevision = 7; item.TypeKey = "game.implementation";
        item.PlanningSpecificationJson = "{\"requirements\":[\"Playable game\"],\"acceptanceCriteria\":[\"Player can win\"]}";
        var repository = Guid.NewGuid();
        item.DeliverySpecificationJson = JsonSerializer.Serialize(new Wire.WorkItemDeliverySpecification(repository,
            ["Playable game"], ["Player can win"], []) { BaseBranch = "main" }, JsonOptions);
        item.ProposalCoordinationSessionId = Guid.NewGuid(); item.ProposalArtifactDigest = "accepted-plan"; item.ProposalItemKey = "phase-one";
        item.AssignedEmployeeId = (await db.CoreOrganizationUsers.FirstAsync(x => x.AgentInstallationId == setup.InstallationId)).Id;
        db.WorkItemStageAssignments.Add(new() { Id = Guid.NewGuid(), StageKey = "implementation", PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation,
            AgentInstallationId = setup.InstallationId, WorkItemId = item.Id });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var session = Session(setup, WorkItemActions.Read);
        var boardResult = await InvokeAsync(handler, session, WorkItemActions.Read, new Wire.WorkBoardReference(request.BoardId));
        Assert.True(boardResult.Succeeded, boardResult.Error);
        var detail = JsonSerializer.Deserialize<Wire.WorkBoardDetail>(boardResult.Payload.ToByteArray(), JsonOptions)!;
        var returned = Assert.Single(detail.Items);
        var itemResult = await InvokeAsync(handler, session, WorkItemActions.Read, new Wire.WorkItemReference(request.BoardId, item.Id));
        Assert.True(itemResult.Succeeded, itemResult.Error);
        var single = JsonSerializer.Deserialize<Wire.WorkItem>(itemResult.Payload.ToByteArray(), JsonOptions)!;
        Assert.Equal(JsonSerializer.Serialize(single, JsonOptions), JsonSerializer.Serialize(returned, JsonOptions));
        Assert.Equal(repository, returned.Delivery!.RepositoryId);
        Assert.Equal("Playable game", Assert.Single(returned.Planning!.Requirements));
        Assert.NotNull(returned.ProposalProvenance); Assert.Single(returned.StageAssignments);
        Assert.Equal("GAME", detail.Board.Key); Assert.Equal(board.ManagerOrganizationUserId, detail.Board.ManagerOrganizationUserId);
        Assert.Equal("game-profile", detail.Board.ProfileKey);
    }
}
