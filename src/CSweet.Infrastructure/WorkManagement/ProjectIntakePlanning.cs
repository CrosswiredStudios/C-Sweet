using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Agent.SDK;
using CSweet.Infrastructure.Core;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkItemMutationEngine
{
    public async Task<Wire.PersonalTodoItem> StartProjectIntakeAsync(Guid org, Guid installation, StartProjectIntakeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("A stable start key is required.");
        var intake = await db.ProjectIntakes.SingleOrDefaultAsync(x => x.OrganizationId == org && x.DeveloperInstallationId == installation && x.Id == request.IntakeId, ct)
            ?? throw new KeyNotFoundException("Project request not found.");
        if (intake.RootItemId.HasValue)
            return await MapItemAsync(await db.CoreWorkTasks.SingleAsync(x => x.Id == intake.RootItemId, ct), ct);
        if (intake.Revision != request.ExpectedRevision || intake.Status != "Ready" || intake.TicketOwner != "self")
            throw new InvalidOperationException("The project must be ready and the human must choose agent-created tickets before planning starts.");
        var policy = new ProjectWorkPolicy(db, clock);
        await policy.RequireAsync(org, intake.DeveloperId, intake.BoardId!.Value, ct);
        var owner = await db.CoreOrganizationUsers.SingleAsync(x => x.OrganizationId == org && x.Id == intake.DeveloperId && x.IsActive && x.AgentInstallationId == installation, ct);
        var board = await db.WorkBoards.Include(x => x.Columns).SingleAsync(x => x.Id == intake.BoardId, ct);
        var now = clock.GetUtcNow();
        var context = new Wire.PersonalTodoWorkContext(intake.ProjectId, board.TeamId, board.Id, SourceFingerprint: intake.Id.ToString("N"));
        var root = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, BoardColumnId = board.Columns.Single(x => x.Category == WorkBoardColumnCategory.ToDo).Id,
            AssignedEmployeeId = owner.Id, AssignedAgentInstallationId = installation, CreatedByOrganizationUserId = intake.RequestingHumanId,
            AccountableOrganizationUserId = board.ManagerOrganizationUserId, SourceConversationId = intake.ConversationId, SourceMessageId = intake.SourceMessageId,
            CreationIdempotencyKey = $"project-intake:{intake.Id:N}", Title = intake.Name,
            Description = JsonSerializer.Serialize(new { kind = "csweet-direct-development-v1", request = intake.Goal, environmentId = intake.EnvironmentId }, JsonOptions),
            PersonalWorkContextJson = JsonSerializer.Serialize(context, JsonOptions), Status = WorkTaskStatus.Ready, Priority = WorkTaskPriority.Medium,
            CreatedAt = now, UpdatedAt = now, BoardRank = (await db.CoreWorkTasks.Where(x => x.BoardId == board.Id).Select(x => (long?)x.BoardRank).MaxAsync(ct) ?? 0) + 1024 };
        if (intake.PendingWorkItemId is { } pendingId)
        {
            var pending = await db.CoreWorkTasks.SingleOrDefaultAsync(x => x.Id == pendingId && x.OrganizationId == org &&
                x.AssignedAgentInstallationId == installation && x.SourceMessageId == intake.SourceMessageId && x.ArchivedAt == null, ct)
                ?? throw new InvalidOperationException("The retained queued ticket is no longer available.");
            if (pending.ClaimEventId.HasValue || pending.Status is WorkTaskStatus.Running or WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)
                throw new InvalidOperationException("The queued ticket changed. Review its current state before starting it.");
            var oldBoard = pending.BoardId;
            var oldItems = await db.CoreWorkTasks.Where(x => x.BoardId == oldBoard && x.OrganizationId == org && x.Id != pending.Id).ToListAsync(ct);
            foreach (var child in oldItems.Where(x => ReadPlanSpecification(x)?.PersonalPlan?.RootItemId == pending.Id))
            {
                if (child.Status is WorkTaskStatus.Running or WorkTaskStatus.Completed || child.ClaimEventId.HasValue)
                    throw new InvalidOperationException("The retained hierarchy has execution evidence and cannot be moved through queued setup.");
                child.BoardId = board.Id; child.BoardColumnId = ColumnForStatus(board, child.Status).Id;
                child.PersonalWorkContextJson = root.PersonalWorkContextJson; child.AccountableOrganizationUserId = root.AccountableOrganizationUserId;
                child.Revision++; child.UpdatedAt = now;
            }
            pending.BoardId = board.Id; pending.BoardColumnId = root.BoardColumnId; pending.BoardRank = root.BoardRank;
            pending.PersonalWorkContextJson = root.PersonalWorkContextJson; pending.Status = WorkTaskStatus.Ready;
            pending.AccountableOrganizationUserId = root.AccountableOrganizationUserId; pending.BlockReason = null;
            pending.WaitingReason = null; pending.Revision++; pending.UpdatedAt = now;
            root = pending;
        }
        else db.CoreWorkTasks.Add(root);
        intake.RootItemId = root.Id; intake.Status = "Started"; intake.Revision++; intake.UpdatedAt = now;
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, TargetInstallationId = installation,
            EventType = ProjectIntakeCapabilities.Changed, DataJson = JsonSerializer.Serialize(new ProjectIntakeChanged(intake.Id, intake.Revision)),
            IdempotencyKey = $"project-intake:{intake.Id:N}:{intake.Revision}", OccurredAt = now, NextAttemptAt = now });
        await QueueAvailableAsync(org, owner, board.Id, root.Id, now, ct);
        await SaveChangesWithRealtimeAsync(ct);
        return await MapItemAsync(root, ct);
    }
    /// <summary>Rollout/reconnect conversion for queued requests with no persisted execution exception.</summary>
    public async Task RetainUnstartedProjectRequestsAsync(CancellationToken ct)
    {
        var candidates = await db.CoreWorkTasks.Include(x => x.Board).ThenInclude(x => x!.Columns)
            .Where(x => x.Board != null && x.Board.WorkstreamId == null && x.ArchivedAt == null && x.ParentWorkTaskId == null &&
                x.Status == WorkTaskStatus.Ready && x.ClaimEventId == null && x.AssignedAgentInstallationId != null &&
                x.SourceConversationId != null && x.SourceMessageId != null && x.Description.Contains("csweet-direct-development-v1") &&
                !db.LegacyDevelopmentAuthorizations.Any(a => a.WorkItemId == x.Id))
            .OrderBy(x => x.CreatedAt).Take(100).ToListAsync(ct);
        var policy = new ProjectWorkPolicy(db, clock);
        var setup = new ProjectSetupService(db, clock, policy);
        foreach (var item in candidates)
        {
            var installation = item.AssignedAgentInstallationId!.Value;
            if (!await policy.RequiresProjectAsync(item.OrganizationId, installation, ct) ||
                !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == item.AssignedEmployeeId && x.AgentInstallationId == installation && x.OrganizationId == item.OrganizationId && x.IsActive && x.ArchivedAt == null, ct)) continue;
            await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            await policy.LockAsync(item.OrganizationId, ct);
            if (db.Database.IsRelational()) await db.Entry(item).ReloadAsync(ct);
            if (item.Status != WorkTaskStatus.Ready || item.ClaimEventId.HasValue) continue;
            var source = await db.CoreConversationMessages.SingleOrDefaultAsync(x => x.Id == item.SourceMessageId && x.ConversationId == item.SourceConversationId, ct);
            var turn = await db.ChatTurns.FirstOrDefaultAsync(x => x.OrganizationId == item.OrganizationId && x.UserMessageId == item.SourceMessageId && x.TargetAgentOrganizationUserId == item.AssignedEmployeeId, ct);
            if (source?.SenderOrganizationUserId is not { } human || turn is null || !await db.CoreOrganizationUsers.AnyAsync(x => x.Id == human && x.OrganizationId == item.OrganizationId && x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Human, ct)) continue;
            var intake = await db.ProjectIntakes.SingleOrDefaultAsync(x => x.OrganizationId == item.OrganizationId && x.DeveloperInstallationId == installation && x.SourceMessageId == source.Id, ct);
            if (intake is { Status: "Started" or "Cancelled" }) continue;
            if (intake is null)
            {
                string? goal = null;
                try
                {
                    using var terms = JsonDocument.Parse(item.Description);
                    if (terms.RootElement.ValueKind == JsonValueKind.Object && terms.RootElement.TryGetProperty("request", out var request) && request.ValueKind == JsonValueKind.String)
                        goal = request.GetString();
                }
                catch (JsonException) { /* The original human message remains the authoritative request. */ }
                intake = new() { Id = Guid.NewGuid(), OrganizationId = item.OrganizationId, DeveloperId = item.AssignedEmployeeId!.Value,
                    DeveloperInstallationId = installation, RequestingHumanId = human, ConversationId = source.ConversationId, SourceMessageId = source.Id,
                    SourceChatTurnId = turn.Id, OriginalRequest = source.Content, Name = item.Title[..Math.Min(160, item.Title.Length)],
                    Goal = (goal ?? source.Content)[..Math.Min(6000, (goal ?? source.Content).Length)], TicketOwner = "self",
                    TeamId = await db.TeamMemberships.Where(x => x.OrganizationId == item.OrganizationId && x.OrganizationUserId == item.AssignedEmployeeId).Select(x => (Guid?)x.TeamId).SingleOrDefaultAsync(ct),
                    Issue = "This queued request needs a project before development can start.",
                    IdempotencyKey = $"queued-work:{item.Id:N}", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
                db.ProjectIntakes.Add(intake);
            }
            intake.PendingWorkItemId = item.Id; intake.Revision++; intake.UpdatedAt = clock.GetUtcNow();
            item.Status = WorkTaskStatus.Blocked; item.BoardColumnId = ColumnForStatus(item.Board!, WorkTaskStatus.Blocked).Id;
            item.BlockReason = $"Project setup is required before this queued development request can start. Open {ProjectIntakeService.Summary(intake).SetupUrl}, select a project and assign the developer. The existing ticket will be reused.";
            item.Revision++; item.UpdatedAt = clock.GetUtcNow(); setup.QueueIntake(intake);
            await SaveChangesWithRealtimeAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
    }

}
