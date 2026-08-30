using CSweet.Api.Auth;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Api.Core;

/// <summary>Human-facing, cross-resource project status and audit projection.</summary>
public static class WorkstreamInspectionEndpoints
{
    public static IEndpointRouteBuilder MapWorkstreamInspectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/core/organizations/{organizationId:guid}/workstreams").RequireAuthorization();
        group.MapGet("/inspection", InspectPortfolioAsync);
        group.MapGet("/{workstreamId:guid}/inspection", InspectAsync);
        group.MapPost("/{workstreamId:guid}/gates/{gateId:guid}/decide", DecideGateAsync);
        return endpoints;
    }

    private static async Task<IResult> InspectPortfolioAsync(
        Guid organizationId, HttpContext http, CSweetDbContext db, CancellationToken token)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        if (!applicationUserId.HasValue) return Results.Forbid();
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive, token);
        if (actor is null) return Results.Forbid();
        var supervised = await db.WorkstreamSupervisionAssignments.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && x.SupervisorOrganizationUserId == actor.Id && x.EndsAt == null)
            .Select(x => x.WorkstreamId).ToListAsync(token);
        var query = db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        if (actor.PermissionLevel < OrganizationPermissionLevel.Manager)
            query = query.Where(x =>
                x.AccountableManagerOrganizationUserId == actor.Id ||
                supervised.Contains(x.Id) ||
                db.WorkstreamTeamAssignments.Any(assignment =>
                    assignment.WorkstreamId == x.Id && assignment.EndsAt == null &&
                    db.TeamMemberships.Any(membership =>
                        membership.OrganizationId == organizationId &&
                        membership.TeamId == assignment.TeamId &&
                        membership.OrganizationUserId == actor.Id &&
                        membership.EndedAt == null)));
        var projects = await query.OrderBy(x => x.Status).ThenBy(x => x.TargetDate).ThenBy(x => x.Name)
            .Select(x => new
            {
                x.Id, x.Name, x.Outcome, Status = x.Status.ToString(), x.LifecycleStage,
                x.ProfileKey, x.ProfileVersion, x.AccountableManagerOrganizationUserId,
                x.TargetDate, x.BudgetAmount, x.BudgetCurrency, x.Revision, x.UpdatedAt,
                ActiveTeams = db.WorkstreamTeamAssignments.Count(team => team.WorkstreamId == x.Id && team.EndsAt == null),
                Boards = db.WorkBoards.Count(board => board.OrganizationId == organizationId && board.WorkstreamId == x.Id),
                OpenItems = db.CoreWorkTasks.Count(item => item.OrganizationId == organizationId &&
                    item.BoardId.HasValue && db.WorkBoards.Any(board => board.Id == item.BoardId && board.WorkstreamId == x.Id) &&
                    item.Status != WorkTaskStatus.Completed && item.Status != WorkTaskStatus.Cancelled),
                PendingGates = db.WorkstreamGates.Count(gate => gate.WorkstreamId == x.Id &&
                    (gate.Status == W.WorkstreamGateStatuses.Pending || gate.Status == W.WorkstreamGateStatuses.Submitted ||
                     gate.Status == W.WorkstreamGateStatuses.ChangesRequired)),
                OpenDecisions = db.WorkstreamDecisions.Count(decision => decision.WorkstreamId == x.Id && decision.Status == W.DecisionStatuses.Pending),
                LatestBuildStatus = db.DeliveryBuilds.Where(build => build.WorkstreamId == x.Id)
                    .OrderByDescending(build => build.CreatedAt).Select(build => build.Status).FirstOrDefault()
            }).ToListAsync(token);
        return Results.Ok(new ProjectPortfolioResponse(
            DateTimeOffset.UtcNow,
            projects.Count,
            projects.Count(x => x.Status is not ("Completed" or "Cancelled")),
            projects.Select(x => new ProjectPortfolioItem(
                x.Id, x.Name, x.Outcome, x.Status, x.LifecycleStage, x.ProfileKey, x.ProfileVersion,
                x.AccountableManagerOrganizationUserId, x.TargetDate, x.BudgetAmount, x.BudgetCurrency,
                x.Revision, x.UpdatedAt, x.ActiveTeams, x.Boards, x.OpenItems, x.PendingGates,
                x.OpenDecisions, x.LatestBuildStatus)).ToList()));
    }

    private static async Task<IResult> DecideGateAsync(
        Guid organizationId, Guid workstreamId, Guid gateId, HumanGateDecisionRequest request,
        HttpContext http, CSweetDbContext db, IAuditEventWriter audit, CancellationToken token)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        if (!applicationUserId.HasValue) return Results.Forbid();
        var actor = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive, token);
        var workstream = await db.Workstreams.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == workstreamId, token);
        var gate = await db.WorkstreamGates.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.WorkstreamId == workstreamId && x.Id == gateId, token);
        if (actor is null || workstream is null || gate is null) return Results.NotFound();
        var requiredRoles = JsonSerializer.Deserialize<IReadOnlyList<string>>(gate.RequiredReviewerRoleKeysJson) ?? [];
        var requiresOwner = requiredRoles.Contains("human-owner", StringComparer.Ordinal);
        var supervisor = await db.WorkstreamSupervisionAssignments.AsNoTracking().AnyAsync(x =>
            x.WorkstreamId == workstreamId && x.SupervisorOrganizationUserId == actor.Id && x.EndsAt == null, token);
        var authorized = requiresOwner
            ? actor.PermissionLevel == OrganizationPermissionLevel.Owner
            : actor.PermissionLevel >= OrganizationPermissionLevel.Manager ||
              workstream.AccountableManagerOrganizationUserId == actor.Id || supervisor;
        if (!authorized) return Results.Forbid();
        if (gate.Revision != request.ExpectedRevision)
            return Results.Conflict(new { error = "stale_gate", currentRevision = gate.Revision });
        if (gate.Status != W.WorkstreamGateStatuses.Submitted)
            return Results.Conflict(new { error = "gate_not_submitted", status = gate.Status });
        var decision = request.Decision.Trim().ToLowerInvariant() switch
        {
            "approved" => W.WorkstreamGateStatuses.Approved,
            "changes-required" => W.WorkstreamGateStatuses.ChangesRequired,
            "rejected" => W.WorkstreamGateStatuses.Rejected,
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(decision) || string.IsNullOrWhiteSpace(request.Rationale) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return Results.BadRequest(new { error = "invalid_gate_decision" });
        if (decision == W.WorkstreamGateStatuses.Approved && request.Findings.Any(x => x.Blocking))
            return Results.BadRequest(new { error = "blocking_findings" });
        var now = DateTimeOffset.UtcNow;
        gate.Status = decision;
        gate.FindingsJson = JsonSerializer.Serialize(request.Findings);
        gate.DecisionRationale = request.Rationale.Trim();
        gate.DecidedByOrganizationUserId = actor.Id;
        gate.DecidedAt = now;
        gate.Revision++;
        var context = new W.AgentWorkContext(organizationId, workstreamId, null, null, null,
            gate.MilestoneId, gate.Id, Guid.NewGuid(), null, workstream.ProfileKey);
        var resourceEvent = new W.GenericResourceEvent(Guid.NewGuid(), now, context, "WorkstreamGate",
            gate.Id, gate.Revision, gate.Key, decision,
            JsonSerializer.SerializeToElement(new { gate.Id, decision, request.Rationale, request.Findings }));
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            EventType = W.WorkstreamEventNames.GateDecidedV1,
            DataJson = JsonSerializer.Serialize(resourceEvent),
            IdempotencyKey = $"{W.WorkstreamEventNames.GateDecidedV1}:{gate.Id:N}:{gate.Revision}",
            Status = AgentPlatformEventOutboxStatus.Pending,
            NextAttemptAt = now, OccurredAt = now
        });
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("workstream.gate.decided", nameof(WorkstreamGateRecord), gate.Id,
            $"{decision}: {request.Rationale}",
            JsonSerializer.Serialize(new { organizationId, workstreamId, actorId = actor.Id, gate.Revision, request.IdempotencyKey }), token);
        return Results.Ok(new { gate.Id, gate.Status, gate.Revision, gate.DecidedAt, gate.DecidedByOrganizationUserId });
    }

    private static async Task<IResult> InspectAsync(
        Guid organizationId, Guid workstreamId, HttpContext http, CSweetDbContext db, CancellationToken token)
    {
        var applicationUserId = http.User.GetApplicationUserId();
        if (!applicationUserId.HasValue) return Results.Forbid();
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId && x.IsActive, token);
        if (actor is null) return Results.Forbid();
        var workstream = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == workstreamId, token);
        if (workstream is null) return Results.NotFound();

        var teamAssignments = await db.WorkstreamTeamAssignments.AsNoTracking().Where(x => x.WorkstreamId == workstreamId)
            .OrderByDescending(x => x.StartsAt).ToListAsync(token);
        var teamIds = teamAssignments.Select(x => x.TeamId).Distinct().ToList();
        var supervision = await db.WorkstreamSupervisionAssignments.AsNoTracking().Where(x => x.WorkstreamId == workstreamId)
            .OrderByDescending(x => x.StartsAt).ToListAsync(token);
        var canInspect = actor.PermissionLevel >= OrganizationPermissionLevel.Manager ||
                         workstream.AccountableManagerOrganizationUserId == actor.Id ||
                         supervision.Any(x => x.SupervisorOrganizationUserId == actor.Id && x.EndsAt == null) ||
                         await db.TeamMemberships.AsNoTracking().AnyAsync(x =>
                             x.OrganizationId == organizationId && teamIds.Contains(x.TeamId) &&
                             x.OrganizationUserId == actor.Id && x.EndedAt == null, token);
        if (!canInspect) return Results.Forbid();
        var teams = await db.OrganizationTeams.AsNoTracking().Where(x => teamIds.Contains(x.Id))
            .Select(x => new { x.Id, x.TeamKey, x.Name, x.LeadOrganizationUserId, x.Revision, x.ArchivedAt }).ToListAsync(token);

        var boards = await db.WorkBoards.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.WorkstreamId == workstreamId)
            .OrderBy(x => x.Name).ToListAsync(token);
        var boardIds = boards.Select(x => x.Id).ToList();
        var workItems = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.BoardId.HasValue && boardIds.Contains(x.BoardId.Value))
            .Select(x => new { x.Id, x.BoardId, x.Identifier, x.Title, Status = x.Status.ToString(), x.TypeKey, x.Priority, x.AssignedEmployeeId,
                x.AssignedAgentInstallationId, x.BlockReason, x.DueDate, x.Revision, x.UpdatedAt }).ToListAsync(token);
        var sprints = await db.WorkSprints.AsNoTracking().Where(x => boardIds.Contains(x.BoardId))
            .Select(x => new { x.Id, x.BoardId, x.Name, Status = x.Status.ToString(), x.StartsAt, x.EndsAt, x.Revision }).ToListAsync(token);

        var artifacts = await db.CoreArtifacts.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.WorkstreamId == workstreamId)
            .Select(x => new { x.Id, x.Title, x.DocumentType, Status = x.DocumentStatus.ToString(), x.LatestRevisionId,
                x.AcceptedRevisionId, x.OriginWorkItemId, x.TeamId, x.UpdatedAt }).ToListAsync(token);
        var artifactIds = artifacts.Select(x => x.Id).ToList();
        var reviews = await db.ArtifactReviews.AsNoTracking().Where(x => artifactIds.Contains(x.ArtifactId))
            .Select(x => new { x.Id, x.ArtifactId, x.RevisionId, x.RevisionDigest, x.RubricTypeKey, x.Disposition,
                x.FindingsJson, x.ReviewerOrganizationUserId, x.CreatedAt }).ToListAsync(token);

        var conversationQuery = db.CoreConversations.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.WorkstreamId == workstreamId);
        if (actor.PermissionLevel < OrganizationPermissionLevel.Manager)
            conversationQuery = conversationQuery.Where(x => x.Participants.Any(p => p.OrganizationUserId == actor.Id && p.LeftAt == null));
        var conversations = await conversationQuery.Select(x => new { x.Id, x.Kind, x.TeamId, x.Title, x.IsPrivate, x.ArchivedAt,
            ParticipantCount = x.Participants.Count(p => p.LeftAt == null),
            MessageCount = x.Messages.Count,
            LastMessageAt = x.Messages.Select(m => (DateTimeOffset?)m.CreatedAt).Max() }).ToListAsync(token);

        var milestones = await db.WorkstreamMilestones.AsNoTracking().Where(x => x.WorkstreamId == workstreamId).OrderBy(x => x.Position).ToListAsync(token);
        var gates = await db.WorkstreamGates.AsNoTracking().Where(x => x.WorkstreamId == workstreamId).OrderBy(x => x.DueAt).ToListAsync(token);
        var decisions = await db.WorkstreamDecisions.AsNoTracking().Where(x => x.WorkstreamId == workstreamId).OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        var builds = await db.DeliveryBuilds.AsNoTracking().Where(x => x.WorkstreamId == workstreamId).OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        var buildIds = builds.Select(x => x.Id).ToList();
        var validations = await db.DeliveryValidations.AsNoTracking().Where(x => x.WorkstreamId == workstreamId || buildIds.Contains(x.BuildId)).ToListAsync(token);
        var previews = await db.PreviewSessions.AsNoTracking().Where(x => x.WorkstreamId == workstreamId).ToListAsync(token);
        var evaluations = await db.EvaluationSessions.AsNoTracking().Where(x => x.WorkstreamId == workstreamId).OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        var releaseReadiness = await db.ReleaseReadinessRecords.AsNoTracking().Where(x => x.WorkstreamId == workstreamId).OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        var media = await db.MediaAssets.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.WorkstreamId == workstreamId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.FileName, x.ContentType, x.SizeBytes, x.Sha256, x.Width, x.Height,
                x.DurationSeconds, x.CreatingAgentInstallationId, x.GenAiJobId, x.TeamId, x.ArtifactId,
                x.WorkItemId, x.BuildId, x.ProvenanceJson, x.CreatedAt }).ToListAsync(token);

        var resourceIds = boardIds.Concat(workItems.Select(x => x.Id)).Concat(artifactIds).Concat(gates.Select(x => x.Id))
            .Concat(decisions.Select(x => x.Id)).Concat(buildIds).Concat(evaluations.Select(x => x.Id))
            .Concat(media.Select(x => x.Id)).Append(workstreamId).Distinct().ToList();
        var workstreamText = workstreamId.ToString("D");
        var audit = await db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                (x.EntityId.HasValue && resourceIds.Contains(x.EntityId.Value) || x.MetadataJson != null && x.MetadataJson.Contains(workstreamText)))
            .OrderByDescending(x => x.Sequence).Take(500)
            .Select(x => new { x.Id, x.Sequence, x.OccurredAt, x.EventType, x.Category, x.Outcome, x.EntityType, x.EntityId,
                x.Summary, x.ActorKind, x.ActorOrganizationUserId, x.ActorDisplayName, x.ActorAgentId, x.ActorInstallationId,
                x.CorrelationId, x.TraceId, x.ParentEventId, x.RecordHash }).ToListAsync(token);

        var definitions = await db.ToolchainAdapterDefinitions.AsNoTracking()
            .Where(x => builds.Select(build => build.ToolchainDefinitionId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, token);
        var projectLink = $"/organizations/{organizationId:D}/projects/{workstreamId:D}";
        var teamResources = teams.Select(team => Resource(team.Id, "Team", team.TeamKey, team.Name,
                team.ArchivedAt.HasValue ? "Archived" : "Active", team.Revision, null, team.LeadOrganizationUserId,
                null, null, null, team.ArchivedAt, $"/organizations/{organizationId:D}/employees", team))
            .Concat(supervision.Select(item => Resource(item.Id, "Supervision", item.RoleKey, item.RoleKey,
                item.EndsAt.HasValue ? "Ended" : "Active", item.Revision, null, item.SupervisorOrganizationUserId,
                null, null, null, item.StartsAt, $"{projectLink}?tab=teams", item))).ToList();
        var workResources = boards.Select(board => Resource(board.Id, "Board", board.ProfileKey, board.Name,
                board.ArchivedAt.HasValue ? "Archived" : "Active", board.Revision, null, board.OwnerOrganizationUserId,
                null, null, null, board.UpdatedAt, $"/organizations/{organizationId:D}/work/boards/{board.Id:D}", board))
            .Concat(workItems.Select(item => Resource(item.Id, "WorkItem", item.TypeKey, item.Title, item.Status,
                item.Revision, null, item.AssignedEmployeeId, item.AssignedAgentInstallationId, null, null,
                item.UpdatedAt, $"/organizations/{organizationId:D}/work/boards/{item.BoardId:D}?item={item.Id:D}", item)))
            .Concat(sprints.Select(item => Resource(item.Id, "Sprint", null, item.Name, item.Status,
                item.Revision, null, null, null, null, null, item.StartsAt,
                $"/organizations/{organizationId:D}/work/boards/{item.BoardId:D}?sprint={item.Id:D}", item))).ToList();
        var documentResources = artifacts.Select(item => Resource(item.Id, "Document", item.DocumentType, item.Title,
                item.Status, null, null, null, null, null, null, item.UpdatedAt,
                $"/organizations/{organizationId:D}/documents?artifact={item.Id:D}", item))
            .Concat(reviews.Select(item => Resource(item.Id, "DocumentReview", item.RubricTypeKey,
                $"Review {item.RevisionId:D}", item.Disposition, null, item.RevisionDigest,
                item.ReviewerOrganizationUserId, null, null, null, item.CreatedAt,
                $"/organizations/{organizationId:D}/documents?artifact={item.ArtifactId:D}&revision={item.RevisionId:D}", item))).ToList();
        var communicationResources = conversations.Select(item => Resource(item.Id, "Conversation", item.Kind.ToString(),
            item.Title ?? "Project conversation", item.ArchivedAt.HasValue ? "Archived" : "Active", null, null,
            null, null, null, null, item.LastMessageAt,
            $"/organizations/{organizationId:D}/communications/{item.Id:D}", item)).ToList();
        var governanceResources = milestones.Select(item => Resource(item.Id, "Milestone", item.Key, item.Name,
                item.LifecycleStage, item.Revision, null, null, null, null, null, item.TargetDate,
                $"{projectLink}?tab=governance&milestone={item.Id:D}", item))
            .Concat(gates.Select(item => Resource(item.Id, "Gate", item.Key, item.Name, item.Status,
                item.Revision, null, item.DecidedByOrganizationUserId ?? item.SubmittedByOrganizationUserId,
                null, null, null, item.DecidedAt ?? item.SubmittedAt ?? item.DueAt,
                $"{projectLink}?tab=governance&gate={item.Id:D}", item)))
            .Concat(decisions.Select(item => Resource(item.Id, "Decision", item.TypeKey, item.Summary, item.Status,
                item.Revision, null, item.DecidedByOrganizationUserId ?? item.RequestedByOrganizationUserId,
                item.RequestedByInstallationId, null, null, item.UpdatedAt,
                $"{projectLink}?tab=governance&decision={item.Id:D}", item))).ToList();
        var evidenceResources = builds.Select(item =>
            {
                definitions.TryGetValue(item.ToolchainDefinitionId, out var definition);
                return Resource(item.Id, "Build", item.RecipeKey, $"{item.RecipeKey} / {item.TargetKey}", item.Status,
                    item.Revision, item.DefinitionDigest, item.RequestedByOrganizationUserId, item.ProviderInstallationId,
                    definition?.ProviderPackageId, definition?.ProviderPackageVersion, item.UpdatedAt,
                    $"{projectLink}?tab=evidence&build={item.Id:D}", item);
            })
            .Concat(validations.Select(item => Resource(item.Id, "Validation", item.TypeKey, item.Summary, item.Status,
                null, null, null, null, null, null, item.CompletedAt ?? item.CreatedAt,
                $"{projectLink}?tab=evidence&validation={item.Id:D}", item)))
            .Concat(previews.Select(item => Resource(item.Id, "Preview", item.Mode, item.Mode, item.Status,
                null, null, item.CreatedByOrganizationUserId, null, null, null, item.CreatedAt,
                $"{projectLink}?tab=evidence&preview={item.Id:D}", item)))
            .Concat(evaluations.Select(item => Resource(item.Id, "Evaluation", item.TypeKey, item.TypeKey, item.Status,
                item.Revision, null, item.CreatedByOrganizationUserId, null, null, null, item.UpdatedAt,
                $"{projectLink}?tab=evidence&evaluation={item.Id:D}", item)))
            .Concat(media.Select(item => Resource(item.Id, "Media", item.ContentType, item.FileName, "Available",
                null, item.Sha256, null, item.CreatingAgentInstallationId, null, null, item.CreatedAt,
                $"{projectLink}?tab=evidence&media={item.Id:D}", item)))
            .Concat(releaseReadiness.Select(item => Resource(item.Id, "ReleaseReadiness", item.TypeKey, item.TypeKey,
                item.Status, item.Revision, null, null, null, null, null, item.UpdatedAt,
                $"{projectLink}?tab=evidence&release={item.Id:D}", item))).ToList();

        return Results.Ok(new ProjectInspectionResponse(
            Resource(workstream.Id, "Workstream", workstream.ProfileKey, workstream.Name, workstream.Status.ToString(),
                workstream.Revision, workstream.ProfileDefinitionDigest, workstream.AccountableManagerOrganizationUserId,
                null, null, null, workstream.UpdatedAt, projectLink, new { workstream.Outcome, workstream.LifecycleStage,
                    workstream.ProfileVersion, workstream.TargetDate, workstream.BudgetAmount, workstream.BudgetCurrency }),
            new ProjectHealthSummary(
                workItems.Count(x => x.Status is not ("Completed" or "Cancelled")),
                workItems.Count(x => x.Status == "Blocked" || !string.IsNullOrWhiteSpace(x.BlockReason)),
                gates.Count(x => x.Status is "Pending" or "Submitted" or "ChangesRequired"),
                decisions.Count(x => x.Status == "Pending"), artifacts.Count(x => x.Status == "InReview"),
                builds.Count(x => x.Status == "Failed"), validations.Count(x => x.Status is "Failed" or "Blocked"),
                releaseReadiness.Any(x => x.Status == "Ready")),
            teamResources, workResources, documentResources, communicationResources, governanceResources,
            evidenceResources,
            audit.Select(item => new ProjectAuditEntry(item.Id, item.Sequence, item.OccurredAt, item.EventType,
                item.Category, item.Outcome, item.EntityType, item.EntityId, item.Summary ?? string.Empty, item.ActorKind,
                item.ActorOrganizationUserId, item.ActorDisplayName, item.ActorAgentId, item.ActorInstallationId,
                item.CorrelationId, item.TraceId, item.ParentEventId, item.RecordHash ?? string.Empty)).ToList(),
            DateTimeOffset.UtcNow));
    }

    private static ProjectInspectionResource Resource(
        Guid id, string resourceType, string? typeKey, string title, string status, long? revision,
        string? sha256, Guid? actorId, Guid? providerInstallationId, string? providerPackageId,
        string? providerPackageVersion, DateTimeOffset? occurredAt, string deepLink, object metadata) =>
        new(id, resourceType, typeKey, title, status, revision, sha256, actorId, providerInstallationId,
            providerPackageId, providerPackageVersion, occurredAt, deepLink,
            JsonSerializer.SerializeToElement(metadata));
}

public sealed record HumanGateDecisionRequest(
    long ExpectedRevision,
    string Decision,
    string Rationale,
    IReadOnlyList<W.ReviewFinding> Findings,
    string IdempotencyKey);
