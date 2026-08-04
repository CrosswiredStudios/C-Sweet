using System.Text.Json;
using CSweet.Agent.Contracts.Packaging;
using CSweet.Agent.SDK;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>
/// Assigns authoritative repository IDs to work. It never accepts clone URLs, refs, credentials,
/// provider tokens, or per-agent repository grants.
/// </summary>
public sealed class SoftwareDevelopmentWorkService(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IAgentRuntimeManager runtimeManager) : ISoftwareDevelopmentWorkService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> RequiredAgentCapabilities = new HashSet<string>(
        [
            WorkItemCapabilities.Read,
            WorkItemCapabilities.Start,
            WorkItemCapabilities.Comment,
            WorkItemCapabilities.Complete,
            GitWorkspaceCapabilities.Prepare,
            GitWorkspaceCapabilities.Refresh,
            GitWorkspaceCapabilities.Inspect,
            GitWorkspaceCapabilities.Publish,
            GitWorkspaceCapabilities.Cleanup
        ],
        StringComparer.Ordinal);

    public async Task<IReadOnlyList<SourceControlRepositoryOptionResponse>> ListRepositoriesAsync(
        Guid organizationId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.IsActive,
            cancellationToken);
        if (member is null)
            throw new UnauthorizedAccessException(
                "The current user is not an active organization member.");

        return await (
            from repository in db.SourceControlRepositories.AsNoTracking()
            join connection in db.SourceControlConnections.AsNoTracking()
                on new { repository.OrganizationId, Id = repository.ConnectionId }
                equals new { connection.OrganizationId, connection.Id }
            join policy in db.TeamRepositoryPolicies.AsNoTracking()
                on new { repository.OrganizationId, RepositoryId = repository.Id }
                equals new { policy.OrganizationId, policy.RepositoryId }
            where repository.OrganizationId == organizationId &&
                  repository.Status == SourceControlRepositoryStatus.Ready &&
                  repository.ArchivedAt == null &&
                  policy.DisabledAt == null
            orderby repository.Name
            select new SourceControlRepositoryOptionResponse(
                repository.Id,
                repository.OrganizationId,
                repository.Name,
                connection.Provider.ToString(),
                repository.CanonicalPath,
                repository.DefaultBranch,
                connection.Provider == SourceControlProvider.GitHub
                    ? GitDeliveryKinds.PullRequest
                    : GitDeliveryKinds.BranchOnly,
                repository.IsManaged))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkBoardItemResponse> AssignAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        AssignSoftwareDevelopmentWorkItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");
        ValidateBrief(request.Development);
        var actor = await RequireBoardUpdateAsync(
            organizationId, boardId, applicationUserId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.BoardId == boardId && x.Id == itemId,
            cancellationToken) ?? throw new KeyNotFoundException("The work item was not found.");
        if (item.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                "The work item changed before it could be assigned.");
        if (item.Status == WorkTaskStatus.Running)
            throw new InvalidOperationException("Running development work cannot be reassigned.");

        var replay = await db.WorkItemActivities.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId && x.BoardId == boardId &&
            x.WorkItemId == itemId && x.Action == "work.item.assign" &&
            x.IdempotencyKey == request.IdempotencyKey,
            cancellationToken);
        if (replay)
            return await ToResponseAsync(item, cancellationToken);

        var installation = await RequireInstallationAsync(
            organizationId, request.AssignedInstallationId, cancellationToken);
        await ValidateAgentPackageAsync(installation, cancellationToken);
        var employee = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.AgentInstallationId == installation.Id && x.IsActive,
            cancellationToken) ?? throw new InvalidOperationException(
            "The installation is not linked to an active organization employee.");
        var teamId = await db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Id == boardId)
            .Select(x => x.TeamId)
            .SingleAsync(cancellationToken) ?? throw new InvalidOperationException(
            "The software board must belong to a team.");
        var repository = await db.SourceControlRepositories.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.Id == request.Development.RepositoryId &&
            x.Status == SourceControlRepositoryStatus.Ready &&
            x.ArchivedAt == null,
            cancellationToken) ?? throw new InvalidOperationException(
            "The selected source-control repository is not ready for this business.");
        var policy = await db.TeamRepositoryPolicies.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.TeamId == teamId &&
            x.RepositoryId == repository.Id && x.DisabledAt == null,
            cancellationToken) ?? throw new InvalidOperationException(
            "The repository is not enabled by this team's delivery policy.");
        if (!await db.TeamMemberships.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.TeamId == teamId &&
                x.OrganizationUserId == employee.Id && x.EndedAt == null,
                cancellationToken))
            throw new InvalidOperationException(
                "The selected developer is not an active member of this team.");

        var now = DateTimeOffset.UtcNow;
        await RevokePriorAssignmentGrantsAsync(
            organizationId, item, installation.Id, now, cancellationToken);
        item.AssignedWorkerId = employee.WorkerId;
        item.AssignedEmployeeId = employee.Id;
        item.AssignedAgentInstallationId = installation.Id;
        item.DevelopmentBriefJson = JsonSerializer.Serialize(request.Development, JsonOptions);
        item.AssignmentRevision++;
        item.Revision++;
        item.UpdatedAt = now;

        foreach (var action in new[]
                 {
                     WorkItemActions.Read,
                     WorkItemActions.Start,
                     WorkItemActions.Comment,
                     WorkItemActions.Complete,
                     GitWorkspaceCapabilities.Prepare,
                     GitWorkspaceCapabilities.Refresh,
                     GitWorkspaceCapabilities.Inspect,
                     GitWorkspaceCapabilities.Publish,
                     GitWorkspaceCapabilities.Cleanup
                 })
        {
            db.ScopedActionGrants.Add(new ScopedActionGrant
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                SubjectKind = GrantSubjectKind.AgentInstallation,
                SubjectId = installation.Id,
                Action = action,
                ScopeKind = GrantScopeKind.WorkItem,
                ScopeId = item.Id,
                CanDelegate = false,
                GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
                GrantedBySubjectId = actor.Id,
                GrantedAt = now,
                ExpiresAt = now.AddDays(7)
            });
        }

        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TargetInstallationId = installation.Id,
            EventType = WorkItemEvents.Assigned,
            DataJson = JsonSerializer.Serialize(
                new WorkItemAssignedEvent(
                    boardId, item.Id, item.AssignmentRevision, installation.Id),
                JsonOptions),
            IdempotencyKey = request.IdempotencyKey,
            Status = AgentPlatformEventOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            WorkItemId = item.Id,
            EventType = "item.assigned",
            Action = "work.item.assign",
            ActorKind = GrantSubjectKind.OrganizationUser,
            ActorSubjectId = actor.Id,
            ActorDisplayName = actor.DisplayName,
            IdempotencyKey = request.IdempotencyKey,
            DataJson = JsonSerializer.Serialize(new
            {
                assignedInstallationId = installation.Id,
                repositoryId = repository.Id,
                teamRepositoryPolicyRevision = policy.Revision,
                item.AssignmentRevision
            }, JsonOptions),
            OccurredAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await runtimeManager.EnsureRuntimeQueuedAsync(
            installation.Id,
            $"Development ticket {item.Id:D} assigned.",
            cancellationToken: cancellationToken);
        return ToResponse(item, employee, request.Development);
    }

    public async Task<WorkBoardItemResponse> UnassignAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid applicationUserId,
        UnassignSoftwareDevelopmentWorkItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");
        var actor = await RequireBoardUpdateAsync(
            organizationId, boardId, applicationUserId, cancellationToken);
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.BoardId == boardId && x.Id == itemId,
            cancellationToken) ?? throw new KeyNotFoundException("The work item was not found.");
        if (item.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                "The work item changed before it could be unassigned.");
        if (!item.AssignedAgentInstallationId.HasValue)
            throw new InvalidOperationException("The work item has no developer assignment.");
        if (item.Status == WorkTaskStatus.Running)
            throw new InvalidOperationException("Running development work cannot be unassigned.");

        var installationId = item.AssignedAgentInstallationId.Value;
        var now = DateTimeOffset.UtcNow;
        var grants = await db.ScopedActionGrants.Where(x =>
            x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == installationId &&
            x.ScopeKind == GrantScopeKind.WorkItem && x.ScopeId == item.Id &&
            x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            grant.RevokedAt = now;
            grant.Revision++;
        }
        item.AssignedWorkerId = null;
        item.AssignedEmployeeId = null;
        item.AssignedAgentInstallationId = null;
        item.DevelopmentBriefJson = null;
        item.AssignmentRevision++;
        item.Revision++;
        item.UpdatedAt = now;
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId,
            BoardId = boardId, WorkItemId = item.Id,
            EventType = "item.unassigned", Action = "work.item.unassign",
            ActorKind = GrantSubjectKind.OrganizationUser,
            ActorSubjectId = actor.Id, ActorDisplayName = actor.DisplayName,
            IdempotencyKey = request.IdempotencyKey,
            DataJson = JsonSerializer.Serialize(
                new { priorInstallationId = installationId, item.AssignmentRevision }, JsonOptions),
            OccurredAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(item, null, null);
    }

    private async Task RevokePriorAssignmentGrantsAsync(
        Guid organizationId,
        WorkTask item,
        Guid newInstallationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!item.AssignedAgentInstallationId.HasValue ||
            item.AssignedAgentInstallationId == newInstallationId)
            return;
        var oldGrants = await db.ScopedActionGrants.Where(x =>
            x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == item.AssignedAgentInstallationId &&
            x.ScopeKind == GrantScopeKind.WorkItem && x.ScopeId == item.Id &&
            x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var grant in oldGrants)
        {
            grant.RevokedAt = now;
            grant.Revision++;
        }
    }

    private async Task ValidateAgentPackageAsync(
        AgentInstallation installation,
        CancellationToken cancellationToken)
    {
        var package = await db.AgentPackageVersions.AsNoTracking()
            .SingleAsync(x => x.Id == installation.PackageVersionId, cancellationToken);
        var manifest = JsonSerializer.Deserialize<AgentManifest>(package.ManifestJson, JsonOptions)
            ?? throw new InvalidOperationException("The developer package manifest is invalid.");
        var requested = manifest.Requires.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var missing = RequiredAgentCapabilities.Where(x => !requested.Contains(x)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"The developer package is missing required capabilities: {string.Join(", ", missing)}.");
        if (!string.Equals(
                manifest.Runtime.EnvironmentProfile,
                "software-development-polyglot-v1",
                StringComparison.Ordinal) ||
            !string.Equals(manifest.Runtime.WorkspaceAccess, "ReadWrite", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The developer package must use the confined software-development environment.");
        if (manifest.Runtime.MaximumConcurrentJobs != 1)
            throw new InvalidOperationException(
                "Software Developer installations must execute tickets sequentially.");
    }

    private async Task<OrganizationUser> RequireBoardUpdateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken)
    {
        var member = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId && x.IsActive &&
            x.EmployeeType == EmployeeType.Human,
            cancellationToken) ?? throw new UnauthorizedAccessException(
            "The current user is not an active organization member.");
        var decision = await authorization.AuthorizeAsync(
            organizationId, GrantSubjectKind.OrganizationUser, member.Id,
            WorkItemActions.Update, GrantScopeKind.Board, boardId, cancellationToken);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException(
                "The current user cannot assign work on this board.");
        return member;
    }

    private async Task<AgentInstallation> RequireInstallationAsync(
        Guid organizationId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var businessId = organizationId.ToString("D");
        return await db.AgentInstallations.SingleOrDefaultAsync(x =>
            x.Id == installationId && x.BusinessId == businessId &&
            x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active,
            cancellationToken) ?? throw new KeyNotFoundException(
            "The active agent installation was not found in this organization.");
    }

    private async Task<WorkBoardItemResponse> ToResponseAsync(
        WorkTask item,
        CancellationToken cancellationToken)
    {
        var employee = item.AssignedEmployeeId.HasValue
            ? await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == item.AssignedEmployeeId, cancellationToken)
            : null;
        var development = string.IsNullOrWhiteSpace(item.DevelopmentBriefJson)
            ? null
            : JsonSerializer.Deserialize<SoftwareDevelopmentBrief>(
                item.DevelopmentBriefJson, JsonOptions);
        return ToResponse(item, employee, development);
    }

    private static WorkBoardItemResponse ToResponse(
        WorkTask item,
        OrganizationUser? employee,
        SoftwareDevelopmentBrief? development) => new(
        item.Id, item.BoardId!.Value, item.BoardColumnId!.Value,
        item.ParentWorkTaskId, item.SprintId, item.Kind.ToString(),
        item.Title, item.Description, item.Status.ToString(), item.Priority.ToString(),
        item.EstimatePoints, item.BoardRank, item.Revision, item.DueDate,
        item.CreatedAt, item.UpdatedAt, item.AssignedWorkerId, item.AssignedEmployeeId,
        item.AssignedAgentInstallationId, employee?.DisplayName, development,
        item.AssignmentRevision);

    private static void ValidateBrief(SoftwareDevelopmentBrief brief)
    {
        if (brief.RepositoryId == Guid.Empty)
            throw new ArgumentException("A source-control repository is required.");
        if (!string.Equals(
                brief.EnvironmentProfile,
                "software-development-polyglot-v1",
                StringComparison.Ordinal))
            throw new ArgumentException("The software development environment is not supported.");
        if (brief.Requirements.Count == 0 || brief.AcceptanceCriteria.Count == 0)
            throw new ArgumentException(
                "At least one requirement and one acceptance criterion are required.");
    }
}
