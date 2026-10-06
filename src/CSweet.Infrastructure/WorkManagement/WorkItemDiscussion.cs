using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Ticket discussion notifications are durable wake hints, never execution grants.</summary>
public static class WorkItemDiscussion
{
    public const string Changed = "com.csweet.work.item.discussion.changed.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool Mentions(string body, string name) => !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(body, @"(?<![\w@])@" + Regex.Escape(name) + @"(?=$|[\r\n,:;.!?])", RegexOptions.CultureInvariant);

    // Call before the mutation's SaveChanges: comment, activity and outbox commit together.
    public static async Task QueueAsync(CSweetDbContext db, Guid boardId, WorkItemComment comment,
        string action, CancellationToken token)
    {
        var board = await db.WorkBoards.AsNoTracking().SingleAsync(b => b.Id == boardId && b.OrganizationId == comment.OrganizationId, token);
        if (board.Kind == WorkBoardKind.Personal || board.OwnerOrganizationUserId.HasValue || board.ArchivedAt.HasValue) return;
        var item = await db.CoreWorkTasks.SingleAsync(t => t.Id == comment.WorkItemId && t.BoardId == boardId && t.OrganizationId == comment.OrganizationId, token);
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(p => p.OrganizationId == comment.OrganizationId && p.IsActive).ToListAsync(token);
        var mentions = people.Where(p => comment.DeletedAt is null &&
            comment.Kind is not ("review.result" or "agent.failure" or "discussion.reply") && Mentions(comment.Body, p.DisplayName)).ToList();
        if (mentions.GroupBy(p => p.DisplayName, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ArgumentException("A mentioned name matches more than one active teammate. Use a unique display name before requesting a reply.");
        var assignments = await db.WorkItemStageAssignments.AsNoTracking().Where(a => a.WorkItemId == item.Id).ToListAsync(token);
        var authors = await db.WorkItemComments.AsNoTracking().Where(c => c.WorkItemId == item.Id && c.DeletedAt == null)
            .Select(c => new { c.AuthorKind, c.AuthorSubjectId }).Distinct().ToListAsync(token);
        var deliveryPrefix = $"ticket-discussion:{comment.Id:N}:";
        var previousRecipients = await db.AgentPlatformEventOutbox.AsNoTracking().Where(e =>
            e.OrganizationId == comment.OrganizationId && e.EventType == Changed && e.IdempotencyKey.StartsWith(deliveryPrefix))
            .Select(e => e.TargetInstallationId).Distinct().ToListAsync(token);
        var authorization = new ScopedActionAuthorizationService(db);
        foreach (var person in people.Where(p => p.AgentInstallationId.HasValue))
        {
            var installation = person.AgentInstallationId!.Value;
            if (comment.AuthorKind == GrantSubjectKind.AgentInstallation && comment.AuthorSubjectId == installation ||
                comment.AuthorKind == GrantSubjectKind.OrganizationUser && comment.AuthorSubjectId == person.Id) continue;
            var mentioned = mentions.Any(p => p.Id == person.Id);
            if (!mentioned && person.Id != board.ManagerOrganizationUserId && person.Id != item.AccountableOrganizationUserId &&
                person.Id != item.AssignedEmployeeId && installation != item.AssignedAgentInstallationId &&
                !previousRecipients.Contains(installation) &&
                assignments.All(a => a.OrganizationUserId != person.Id && a.AgentInstallationId != installation) &&
                authors.All(a => a.AuthorSubjectId != (a.AuthorKind == GrantSubjectKind.AgentInstallation ? installation : person.Id))) continue;
            if (!await CanReadAsync(WorkItemActions.Read) || !await CanReadAsync(WorkItemActions.ReadComments)) continue;
            async Task<bool> CanReadAsync(string action) =>
                (await authorization.AuthorizeAsync(comment.OrganizationId, GrantSubjectKind.AgentInstallation, installation,
                    action, GrantScopeKind.WorkItem, item.Id, token)).Allowed ||
                (await authorization.AuthorizeAsync(comment.OrganizationId, GrantSubjectKind.AgentInstallation, installation,
                    action, GrantScopeKind.Board, boardId, token)).Allowed ||
                board.TeamId is { } team && (await authorization.AuthorizeAsync(comment.OrganizationId,
                    GrantSubjectKind.AgentInstallation, installation, action, GrantScopeKind.Team, team, token)).Allowed;
            var key = $"ticket-discussion:{comment.Id:N}:{comment.Revision}:{installation:N}";
            if (db.AgentPlatformEventOutbox.Local.Any(e => e.IdempotencyKey == key) ||
                await db.AgentPlatformEventOutbox.AnyAsync(e => e.IdempotencyKey == key, token)) continue;
            var now = DateTimeOffset.UtcNow;
            db.AgentPlatformEventOutbox.Add(new()
            {
                Id = Guid.NewGuid(), OrganizationId = comment.OrganizationId, TargetInstallationId = installation,
                EventType = Changed, IdempotencyKey = key, OccurredAt = now, NextAttemptAt = now,
                DataJson = JsonSerializer.Serialize(new { boardId, itemId = item.Id, commentId = comment.Id,
                    commentRevision = comment.Revision, action, recipientEmployeeId = person.Id,
                    requiresResponse = mentioned && comment.DeletedAt is null && comment.Kind != "discussion.reply" }, Json)
            });
        }
    }
}
