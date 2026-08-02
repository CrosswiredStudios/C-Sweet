using CSweet.Contracts.WorkManagement;
using CSweet.UI.Components.WorkBoards;

namespace CSweet.UnitTests;

public sealed class WorkBoardPresentationTests
{
    [Fact]
    public void ItemsForColumn_GroupsOrdersAndFiltersCards()
    {
        var columnId = Guid.NewGuid();
        var otherColumnId = Guid.NewGuid();
        var later = Item(columnId, "GAME-2", "Polish controls", 20);
        var earlier = Item(columnId, "GAME-1", "Build flight controls", 10);
        var other = Item(otherColumnId, "GAME-3", "Test flight", 1);

        var result = WorkBoardPresentation.ItemsForColumn(
            [later, other, earlier], columnId, "flight");

        var item = Assert.Single(result);
        Assert.Equal(earlier.Id, item.Id);
    }

    [Fact]
    public void MoveRequest_PreservesDestinationBeforeItemAndRevision()
    {
        var item = Item(Guid.NewGuid(), "GAME-1", "Build controls", 10) with { Revision = 7 };
        var targetColumnId = Guid.NewGuid();
        var beforeItemId = Guid.NewGuid();

        var request = WorkBoardPresentation.MoveRequest(item, targetColumnId, beforeItemId);

        Assert.Equal(targetColumnId, request.TargetColumnId);
        Assert.Equal(beforeItemId, request.BeforeItemId);
        Assert.Equal(7, request.ExpectedRevision);
    }

    [Fact]
    public void CanMove_RespectsArchiveAndGrantedTransitionAction()
    {
        var item = Item(Guid.NewGuid(), "GAME-1", "Build controls", 10);
        var done = new WorkBoardColumnResponse(Guid.NewGuid(), "Done", "Done", 1, "Disabled", null);
        var allowed = Detail(false, [WorkItemActions.Complete]);
        var archived = Detail(true, [WorkItemActions.Complete]);

        Assert.True(WorkBoardPresentation.CanMove(allowed, item, done));
        Assert.False(WorkBoardPresentation.CanMove(archived, item, done));
        Assert.False(WorkBoardPresentation.CanMove(Detail(false, []), item, done));
    }

    private static WorkBoardItemResponse Item(Guid columnId, string identifier, string title, long rank) =>
        new(Guid.NewGuid(), Guid.NewGuid(), columnId, null, null, "Task", title, "", "Active", "High",
            null, rank, 1, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        {
            Identifier = identifier
        };

    private static WorkBoardDetailResponse Detail(bool archived, IReadOnlyList<string> actions) =>
        new(
            new WorkBoardSummaryResponse(
                Guid.NewGuid(), Guid.NewGuid(), null, "Board", "", false, archived, false, null,
                0, 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, actions),
            [],
            []);
}
