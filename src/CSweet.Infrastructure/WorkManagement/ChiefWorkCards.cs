using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Platform-owned accounting cards. These links never grant execution authority.</summary>
public static class ChiefWorkCards
{
    public const string Prefix = "chief-work:";

    public static Task<OrganizationUser?> ChiefAsync(CSweetDbContext db, Guid organization, Guid installation, CancellationToken ct) =>
        db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == organization && x.IsActive &&
            x.AgentInstallationId == installation && db.LeadershipAssignments.Any(a =>
                a.OrganizationId == organization && a.OrganizationUserId == x.Id &&
                a.PositionKey == LeadershipPositionKeys.ChiefOfStaff && a.EndsAt == null), ct);

    private static string Key(AgentWorkItem work) => work.SourceType == "chat-turn" && Guid.TryParse(work.SourceId, out var turn)
        ? $"{Prefix}chat:{turn:N}" : $"{Prefix}delivery:{work.Id:N}";

    public static async Task<WorkTask?> PrepareAsync(CSweetDbContext db, AgentWorkItem work, TimeProvider clock, CancellationToken ct)
    {
        if (!Guid.TryParse(work.OrganizationId, out var organization) ||
            await ChiefAsync(db, organization, work.AgentInstallationId, ct) is not { } chief) return null;
        ChatTurn? turn = work.SourceType == "chat-turn" && Guid.TryParse(work.SourceId, out var id)
            ? await db.ChatTurns.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organization &&
                x.TargetAgentOrganizationUserId == chief.Id, ct) : null;
        return await new WorkItemMutationEngine(db, clock).EnsureChiefAuditCardAsync(chief, Key(work),
            turn is not null ? "Respond to conversation" : Title(work.Name), turn?.ConversationId, turn?.UserMessageId, ct);
    }

    public static async Task PrepareChatAsync(CSweetDbContext db, ChatTurn turn, Guid installation, CancellationToken ct)
    {
        if (await ChiefAsync(db, turn.OrganizationId, installation, ct) is not { } chief) return;
        await new WorkItemMutationEngine(db, TimeProvider.System).EnsureChiefAuditCardAsync(chief,
            $"{Prefix}chat:{turn.Id:N}", "Respond to conversation", turn.ConversationId, turn.UserMessageId, ct);
    }

    public static async Task BindAsync(CSweetDbContext db, AgentWorkItem work, AgentWorkAttempt attempt, TimeProvider clock, CancellationToken ct)
    {
        var card = await PrepareAsync(db, work, clock, ct);
        if (card is null) return;
        // The inbox's per-installation claim lock serializes retries. Passive cards are never
        // eligible for personal-queue execution and therefore cannot generate recursive work.
        card.Status = WorkTaskStatus.Running;
        card.BoardColumnId = await db.WorkBoardColumns.Where(x => x.BoardId == card.BoardId &&
            x.Category == CSweet.Domain.WorkManagement.WorkBoardColumnCategory.InProgress).Select(x => x.Id).SingleAsync(ct);
        card.ClaimEventId = work.Id;
        card.ClaimExpiresAt = attempt.LeaseExpiresAt.AddMinutes(2);
        card.NextReviewAt = null;
        card.WaitingReason = null;
        card.ResultSummary = null;
        card.UpdatedAt = clock.GetUtcNow();
        card.Revision++;
        AgentTicketFeedback.RecordClaim(db, card, work.AgentInstallationId, work.Id, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await PersonalWorkAttribution.BindClaimAsync(db, card, work.AgentInstallationId, attempt.Id, clock.GetUtcNow(), ct);
    }

    public static async Task SettleAsync(CSweetDbContext db, AgentWorkItem work, DateTimeOffset now, CancellationToken ct)
    {
        var card = await db.CoreWorkTasks.SingleOrDefaultAsync(x => x.CreationIdempotencyKey == Key(work) &&
            x.AssignedAgentInstallationId == work.AgentInstallationId && !x.IsExecutable, ct);
        if (card is null) return;
        var status = work.Status switch
        {
            AgentWorkStatus.Completed => work.LastError is null &&
                !work.Attempts.Any(x => x.Attempt == work.AttemptCount && x.WorkSucceeded == false) &&
                !db.AgentWorkAttempts.Local.Any(x => x.AgentWorkItemId == work.Id && x.Attempt == work.AttemptCount && x.WorkSucceeded == false)
                    ? WorkTaskStatus.Completed : WorkTaskStatus.Blocked,
            AgentWorkStatus.DeadLetter => WorkTaskStatus.Blocked,
            AgentWorkStatus.Cancelled => WorkTaskStatus.Cancelled,
            _ => WorkTaskStatus.Ready
        };
        var summary = work.LastError ?? (status switch
        {
            WorkTaskStatus.Completed => "The callback completed. Its recorded actions and inference receipts are linked to this card.",
            WorkTaskStatus.Blocked => "The callback reported a failure. Review its recorded attempt.",
            WorkTaskStatus.Cancelled => "The callback was cancelled.",
            _ => "Awaiting delivery or an authorized retry."
        });
        if (card.Status == status && card.ClaimEventId is null && card.ResultSummary == summary) return;
        await new WorkItemMutationEngine(db, TimeProvider.System).SetChiefAuditCardStateAsync(card, status, summary, now, ct);
    }

    public static async Task RequireActiveAsync(CSweetDbContext db, Guid organization, Guid installation, Guid runtime, CancellationToken ct)
    {
        if (await ChiefAsync(db, organization, installation, ct) is null) return;
        var now = DateTimeOffset.UtcNow;
        var attempts = await db.AgentWorkAttempts.Include(x => x.AgentWorkItem).Where(x => x.RuntimeInstanceId == runtime &&
            x.AgentWorkItem!.AgentInstallationId == installation && x.AgentWorkItem.OrganizationId == organization.ToString() &&
            x.AgentWorkItem.Status == AgentWorkStatus.Leased && x.AgentWorkItem.DeadlineAt > now &&
            x.FinishedAt == null && x.LeaseExpiresAt > now).Take(2).ToListAsync(ct);
        if (attempts.Count != 1) throw new InvalidOperationException("chief.work-card.required: Chief actions require one authenticated, card-bound delivery.");
        var attempt = attempts[0];
        var context = await db.WorkExecutionContexts.FindAsync([attempt.Id], ct);
        if (context?.WorkItemId is { } focus && await db.CoreWorkTasks.AnyAsync(x => x.Id == focus &&
            x.OrganizationId == organization && x.AssignedAgentInstallationId == installation && x.ArchivedAt == null &&
            x.Status == WorkTaskStatus.Running && x.Board != null && x.Board.ArchivedAt == null, ct)) return;
        if (context?.RootWorkItemId is { } root && await db.CoreWorkTasks.AnyAsync(x => x.Id == root &&
            !x.IsExecutable && (x.ArchivedAt != null || x.Status == WorkTaskStatus.Cancelled || x.Status == WorkTaskStatus.Blocked), ct))
            throw new InvalidOperationException("chief.work-card.required: The callback's audit card is blocked, cancelled or archived.");
        // Completing a queued commitment may release its focus before the callback returns.
        // Restore that callback's own card before its next tool or model operation.
        await BindAsync(db, attempt.AgentWorkItem!, attempt, TimeProvider.System, ct);
        await db.SaveChangesAsync(ct);
    }

    public static async Task BindInferenceAsync(CSweetDbContext db, AgentRunLog log, Guid runtime, CancellationToken ct)
    {
        if (log.OrganizationId is not { } organization || log.AgentInstallationId is not { } installation ||
            await ChiefAsync(db, organization, installation, ct) is null) return;
        await RequireActiveAsync(db, organization, installation, runtime, ct);
        var now = DateTimeOffset.UtcNow;
        var attempt = await db.AgentWorkAttempts.Include(x => x.AgentWorkItem).SingleAsync(x =>
            x.RuntimeInstanceId == runtime && x.FinishedAt == null && x.LeaseExpiresAt > now &&
            x.AgentWorkItem!.OrganizationId == organization.ToString() && x.AgentWorkItem.AgentInstallationId == installation &&
            x.AgentWorkItem.Status == AgentWorkStatus.Leased && x.AgentWorkItem.DeadlineAt > now, ct);
        if (log.AgentWorkItemId.HasValue && log.AgentWorkItemId != attempt.AgentWorkItemId)
            throw new InvalidOperationException("chief.work-card.required: Inference belongs to a different delivery.");
        await InferenceAttribution.CaptureAsync(db, log, attempt.AgentWorkItemId, ct, attempt.Attempt);
        if (log.WorkItemId is null)
            throw new InvalidOperationException("chief.work-card.required: Inference has no current work-card attribution.");
    }

    public static async Task<WorkTask?> ActionAsync(CSweetDbContext db, Guid organization, Guid installation, string key, string title, CancellationToken ct)
    {
        if (await ChiefAsync(db, organization, installation, ct) is not { } chief) return null;
        return await new WorkItemMutationEngine(db, TimeProvider.System).EnsureChiefAuditCardAsync(chief, Prefix + key, title, null, null, ct);
    }

    public static async Task FinishActionAsync(CSweetDbContext db, WorkTask card, WorkTaskStatus status, string summary, CancellationToken ct)
    {
        await new WorkItemMutationEngine(db, TimeProvider.System).SetChiefAuditCardStateAsync(card, status, summary, DateTimeOffset.UtcNow, ct);
    }

    public static async Task SettleChatAsync(CSweetDbContext db, ChatTurn turn, CancellationToken ct)
    {
        var card = await db.CoreWorkTasks.SingleOrDefaultAsync(x => x.OrganizationId == turn.OrganizationId &&
            x.CreationIdempotencyKey == $"{Prefix}chat:{turn.Id:N}" && !x.IsExecutable, ct);
        if (card is null) return;
        await FinishActionAsync(db, card, turn.Status == ChatTurnStatus.Cancelled ? WorkTaskStatus.Cancelled : WorkTaskStatus.Blocked,
            turn.ErrorMessage ?? "The conversation turn stopped before completing.", ct);
    }

    private static string Title(string name) => name switch
    {
        "com.csweet.agent.attention.review-due.v1" => "Review business activity and recover outstanding work",
        "assistant.converse.v1" => "Respond to an executive request",
        "assistant.plan-work.v1" => "Plan executive work",
        "assistant.summarize-activity.v1" => "Prepare an activity summary",
        _ when name.Contains("onboard", StringComparison.OrdinalIgnoreCase) => "Set up business leadership",
        _ when name.Contains("resource-change", StringComparison.OrdinalIgnoreCase) => "Review and coordinate team staffing",
        _ when name.Contains("incident", StringComparison.OrdinalIgnoreCase) => "Review a business incident",
        _ when name.Contains("coordination", StringComparison.OrdinalIgnoreCase) => "Coordinate with another employee",
        _ => "Review business activity"
    };

    public static void Notify(CSweetDbContext db, WorkTask card, DateTimeOffset now)
    {
        var readers = db.ScopedActionGrants.Where(x => x.OrganizationId == card.OrganizationId &&
            x.ScopeKind == CSweet.Domain.Security.GrantScopeKind.Board && x.ScopeId == card.BoardId &&
            x.Action == CSweet.Contracts.WorkManagement.PersonalTodoActions.Read &&
            x.SubjectKind == CSweet.Domain.Security.GrantSubjectKind.OrganizationUser && x.RevokedAt == null &&
            (x.ExpiresAt == null || x.ExpiresAt > now)).Select(x => x.SubjectId).ToList();
        var recipients = db.CoreOrganizationUsers.Where(x => readers.Contains(x.Id) && x.IsActive && x.ArchivedAt == null &&
            x.OrganizationId == card.OrganizationId && x.EmployeeType == EmployeeType.Human).Select(x => x.Id).ToList();
        db.ApplicationRealtimeOutbox.Add(new CSweet.Domain.Notifications.ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = card.OrganizationId,
            RecipientOrganizationUserIdsJson = System.Text.Json.JsonSerializer.Serialize(recipients),
            EventType = CSweet.Contracts.Realtime.AppRealtimeEvents.WorkBoardChanged,
            Subject = $"organizations/{card.OrganizationId:D}/work/boards/{card.BoardId:D}",
            DataJson = System.Text.Json.JsonSerializer.Serialize(new { boardId = card.BoardId, itemId = card.Id,
                changeType = "personal.changed", revision = card.Revision }), OccurredAt = now, NextAttemptAt = now
        });
    }
}
