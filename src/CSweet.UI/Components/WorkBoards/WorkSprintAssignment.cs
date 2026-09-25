using CSweet.Contracts.WorkManagement;

namespace CSweet.UI.Components.WorkBoards;

public sealed record WorkSprintAssignment(WorkBoardItemResponse Item, Guid? SprintId);
