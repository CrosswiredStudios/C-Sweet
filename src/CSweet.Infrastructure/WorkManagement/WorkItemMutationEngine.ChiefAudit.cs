using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    internal async Task<WorkTask> EnsureChiefAuditCardAsync(OrganizationUser owner, string key, string title,
        Guid? conversation, Guid? message, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql())
        {
            var lockKey = $"chief-work-card:{owner.Id:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey},0))", ct);
        }
        await EnsureBoardAsync(owner.OrganizationId, owner.Id, ct);
        var board = await PersonalBoardAsync(owner.OrganizationId, owner.Id, ct);
        if (board.ArchivedAt is not null) throw new InvalidOperationException("The Chief's audit board is archived.");
        var existing = await db.CoreWorkTasks.SingleOrDefaultAsync(x => x.OrganizationId == owner.OrganizationId &&
            x.CreatedByOrganizationUserId == owner.Id && x.CreationIdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.BoardId != board.Id || existing.AssignedAgentInstallationId != owner.AgentInstallationId || existing.ArchivedAt is not null)
                throw new InvalidOperationException("The Chief's audit card is unavailable.");
            if (transaction is not null) await transaction.CommitAsync(ct);
            return existing;
        }
        var now = clock.GetUtcNow();
        var card = new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = owner.OrganizationId, BoardId = board.Id,
            BoardColumnId = board.Columns.Single(x => x.Category == WorkBoardColumnCategory.ToDo).Id,
            AssignedEmployeeId = owner.Id, AssignedAgentInstallationId = owner.AgentInstallationId,
            CreatedByOrganizationUserId = owner.Id, CreationIdempotencyKey = key, CorrelationId = key,
            Title = title, Description = "Platform-managed Chief of Staff work. Actions, attempts and model usage are recorded against this card.",
            SourceConversationId = conversation, SourceMessageId = message, IsExecutable = false,
            Status = WorkTaskStatus.Ready, CreatedAt = now, UpdatedAt = now,
            BoardRank = (await db.CoreWorkTasks.Where(x => x.BoardId == board.Id).Select(x => (long?)x.BoardRank).MaxAsync(ct) ?? 0) + 1024
        };
        db.CoreWorkTasks.Add(card);
        await QueuePersonalRealtimeAsync(card, ct);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return card;
    }

    internal async Task SetChiefAuditCardStateAsync(WorkTask card, WorkTaskStatus status, string summary, DateTimeOffset now, CancellationToken ct)
    {
        var category = status switch
        {
            WorkTaskStatus.Completed => WorkBoardColumnCategory.Done,
            WorkTaskStatus.Cancelled => WorkBoardColumnCategory.Cancelled,
            WorkTaskStatus.Blocked => WorkBoardColumnCategory.Blocked,
            WorkTaskStatus.Running => WorkBoardColumnCategory.InProgress,
            _ => WorkBoardColumnCategory.ToDo
        };
        card.Status = status;
        card.BoardColumnId = await db.WorkBoardColumns.Where(x => x.BoardId == card.BoardId && x.Category == category).Select(x => x.Id).SingleAsync(ct);
        card.ResultSummary = summary[..Math.Min(summary.Length, 2048)];
        card.ClaimEventId = null; card.ClaimExpiresAt = null;
        card.UpdatedAt = now; card.Revision++;
        await QueuePersonalRealtimeAsync(card, ct);
    }
}
