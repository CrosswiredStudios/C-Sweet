using System.Data;
using System.Text.Json;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed class WorkOrchestrationService(
    CSweetDbContext db,
    TimeProvider timeProvider) : IWorkOrchestrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkOrchestrationPolicyResponse?> GetPolicyAsync(
        Guid organizationId, Guid boardId, Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var policy = await LoadPolicyAsync(organizationId, boardId, cancellationToken);
        return policy is null ? null : ToPolicyResponse(policy);
    }

    public async Task<Shared.WorkOrchestrationPolicyRevision> SavePolicyRevisionAsync(
        Guid organizationId, Guid boardId, Guid applicationUserId,
        SaveWorkOrchestrationPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireManagerAsync(
            organizationId, boardId, applicationUserId, cancellationToken);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var board = await db.WorkBoards.Include(x => x.Columns)
            .SingleAsync(x => x.Id == boardId && x.OrganizationId == organizationId, cancellationToken);
        var errors = WorkOrchestrationPolicyValidator.Validate(
            request.InitialStageKey, request.MergeMode, request.Concurrency,
            request.Stages, request.Transitions,
            board.Columns.Select(x => x.Id).ToHashSet());
        if (errors.Count > 0) throw new WorkOrchestrationValidationException(errors);

        var policy = await db.WorkOrchestrationPolicies
            .Include(x => x.Revisions)
            .SingleOrDefaultAsync(x => x.BoardId == boardId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (policy is null)
        {
            policy = new WorkOrchestrationPolicy
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = boardId,
                Name = request.Name.Trim(), CreatedAt = now, UpdatedAt = now
            };
            db.WorkOrchestrationPolicies.Add(policy);
        }

        var revision = new WorkOrchestrationPolicyRevision
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = boardId,
            PolicyId = policy.Id, Revision = (policy.Revisions.Count == 0 ? 0 : policy.Revisions.Max(x => x.Revision)) + 1,
            Name = request.Name.Trim(), InitialStageKey = request.InitialStageKey,
            MergeMode = request.MergeMode,
            GlobalConcurrencyLimit = request.Concurrency.Global,
            OrganizationConcurrencyLimit = request.Concurrency.Organization,
            BoardConcurrencyLimit = request.Concurrency.Board,
            DefaultStageConcurrencyLimit = request.Concurrency.DefaultStage,
            DefaultAssigneeConcurrencyLimit = request.Concurrency.DefaultAssignee,
            CreatedAt = now
        };
        revision.Stages = request.Stages.Select(stage => new WorkOrchestrationStage
        {
            Id = Guid.NewGuid(), PolicyRevisionId = revision.Id, Key = stage.Key,
            Name = stage.Name.Trim(), Type = ParseStageType(stage.StageType), ColumnId = stage.ColumnId,
            Instructions = stage.Instructions, InputSchemaJson = stage.InputSchemaJson,
            OutputSchemaJson = stage.OutputSchemaJson, TimeoutSeconds = stage.TimeoutSeconds,
            ConcurrencyLimit = stage.ConcurrencyLimit, MaximumAttempts = stage.RetryPolicy.MaximumAttempts,
            InitialRetryDelaySeconds = stage.RetryPolicy.InitialDelaySeconds,
            MaximumRetryDelaySeconds = stage.RetryPolicy.MaximumDelaySeconds,
            PlatformAction = stage.PlatformAction, IsSuccessfulTerminal = stage.IsSuccessfulTerminal
        }).ToList();
        revision.Transitions = request.Transitions.Select(transition => new WorkOrchestrationTransition
        {
            Id = Guid.NewGuid(), PolicyRevisionId = revision.Id,
            FromStageKey = transition.FromStageKey, OutcomeCode = transition.OutcomeCode,
            ToStageKey = transition.ToStageKey, MaximumTraversals = transition.MaximumTraversals
        }).ToList();
        policy.Name = request.Name.Trim(); policy.UpdatedAt = now;
        db.WorkOrchestrationPolicyRevisions.Add(revision);
        AddEvent(organizationId, boardId, Guid.Empty, null, null, null,
            "policy.revision.created", new
            {
                policyId = policy.Id,
                revisionId = revision.Id,
                revisionNumber = revision.Revision,
                managerId = member.Id,
                request.IdempotencyKey
            });
        await db.SaveChangesAsync(cancellationToken);
        return ToContract(revision);
    }

    public async Task<Shared.WorkOrchestrationPolicyRevision> PublishPolicyRevisionAsync(
        Guid organizationId, Guid boardId, Guid applicationUserId,
        PublishWorkOrchestrationPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireManagerAsync(
            organizationId, boardId, applicationUserId, cancellationToken);
        ValidateIdempotencyKey(request.IdempotencyKey);
        var board = await db.WorkBoards.Include(x => x.Columns)
            .SingleAsync(x => x.Id == boardId && x.OrganizationId == organizationId, cancellationToken);
        var revision = await db.WorkOrchestrationPolicyRevisions
            .Include(x => x.Policy).Include(x => x.Stages).Include(x => x.Transitions)
            .SingleOrDefaultAsync(x => x.Id == request.RevisionId && x.BoardId == boardId, cancellationToken)
            ?? throw new KeyNotFoundException("Policy revision was not found.");
        var errors = ValidateRevision(revision, board.Columns.Select(x => x.Id).ToHashSet());
        if (errors.Count > 0) throw new WorkOrchestrationValidationException(errors);
        var now = timeProvider.GetUtcNow();
        revision.IsPublished = true;
        revision.PublishedAt = now;
        revision.PublishedByOrganizationUserId = member.Id;
        revision.Policy!.PublishedRevisionId = revision.Id;
        revision.Policy.UpdatedAt = now;
        AddEvent(organizationId, boardId, Guid.Empty, null, null, null,
            "policy.revision.published", new
            {
                revision.PolicyId,
                revisionId = revision.Id,
                revisionNumber = revision.Revision,
                managerId = member.Id,
                request.IdempotencyKey
            });
        await db.SaveChangesAsync(cancellationToken);
        return ToContract(revision);
    }

    public async Task<Shared.WorkSprintPreflightResult> PreflightAsync(
        Guid organizationId, Guid boardId, Guid sprintId, Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        var member = await RequireManagerAsync(
            organizationId, boardId, applicationUserId, cancellationToken);
        return await PreflightCoreAsync(organizationId, boardId, sprintId, member.Id, cancellationToken);
    }

    public async Task<Shared.WorkSprintExecutionResponse> StartAsync(
        Guid organizationId, Guid boardId, Guid sprintId, Guid applicationUserId,
        WorkOrchestrationControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var member = await RequireManagerAsync(
            organizationId, boardId, applicationUserId, cancellationToken);
        var existing = await db.WorkSprintExecutions
            .Include(x => x.Items).ThenInclude(x => x.Stages).ThenInclude(x => x.Attempts)
            .SingleOrDefaultAsync(x => x.SprintId == sprintId, cancellationToken);
        if (existing is not null)
            return ToResponse(existing);

        var sprint = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == sprintId && x.BoardId == boardId && x.OrganizationId == organizationId,
            cancellationToken) ?? throw new KeyNotFoundException("Sprint was not found.");
        if (sprint.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The sprint changed since it was loaded.");
        var preflight = await PreflightCoreAsync(
            organizationId, boardId, sprintId, member.Id, cancellationToken);
        if (!preflight.IsValid) throw new WorkOrchestrationValidationException(preflight.Errors);

        var policy = await db.WorkOrchestrationPolicyRevisions.AsNoTracking()
            .Include(x => x.Stages).Include(x => x.Transitions)
            .SingleAsync(x => x.Id == preflight.PolicyRevisionId, cancellationToken);
        var board = await db.WorkBoards.AsNoTracking().SingleAsync(x => x.Id == boardId, cancellationToken);
        var items = await db.CoreWorkTasks.Include(x => x.StageAssignments)
            .Where(x => x.SprintId == sprintId && x.Kind != WorkItemKind.Initiative && x.Kind != WorkItemKind.Epic)
            .OrderByDescending(x => x.Priority).ThenBy(x => x.BoardRank).ThenBy(x => x.CreatedAt).ThenBy(x => x.Identifier)
            .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var execution = new WorkSprintExecution
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = boardId,
            SprintId = sprintId, PolicyRevisionId = policy.Id,
            StartedByOrganizationUserId = member.Id, Status = WorkSprintExecutionStatus.Active,
            PolicySnapshotJson = JsonSerializer.Serialize(ToContract(policy), JsonOptions),
            AssignmentSnapshotJson = JsonSerializer.Serialize(items.SelectMany(item =>
                item.StageAssignments.Select(assignment => new AssignmentSnapshot(
                    item.Id, assignment.StageKey, assignment.PrincipalKind,
                    assignment.OrganizationUserId, assignment.AgentInstallationId,
                    assignment.PlatformAction))), JsonOptions),
            StartedAt = now, UpdatedAt = now
        };
        foreach (var item in items)
        {
            var itemExecution = new WorkItemExecution
            {
                Id = Guid.NewGuid(), SprintExecutionId = execution.Id, WorkItemId = item.Id,
                ItemIdentifier = item.Identifier!, CurrentStageKey = policy.InitialStageKey,
                Status = WorkItemExecutionStatus.Pending, CreatedAt = now, UpdatedAt = now
            };
            execution.Items.Add(itemExecution);
            CreateStageExecution(itemExecution, policy.Stages.Single(x => x.Key == policy.InitialStageKey), item.StageAssignments, board.ManagerOrganizationUserId!.Value, now);
        }
        db.WorkSprintExecutions.Add(execution);
        sprint.Status = WorkSprintStatus.Active; sprint.StartedAt = now; sprint.UpdatedAt = now; sprint.Revision++;
        AddEvent(organizationId, boardId, execution.Id, null, null, null,
            "sprint.execution.started", new { sprintId, policyRevisionId = policy.Id, member.Id, request.IdempotencyKey });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToResponse(execution);
    }

    public async Task<Shared.WorkSprintExecutionResponse?> ControlAsync(
        Guid organizationId, Guid boardId, Guid sprintId, Guid applicationUserId,
        string action, WorkOrchestrationControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var member = await RequireManagerAsync(organizationId, boardId, applicationUserId, cancellationToken);
        var execution = await LoadExecutionAsync(organizationId, boardId, sprintId, cancellationToken);
        if (execution is null) return null;
        if (execution.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The sprint execution changed since it was loaded.");
        var sprint = await db.WorkSprints.SingleAsync(x => x.Id == sprintId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        switch (action.ToLowerInvariant())
        {
            case "pause" when execution.Status == WorkSprintExecutionStatus.Active:
                execution.Status = WorkSprintExecutionStatus.Paused; sprint.Status = WorkSprintStatus.Paused; break;
            case "resume" when execution.Status == WorkSprintExecutionStatus.Paused:
                execution.Status = WorkSprintExecutionStatus.Active; sprint.Status = WorkSprintStatus.Active; break;
            case "cancel" when execution.Status is WorkSprintExecutionStatus.Active or WorkSprintExecutionStatus.Paused:
                execution.Status = WorkSprintExecutionStatus.Cancelled; execution.CancelledAt = now;
                sprint.Status = WorkSprintStatus.Cancelled; sprint.CompletedAt = now;
                foreach (var item in execution.Items.Where(x => x.Status is not (WorkItemExecutionStatus.Completed or WorkItemExecutionStatus.Cancelled)))
                {
                    item.Status = WorkItemExecutionStatus.Cancelled; item.UpdatedAt = now;
                    foreach (var stage in item.Stages.Where(x => x.Status is not (WorkStageExecutionStatus.Completed or WorkStageExecutionStatus.Cancelled)))
                    {
                        stage.Status = WorkStageExecutionStatus.Cancelled; stage.UpdatedAt = now;
                        foreach (var attempt in stage.Attempts.Where(x => x.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running))
                        {
                            attempt.Status = WorkExecutionAttemptStatus.Cancelled; attempt.CompletedAt = now;
                            if (attempt.AgentWorkItemId.HasValue)
                            {
                                var work = await db.AgentWorkItems.SingleOrDefaultAsync(x => x.Id == attempt.AgentWorkItemId, cancellationToken);
                                if (work is not null && work.Status is AgentWorkStatus.Pending or AgentWorkStatus.Leased)
                                    work.Status = AgentWorkStatus.Cancelled;
                            }
                        }
                    }
                }
                foreach (var grant in await db.ScopedActionGrants.Where(x =>
                             x.OrganizationId == organizationId &&
                             x.GrantedBySubjectKind == CSweet.Domain.Security.GrantSubjectKind.AutomationIdentity &&
                             x.GrantedBySubjectId == execution.Id && x.RevokedAt == null)
                         .ToListAsync(cancellationToken))
                {
                    grant.RevokedAt = now;
                    grant.Revision++;
                }
                break;
            default: throw new InvalidOperationException($"Cannot {action} an execution in state {execution.Status}.");
        }
        execution.Revision++; execution.UpdatedAt = now; sprint.Revision++; sprint.UpdatedAt = now;
        AddEvent(organizationId, boardId, execution.Id, null, null, null,
            $"sprint.execution.{action.ToLowerInvariant()}", new { member.Id, request.Reason, request.IdempotencyKey });
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(execution);
    }

    public async Task<Shared.WorkSprintExecutionResponse?> GetExecutionAsync(
        Guid organizationId, Guid boardId, Guid sprintId, Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var execution = await LoadExecutionAsync(organizationId, boardId, sprintId, cancellationToken);
        return execution is null ? null : ToResponse(execution);
    }

    public async Task<Shared.WorkStageExecutionResponse> RetryAsync(
        Guid organizationId, Guid boardId, Guid stageExecutionId, Guid applicationUserId,
        WorkOrchestrationControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var stage = await LoadStageAsync(organizationId, boardId, stageExecutionId, cancellationToken);
        var board = await db.WorkBoards.AsNoTracking().SingleAsync(x =>
            x.Id == boardId && x.OrganizationId == organizationId, cancellationToken);
        var isManager = board.ManagerOrganizationUserId == member.Id;
        var isAssigned = stage.PrincipalKind switch
        {
            WorkOrchestrationPrincipalKind.AgentInstallation =>
                stage.AgentInstallationId == member.AgentInstallationId,
            WorkOrchestrationPrincipalKind.Human => stage.OrganizationUserId == member.Id,
            _ => false
        };
        if (!isManager && !isAssigned)
            throw new UnauthorizedAccessException(
                "Only the exact stage assignee or accountable board manager may request a retry.");
        var replay = await db.WorkOrchestrationEvents.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EventType == "stage.retry.requested" &&
            x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.StageExecutionId != stageExecutionId)
                throw new InvalidOperationException(
                    "The retry idempotency key is already bound to a different stage execution.");
            return ToResponse(stage);
        }
        var workItem = stage.ItemExecution!.WorkItem!;
        if (workItem.AssignmentRevision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The work assignment changed since the blocker was observed.");
        if (stage.Status is not (WorkStageExecutionStatus.Blocked or WorkStageExecutionStatus.Failed))
            throw new InvalidOperationException("Only blocked or failed stages may be retried.");
        var policyStage = await db.WorkOrchestrationStages.AsNoTracking().SingleAsync(x =>
            x.PolicyRevisionId == stage.ItemExecution.SprintExecution!.PolicyRevisionId &&
            x.Key == stage.StageKey, cancellationToken);
        if (stage.Attempts.Count >= policyStage.MaximumAttempts)
            throw new InvalidOperationException("The stage attempt budget is exhausted.");
        if (board.TeamId is { } teamId)
        {
            var viableRoles = await db.TeamMemberships.AsNoTracking().Where(x =>
                    x.TeamId == teamId && x.OrganizationId == organizationId && x.EndedAt == null &&
                    x.OrganizationUser!.IsActive)
                .Select(x => x.OrganizationUser!.Role!.Name).ToListAsync(cancellationToken);
            if (!viableRoles.Any(x => x.Contains("Architect", StringComparison.OrdinalIgnoreCase)) ||
                !viableRoles.Any(x => x.Contains("Developer", StringComparison.OrdinalIgnoreCase)) ||
                !viableRoles.Any(x => x.Contains("Quality", StringComparison.OrdinalIgnoreCase) ||
                                      x.Contains("QA", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "The current software team is not viable for a governed stage retry.");
        }
        var now = timeProvider.GetUtcNow();
        stage.Status = WorkStageExecutionStatus.Pending; stage.LastError = null; stage.RetryAt = now; stage.UpdatedAt = now;
        stage.ItemExecution!.Status = WorkItemExecutionStatus.Pending;
        stage.ItemExecution.BlockedReason = null; stage.ItemExecution.UpdatedAt = now;
        AddEvent(organizationId, boardId, stage.ItemExecution.SprintExecutionId, stage.ItemExecutionId, stage.Id, null,
            "stage.retry.requested", new { member.Id, request.Reason, request.IdempotencyKey }, request.IdempotencyKey);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(stage);
    }

    public async Task<Shared.WorkStageExecutionResponse> CompleteManualAsync(
        Guid organizationId, Guid boardId, Guid stageExecutionId, Guid applicationUserId,
        Shared.CompleteManualWorkStageRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var stage = await LoadStageAsync(organizationId, boardId, stageExecutionId, cancellationToken);
        if ((stage.StageType != WorkOrchestrationStageType.ManualWork &&
             stage.StageType != WorkOrchestrationStageType.MemberExecution) ||
            stage.PrincipalKind != WorkOrchestrationPrincipalKind.Human ||
            stage.OrganizationUserId != member.Id)
            throw new UnauthorizedAccessException("Only the assigned human may complete this manual stage.");
        await CompleteStageAsync(stage, "Completed", request.OutcomeCode, request.Summary,
            request.Output.GetRawText(), member.Id, request.IdempotencyKey, cancellationToken);
        return ToResponse(stage);
    }

    public async Task<Shared.WorkStageExecutionResponse> DecideApprovalAsync(
        Guid organizationId, Guid boardId, Guid stageExecutionId, Guid applicationUserId,
        Shared.DecideWorkApprovalStageRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var member = await RequireManagerAsync(organizationId, boardId, applicationUserId, cancellationToken);
        var stage = await LoadStageAsync(organizationId, boardId, stageExecutionId, cancellationToken);
        if (stage.StageType != WorkOrchestrationStageType.ManagerApproval)
            throw new InvalidOperationException("This is not an approval stage.");
        await CompleteStageAsync(stage, "Completed", request.Approved ? "approved" : "rejected",
            request.Summary, "{}", member.Id, request.IdempotencyKey, cancellationToken);
        return ToResponse(stage);
    }

    private async Task<Shared.WorkSprintPreflightResult> PreflightCoreAsync(
        Guid organizationId, Guid boardId, Guid sprintId, Guid managerId,
        CancellationToken cancellationToken)
    {
        var errors = new List<Shared.WorkOrchestrationValidationError>();
        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == boardId && x.OrganizationId == organizationId, cancellationToken);
        var sprint = await db.WorkSprints.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == sprintId && x.BoardId == boardId && x.OrganizationId == organizationId, cancellationToken);
        if (board is null || sprint is null)
            return new(false, boardId, sprintId, null, [new("sprint.not_found", "Board or sprint was not found.")]);
        if (board.ManagerOrganizationUserId != managerId)
            errors.Add(new("sprint.manager", "Only the assigned board manager may start this sprint."));
        if (sprint.Status != WorkSprintStatus.Planned)
            errors.Add(new("sprint.state", "Only a Planned sprint may be started."));
        if (await db.WorkSprintExecutions.AnyAsync(x => x.BoardId == boardId &&
                (x.Status == WorkSprintExecutionStatus.Active || x.Status == WorkSprintExecutionStatus.Paused), cancellationToken))
            errors.Add(new("sprint.active_exists", "The board already has an Active or Paused sprint."));
        var policyId = await db.WorkOrchestrationPolicies.AsNoTracking().Where(x => x.BoardId == boardId)
            .Select(x => x.PublishedRevisionId).SingleOrDefaultAsync(cancellationToken);
        if (!policyId.HasValue)
            return new(false, boardId, sprintId, null, [.. errors, new("policy.not_published", "Publish an orchestration policy before starting the sprint.")]);
        var policy = await db.WorkOrchestrationPolicyRevisions.AsNoTracking()
            .Include(x => x.Stages).Include(x => x.Transitions)
            .SingleAsync(x => x.Id == policyId.Value, cancellationToken);
        errors.AddRange(ValidateRevision(policy,
            await db.WorkBoardColumns.AsNoTracking().Where(x => x.BoardId == boardId).Select(x => x.Id).ToHashSetAsync(cancellationToken)));
        var items = await db.CoreWorkTasks.AsNoTracking().Include(x => x.StageAssignments)
            .Where(x => x.SprintId == sprintId && x.Kind != WorkItemKind.Initiative && x.Kind != WorkItemKind.Epic)
            .ToListAsync(cancellationToken);
        if (items.Count == 0) errors.Add(new("sprint.empty", "The sprint has no executable work items."));
        var sprintIds = items.Select(x => x.Id).ToHashSet();
        var dependencies = await db.WorkItemDependencies.AsNoTracking()
            .Where(x => sprintIds.Contains(x.WorkItemId)).ToListAsync(cancellationToken);
        var statuses = await db.CoreWorkTasks.AsNoTracking().Where(x =>
                dependencies.Select(d => d.DependsOnWorkItemId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Status, cancellationToken);
        if (HasDependencyCycle(sprintIds, dependencies))
            errors.Add(new("sprint.dependencies_cycle", "Sprint dependencies contain a cycle."));
        foreach (var dependency in dependencies.Where(x => !sprintIds.Contains(x.DependsOnWorkItemId) &&
                     (!statuses.TryGetValue(x.DependsOnWorkItemId, out var status) || status != WorkTaskStatus.Completed)))
            errors.Add(new("item.dependency", "A dependency must be completed or included in the sprint.", dependency.WorkItemId));
        var initialStage = policy.Stages.Single(x => x.Key == policy.InitialStageKey);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.DeliverySpecificationJson))
                errors.Add(new("item.delivery_not_finalized",
                    "Planning-only work must be finalized with a repository and base branch before sprint execution.",
                    item.Id));
            if (item.Status != WorkTaskStatus.Ready)
                errors.Add(new("item.not_ready", "Executable items must be Ready.", item.Id));
            if (!item.AccountableOrganizationUserId.HasValue)
                errors.Add(new("item.accountable_owner", "Executable item lacks an accountable owner.", item.Id));
            else if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                         x.Id == item.AccountableOrganizationUserId &&
                         x.OrganizationId == organizationId && x.IsActive, cancellationToken))
                errors.Add(new("item.accountable_owner", "The accountable owner is no longer active.", item.Id));
            if (item.StageAssignments.Select(x => x.StageKey).Distinct(StringComparer.Ordinal).Count() !=
                item.StageAssignments.Count)
                errors.Add(new("item.assignments", "A stage may have only one assignment.", item.Id));
            if (IsStaffable(initialStage.Type) &&
                item.StageAssignments.All(x => x.StageKey != initialStage.Key))
                errors.Add(new("item.initial_assignment",
                    $"Initial stage '{initialStage.Key}' requires an assignment before the sprint can start.",
                    item.Id, initialStage.Key));
            foreach (var assignment in item.StageAssignments)
            {
                var stage = policy.Stages.SingleOrDefault(x => x.Key == assignment.StageKey);
                if (stage is null) continue;
                if (stage.Type == WorkOrchestrationStageType.AgentExecution)
                {
                    var installation = assignment.AgentInstallationId.HasValue
                        ? await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).SingleOrDefaultAsync(x =>
                            x.Id == assignment.AgentInstallationId && x.IsEnabled &&
                            x.RevisionStatus == PluginRevisionStatus.Active && x.BusinessId == organizationId.ToString(), cancellationToken)
                        : null;
                    if (installation is null || !ProvidesExecutionCapability(installation.PackageVersion?.ManifestJson))
                        errors.Add(new("assignment.agent", "Assigned installation is inactive or does not provide work.execution.run.v1.", item.Id, stage.Key, assignment.Id));
                }
                if (stage.Type == WorkOrchestrationStageType.ManualWork &&
                    (!assignment.OrganizationUserId.HasValue ||
                     !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                         x.Id == assignment.OrganizationUserId && x.OrganizationId == organizationId &&
                         x.IsActive && x.EmployeeType == EmployeeType.Human, cancellationToken)))
                    errors.Add(new("assignment.human", "Manual stage lacks an active human assignment.", item.Id, stage.Key, assignment.Id));
                if (stage.Type == WorkOrchestrationStageType.MemberExecution)
                {
                    if (assignment.PrincipalKind == WorkOrchestrationPrincipalKind.AgentInstallation)
                    {
                        var installation = assignment.AgentInstallationId.HasValue
                            ? await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).SingleOrDefaultAsync(x =>
                                x.Id == assignment.AgentInstallationId && x.IsEnabled &&
                                x.RevisionStatus == PluginRevisionStatus.Active && x.BusinessId == organizationId.ToString(), cancellationToken)
                            : null;
                        if (installation is null || !ProvidesExecutionCapability(installation.PackageVersion?.ManifestJson))
                            errors.Add(new("assignment.agent", "Assigned installation is inactive or does not provide work.execution.run.v1.", item.Id, stage.Key, assignment.Id));
                    }
                    else if (assignment.PrincipalKind == WorkOrchestrationPrincipalKind.Human)
                    {
                        if (!assignment.OrganizationUserId.HasValue ||
                            !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                                x.Id == assignment.OrganizationUserId && x.OrganizationId == organizationId &&
                                x.IsActive && x.EmployeeType == EmployeeType.Human, cancellationToken))
                            errors.Add(new("assignment.human", "Member stage lacks an active human assignment.", item.Id, stage.Key, assignment.Id));
                    }
                    else
                    {
                        errors.Add(new("assignment.member", "Member stage requires a human or agent assignment.", item.Id, stage.Key, assignment.Id));
                    }
                }
            }
        }
        return new(errors.Count == 0, boardId, sprintId, policy.Id, errors);
    }

    private async Task CompleteStageAsync(
        WorkStageExecution stage, string disposition, string outcomeCode, string summary,
        string outputJson, Guid actorId, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (stage.Status is WorkStageExecutionStatus.Completed or WorkStageExecutionStatus.Cancelled)
            throw new InvalidOperationException("The stage is already terminal.");
        var execution = stage.ItemExecution!.SprintExecution!;
        var policy = await db.WorkOrchestrationPolicyRevisions.Include(x => x.Stages).Include(x => x.Transitions)
            .SingleAsync(x => x.Id == execution.PolicyRevisionId, cancellationToken);
        var transition = policy.Transitions.SingleOrDefault(x => x.FromStageKey == stage.StageKey && x.OutcomeCode == outcomeCode)
            ?? throw new InvalidOperationException($"Outcome '{outcomeCode}' is not valid for stage '{stage.StageKey}'.");
        var now = timeProvider.GetUtcNow();
        stage.Status = WorkStageExecutionStatus.Completed; stage.LastOutcomeCode = outcomeCode;
        stage.LastSummary = summary; stage.CompletedAt = now; stage.UpdatedAt = now;
        var next = policy.Stages.Single(x => x.Key == transition.ToStageKey);
        var traversal = stage.ItemExecution.Traversal + (transition.MaximumTraversals.HasValue ? 1 : 0);
        if (transition.MaximumTraversals.HasValue && traversal > transition.MaximumTraversals.Value)
        {
            stage.ItemExecution.Status = WorkItemExecutionStatus.Failed;
            stage.ItemExecution.BlockedReason = "The workflow traversal limit was reached.";
            stage.ItemExecution.WorkItem!.Status = WorkTaskStatus.Failed;
        }
        else
        {
            stage.ItemExecution.Traversal = traversal;
            stage.ItemExecution.CurrentStageKey = next.Key;
            var assignments = DeserializeAssignments(execution.AssignmentSnapshotJson)
                .Where(x => x.WorkItemId == stage.ItemExecution.WorkItemId)
                .Select(x => new WorkItemStageAssignment
                {
                    StageKey = x.StageKey, PrincipalKind = x.PrincipalKind,
                    OrganizationUserId = x.OrganizationUserId, AgentInstallationId = x.AgentInstallationId,
                    PlatformAction = x.PlatformAction
                }).ToList();
            CreateStageExecution(stage.ItemExecution, next, assignments,
                (await db.WorkBoards.AsNoTracking().SingleAsync(x => x.Id == execution.BoardId, cancellationToken)).ManagerOrganizationUserId!.Value, now);
        }
        stage.ItemExecution.UpdatedAt = now; execution.UpdatedAt = now; execution.Revision++;
        AddEvent(execution.OrganizationId, execution.BoardId, execution.Id, stage.ItemExecutionId, stage.Id, null,
            "stage.completed", new { disposition, outcomeCode, summary, outputJson, actorId, idempotencyKey });
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static void CreateStageExecution(
        WorkItemExecution item, WorkOrchestrationStage stage,
        IEnumerable<WorkItemStageAssignment> assignments, Guid managerId, DateTimeOffset now)
    {
        var assignment = assignments.SingleOrDefault(x => x.StageKey == stage.Key);
        var isMissingStaffAssignment = assignment is null && IsStaffable(stage.Type);
        var isHumanMemberStage = stage.Type == WorkOrchestrationStageType.MemberExecution &&
                                 assignment?.PrincipalKind == WorkOrchestrationPrincipalKind.Human;
        var status = isMissingStaffAssignment
            ? WorkStageExecutionStatus.Blocked
            : stage.Type switch
        {
            WorkOrchestrationStageType.ManualWork => WorkStageExecutionStatus.WaitingForHuman,
            WorkOrchestrationStageType.MemberExecution when isHumanMemberStage => WorkStageExecutionStatus.WaitingForHuman,
            WorkOrchestrationStageType.ManagerApproval => WorkStageExecutionStatus.WaitingForApproval,
            _ => WorkStageExecutionStatus.Pending
        };
        var principal = isMissingStaffAssignment
            ? WorkOrchestrationPrincipalKind.Unassigned
            : stage.Type switch
        {
            WorkOrchestrationStageType.Queue or WorkOrchestrationStageType.Terminal => WorkOrchestrationPrincipalKind.PlatformAction,
            WorkOrchestrationStageType.ManagerApproval => WorkOrchestrationPrincipalKind.BoardManager,
            _ => assignment?.PrincipalKind ?? WorkOrchestrationPrincipalKind.PlatformAction
        };
        var execution = new WorkStageExecution
        {
            Id = Guid.NewGuid(), ItemExecutionId = item.Id, StageKey = stage.Key,
            StageType = stage.Type, Traversal = item.Traversal, Status = status,
            PrincipalKind = principal,
            OrganizationUserId = stage.Type == WorkOrchestrationStageType.ManagerApproval ? managerId : assignment?.OrganizationUserId,
            AgentInstallationId = assignment?.AgentInstallationId,
            PlatformAction = assignment?.PlatformAction ?? stage.PlatformAction,
            LastError = isMissingStaffAssignment ? "staffing.assignment_missing" : null,
            CreatedAt = now, UpdatedAt = now
        };
        item.Stages.Add(execution);
        item.CurrentStageKey = stage.Key;
        item.Status = isMissingStaffAssignment
            ? WorkItemExecutionStatus.Blocked
            : stage.Type switch
        {
            WorkOrchestrationStageType.ManualWork => WorkItemExecutionStatus.WaitingForHuman,
            WorkOrchestrationStageType.MemberExecution when isHumanMemberStage => WorkItemExecutionStatus.WaitingForHuman,
            WorkOrchestrationStageType.ManagerApproval => WorkItemExecutionStatus.WaitingForApproval,
            _ => WorkItemExecutionStatus.Pending
        };
        item.BlockedReason = isMissingStaffAssignment ? "staffing.assignment_missing" : null;
        if (isMissingStaffAssignment && item.WorkItem is not null)
            item.WorkItem.Status = WorkTaskStatus.Blocked;
    }

    private static bool IsStaffable(WorkOrchestrationStageType type) =>
        type is WorkOrchestrationStageType.AgentExecution or
            WorkOrchestrationStageType.ManualWork or
            WorkOrchestrationStageType.MemberExecution;

    private async Task<OrganizationUser> RequireManagerAsync(
        Guid organizationId, Guid boardId, Guid applicationUserId, CancellationToken cancellationToken)
    {
        var member = await ResolveMemberAsync(organizationId, applicationUserId, cancellationToken);
        var manager = await db.WorkBoards.AsNoTracking().Where(x => x.Id == boardId && x.OrganizationId == organizationId)
            .Select(x => x.ManagerOrganizationUserId).SingleOrDefaultAsync(cancellationToken);
        if (!manager.HasValue) throw new InvalidOperationException("The board does not have a manager.");
        if (manager != member.Id) throw new UnauthorizedAccessException("Only the assigned board manager may perform this action.");
        return member;
    }

    private async Task<OrganizationUser> ResolveMemberAsync(
        Guid organizationId, Guid applicationUserId, CancellationToken cancellationToken) =>
        await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            (x.ApplicationUserId == applicationUserId || x.AgentInstallationId == applicationUserId) &&
            x.IsActive, cancellationToken)
        ?? throw new UnauthorizedAccessException("The current user is not an active organization member.");

    private async Task<WorkOrchestrationPolicy?> LoadPolicyAsync(
        Guid organizationId, Guid boardId, CancellationToken cancellationToken) =>
        await db.WorkOrchestrationPolicies.AsNoTracking().Include(x => x.Revisions).ThenInclude(x => x.Stages)
            .Include(x => x.Revisions).ThenInclude(x => x.Transitions)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.BoardId == boardId, cancellationToken);

    private async Task<WorkSprintExecution?> LoadExecutionAsync(
        Guid organizationId, Guid boardId, Guid sprintId, CancellationToken cancellationToken) =>
        await db.WorkSprintExecutions.Include(x => x.Items).ThenInclude(x => x.WorkItem)
            .Include(x => x.Items).ThenInclude(x => x.Stages).ThenInclude(x => x.Attempts)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.BoardId == boardId && x.SprintId == sprintId, cancellationToken);

    private async Task<WorkStageExecution> LoadStageAsync(
        Guid organizationId, Guid boardId, Guid stageExecutionId, CancellationToken cancellationToken) =>
        await db.WorkStageExecutions.Include(x => x.Attempts)
            .Include(x => x.ItemExecution)!.ThenInclude(x => x!.WorkItem)
            .Include(x => x.ItemExecution)!.ThenInclude(x => x!.SprintExecution)
            .SingleOrDefaultAsync(x => x.Id == stageExecutionId &&
                x.ItemExecution!.SprintExecution!.OrganizationId == organizationId &&
                x.ItemExecution.SprintExecution.BoardId == boardId, cancellationToken)
        ?? throw new KeyNotFoundException("Stage execution was not found.");

    private static bool HasDependencyCycle(IReadOnlySet<Guid> itemIds, IReadOnlyList<WorkItemDependency> dependencies)
    {
        var graph = dependencies.Where(x => itemIds.Contains(x.DependsOnWorkItemId))
            .GroupBy(x => x.WorkItemId).ToDictionary(x => x.Key, x => x.Select(y => y.DependsOnWorkItemId).ToList());
        var visiting = new HashSet<Guid>(); var visited = new HashSet<Guid>();
        bool Visit(Guid id)
        {
            if (!visiting.Add(id)) return true;
            if (visited.Contains(id)) { visiting.Remove(id); return false; }
            if (graph.TryGetValue(id, out var next) && next.Any(Visit)) return true;
            visiting.Remove(id); visited.Add(id); return false;
        }
        return itemIds.Any(Visit);
    }

    private static bool ProvidesExecutionCapability(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            return document.RootElement.TryGetProperty("provides", out var provides) &&
                provides.ValueKind == JsonValueKind.Array && provides.EnumerateArray().Any(x =>
                    x.TryGetProperty("name", out var name) && name.GetString() == Shared.WorkManagementCapabilityNames.ExecutionRunV1);
        }
        catch (JsonException) { return false; }
    }

    private static IReadOnlyList<Shared.WorkOrchestrationValidationError> ValidateRevision(
        WorkOrchestrationPolicyRevision revision, IReadOnlySet<Guid> columns) =>
        WorkOrchestrationPolicyValidator.Validate(
            revision.InitialStageKey, revision.MergeMode,
            new(revision.GlobalConcurrencyLimit, revision.OrganizationConcurrencyLimit,
                revision.BoardConcurrencyLimit, revision.DefaultStageConcurrencyLimit,
                revision.DefaultAssigneeConcurrencyLimit),
            revision.Stages.Select(ToContract).ToList(),
            revision.Transitions.Select(ToContract).ToList(), columns);

    private static Shared.WorkOrchestrationPolicyRevision ToContract(WorkOrchestrationPolicyRevision revision) => new(
        revision.PolicyId, revision.Id, revision.BoardId, revision.Revision, revision.Name,
        revision.InitialStageKey, revision.MergeMode,
        new(revision.GlobalConcurrencyLimit, revision.OrganizationConcurrencyLimit,
            revision.BoardConcurrencyLimit, revision.DefaultStageConcurrencyLimit,
            revision.DefaultAssigneeConcurrencyLimit),
        revision.Stages.Select(ToContract).ToList(), revision.Transitions.Select(ToContract).ToList(),
        revision.IsPublished, revision.CreatedAt, revision.PublishedAt);

    private static Shared.WorkOrchestrationStageDefinition ToContract(WorkOrchestrationStage stage) => new(
        stage.Key, stage.Name, stage.Type.ToString(), stage.ColumnId, stage.Instructions,
        stage.InputSchemaJson, stage.OutputSchemaJson, stage.TimeoutSeconds, stage.ConcurrencyLimit,
        new(stage.MaximumAttempts, stage.InitialRetryDelaySeconds, stage.MaximumRetryDelaySeconds),
        stage.PlatformAction, stage.IsSuccessfulTerminal);

    private static Shared.WorkOrchestrationTransitionDefinition ToContract(WorkOrchestrationTransition transition) =>
        new(transition.FromStageKey, transition.OutcomeCode, transition.ToStageKey, transition.MaximumTraversals);

    private static WorkOrchestrationPolicyResponse ToPolicyResponse(WorkOrchestrationPolicy policy) =>
        new(policy.Id, policy.BoardId, policy.PublishedRevisionId,
            policy.Revisions.OrderByDescending(x => x.Revision).Select(ToContract).ToList());

    private static Shared.WorkSprintExecutionResponse ToResponse(WorkSprintExecution execution) => new(
        execution.Id, execution.BoardId, execution.SprintId, execution.PolicyRevisionId,
        execution.StartedByOrganizationUserId, execution.Status.ToString(), execution.Revision,
        execution.StartedAt, execution.UpdatedAt, execution.CompletedAt,
        execution.Items.OrderBy(x => x.ItemIdentifier, StringComparer.Ordinal).Select(ToResponse).ToList());

    private static Shared.WorkItemExecutionResponse ToResponse(WorkItemExecution item) => new(
        item.Id, item.WorkItemId, item.ItemIdentifier, item.CurrentStageKey, item.Traversal,
        item.Status.ToString(), item.BlockedReason,
        item.Stages.OrderBy(x => x.CreatedAt).Select(ToResponse).ToList(), item.UpdatedAt);

    private static Shared.WorkStageExecutionResponse ToResponse(WorkStageExecution stage) => new(
        stage.Id, stage.StageKey, stage.StageType.ToString(), stage.Traversal, stage.Status.ToString(),
        stage.PrincipalKind.ToString(), stage.OrganizationUserId, stage.AgentInstallationId,
        stage.PlatformAction, stage.Attempts.Count, stage.LastOutcomeCode, stage.LastSummary,
        stage.LastError, stage.RetryAt, stage.UpdatedAt);

    private void AddEvent(
        Guid organizationId, Guid boardId, Guid sprintExecutionId,
        Guid? itemExecutionId, Guid? stageExecutionId, Guid? attemptId,
        string eventType, object data, string? idempotencyKey = null) =>
        db.WorkOrchestrationEvents.Add(new WorkOrchestrationEvent
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = boardId,
            SprintExecutionId = sprintExecutionId, ItemExecutionId = itemExecutionId,
            StageExecutionId = stageExecutionId, AttemptId = attemptId, EventType = eventType,
            IdempotencyKey = idempotencyKey,
            DataJson = JsonSerializer.Serialize(data, JsonOptions), OccurredAt = timeProvider.GetUtcNow()
        });

    private static WorkOrchestrationStageType ParseStageType(string value) =>
        Enum.TryParse<WorkOrchestrationStageType>(value, out var parsed) ? parsed :
            throw new ArgumentException($"Unknown stage type '{value}'.");

    private static IReadOnlyList<AssignmentSnapshot> DeserializeAssignments(string json) =>
        JsonSerializer.Deserialize<List<AssignmentSnapshot>>(json, JsonOptions) ?? [];

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
            throw new ArgumentException("Idempotency key is required and cannot exceed 160 characters.");
    }

    private sealed record AssignmentSnapshot(
        Guid WorkItemId, string StageKey, WorkOrchestrationPrincipalKind PrincipalKind,
        Guid? OrganizationUserId, Guid? AgentInstallationId, string? PlatformAction);
}

public sealed class WorkOrchestrationValidationException(
    IReadOnlyList<Shared.WorkOrchestrationValidationError> errors)
    : ArgumentException("Work orchestration validation failed.")
{
    public IReadOnlyList<Shared.WorkOrchestrationValidationError> Errors { get; } = errors;
}
