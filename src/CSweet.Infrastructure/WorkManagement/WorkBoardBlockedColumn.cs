using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>
/// Every board can show blocked work. A board provisioned without a Blocked column gets one the first
/// time a card needs it, so a ticket that is waiting on a person never looks like it is still in progress.
/// Moving the card back to a ready column is how a person retries it.
/// </summary>
internal static class WorkBoardBlockedColumn
{
    internal const string Name = "Blocked";

    internal static async Task<WorkBoardColumn> EnsureAsync(
        CSweetDbContext db, Guid boardId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A column added earlier in this unit of work is not in the database yet.
        var pending = db.WorkBoardColumns.Local.FirstOrDefault(x =>
            x.BoardId == boardId && x.Category == WorkBoardColumnCategory.Blocked);
        if (pending is not null) return pending;
        var columns = await db.WorkBoardColumns.Where(x => x.BoardId == boardId)
            .OrderBy(x => x.Position).ToListAsync(cancellationToken);
        var existing = columns.FirstOrDefault(x => x.Category == WorkBoardColumnCategory.Blocked);
        if (existing is not null) return existing;

        // Appending keeps (BoardId, Position) unique without reordering columns people arranged.
        var column = new WorkBoardColumn
        {
            Id = Guid.NewGuid(), BoardId = boardId, Name = Name, Category = WorkBoardColumnCategory.Blocked,
            Position = columns.Select(x => x.Position).DefaultIfEmpty(-1).Max() + 1,
            WipPolicy = WorkBoardWipPolicy.Disabled
        };
        db.WorkBoardColumns.Add(column);
        var board = await db.WorkBoards.SingleOrDefaultAsync(x => x.Id == boardId, cancellationToken);
        if (board is not null)
        {
            board.Revision++;
            board.UpdatedAt = now;
        }
        return column;
    }
}
