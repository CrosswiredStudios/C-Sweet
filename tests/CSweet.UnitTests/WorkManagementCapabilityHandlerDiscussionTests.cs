using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;

namespace CSweet.UnitTests;

public sealed partial class WorkManagementCapabilityHandlerTests
{
    [Fact]
    public async Task CommentReadCannotUseOneBoardsGrantToReadAnotherBoardsTicket()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var visible = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, Name = "Visible" };
        var privateBoard = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, Name = "Private" };
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = privateBoard.Id, Title = "Private ticket" };
        db.AddRange(visible, privateBoard, item);
        db.WorkItemComments.Add(new WorkItemComment { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId,
            WorkItemId = item.Id, Body = "Private discussion", IdempotencyKey = "private" });
        Grant(db, setup, WorkItemActions.ReadComments, GrantScopeKind.Board, visible.Id);
        await db.SaveChangesAsync();
        var result = await InvokeAsync(CreateHandler(db, new TestAuditEventWriter()), Session(setup, WorkItemActions.ReadComments),
            WorkItemActions.ReadComments, new { boardId = visible.Id, itemId = item.Id });
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("Private discussion", result.Error ?? "");
    }
}
