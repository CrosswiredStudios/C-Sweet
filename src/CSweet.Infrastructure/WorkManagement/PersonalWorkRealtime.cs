using System.Text.Json;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Notifications;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Application.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    private async Task SaveChangesWithRealtimeAsync(CancellationToken ct)
    {
        var changes = db.ChangeTracker.Entries<WorkTask>().Where(x => x.State is EntityState.Added or EntityState.Modified)
            .Select(x => x.Entity).Where(x => x.BoardId.HasValue).ToList();
        foreach (var item in changes) await QueuePersonalRealtimeAsync(item, ct);
        // The task transition and its UI wake hint commit together.
        await db.SaveChangesAsync(ct);
    }

    private async Task QueuePersonalRealtimeAsync(WorkTask item, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var readers = await db.ScopedActionGrants.AsNoTracking().Where(x => x.OrganizationId == item.OrganizationId &&
            x.ScopeKind == GrantScopeKind.Board && x.ScopeId == item.BoardId && x.Action == PersonalTodoActions.Read &&
            x.SubjectKind == GrantSubjectKind.OrganizationUser && x.RevokedAt == null && (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .Select(x => x.SubjectId).ToListAsync(ct);
        var recipients = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == item.OrganizationId &&
            readers.Contains(x.Id) && x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null).Select(x => x.Id).ToListAsync(ct);
        db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = item.OrganizationId,
            RecipientOrganizationUserIdsJson = JsonSerializer.Serialize(recipients, JsonOptions), EventType = AppRealtimeEvents.WorkBoardChanged,
            Subject = $"organizations/{item.OrganizationId:D}/work/boards/{item.BoardId:D}",
            DataJson = JsonSerializer.Serialize(new { boardId = item.BoardId, itemId = item.Id, changeType = "personal.changed", revision = item.Revision }, JsonOptions),
            Status = ApplicationRealtimeOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now });
    }
}
