using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Security;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Realtime;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

public sealed class WorkManagementCapabilityHandler(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IAuditEventWriter audit,
    IWorkOrchestrationService orchestration) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> HandledCapabilities =
    [
        WorkBoardActions.Read,
        WorkBoardActions.Create,
        WorkBoardActions.Configure,
        WorkBoardActions.ConfigureColumns,
        WorkItemActions.Read,
        WorkItemActions.Create,
        WorkItemActions.Comment,
        WorkItemActions.ReadComments,
        WorkItemActions.Estimate,
        WorkItemActions.Move,
        WorkItemActions.Transfer,
        WorkSprintActions.Read,
        WorkSprintActions.Create,
        WorkSprintActions.ManageScope,
        WorkSprintActions.ManageCapacity,
        WorkSprintActions.CarryOver,
        WorkSprintActions.ReadReports,
        WorkOrchestrationActions.Preflight,
        WorkOrchestrationActions.Read,
        WorkOrchestrationActions.Start,
        WorkOrchestrationActions.Pause,
        WorkOrchestrationActions.Resume,
        WorkOrchestrationActions.Cancel,
        WorkOrchestrationActions.Retry,
        WorkOrchestrationActions.ConfigureSoftwareTemplate,
    ];

    public bool CanHandle(string capability) => HandledCapabilities.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken)
    {
        if (!session.Grant.RequestedCapabilities.Contains(request.Capability))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                $"The installation capability grant does not include '{request.Capability}'.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The agent organization or installation identity is invalid.");
        var installation = await db.AgentInstallations.AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.Id == installationId &&
                x.BusinessId == session.BusinessId &&
                x.IsEnabled &&
                x.RevisionStatus == PluginRevisionStatus.Active,
                cancellationToken);
        if (installation is null)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied,
                "The agent installation is not active in this organization.");

        try
        {
            await RejectPersonalBoardReferencesAsync(organizationId, request, cancellationToken);
            return request.Capability switch
            {
                WorkBoardActions.Read => Success(
                    request.RequestId,
                    await ListBoardsAsync(
                        organizationId, installationId,
                        Read<Wire.WorkBoardListRequest>(request), cancellationToken)),
                WorkItemActions.Read => Success(
                    request.RequestId,
                    await ReadBoardOrItemAsync(
                        organizationId,
                        installationId,
                        request,
                        cancellationToken)),
                WorkBoardActions.Create => Success(
                    request.RequestId,
                    await CreateBoardAsync(
                        session, organizationId, installation,
                        Read<Wire.CreateWorkBoardRequest>(request), cancellationToken)),
                WorkBoardActions.Configure => Success(
                    request.RequestId,
                    await ConfigureBoardAsync(
                        session, organizationId, installation,
                        Read<Wire.ConfigureWorkBoardRequest>(request), cancellationToken)),
                WorkBoardActions.ConfigureColumns => Success(
                    request.RequestId,
                    await ConfigureBoardColumnsAsync(
                        session, organizationId, installation,
                        Read<Wire.ConfigureWorkBoardColumnsRequest>(request), cancellationToken)),
                WorkItemActions.Create => Success(
                    request.RequestId,
                    await CreateItemAsync(
                        session, organizationId, installation,
                        Read<Wire.CreateWorkItemRequest>(request), cancellationToken)),
                WorkItemActions.FinalizeDelivery => Success(
                    request.RequestId,
                    await FinalizeItemDeliveryAsync(
                        session, organizationId, installation,
                        Read<Wire.FinalizeWorkItemDeliveryRequest>(request), cancellationToken)),
                WorkItemActions.Comment => Success(
                    request.RequestId,
                    await CommentItemAsync(
                        session, organizationId, installation,
                        Read<Wire.CommentOnWorkItemRequest>(request), cancellationToken)),
                WorkItemActions.ReadComments => Success(
                    request.RequestId,
                    await ReadCommentsAsync(
                        organizationId, installation,
                        Read<Wire.ReadWorkItemCommentsRequest>(request), cancellationToken)),
                WorkItemActions.Estimate => Success(
                    request.RequestId,
                    await EstimateItemAsync(
                        session, organizationId, installation,
                        Read<Wire.EstimateWorkItemRequest>(request), cancellationToken)),
                WorkItemActions.Move => Success(
                    request.RequestId,
                    await MoveItemAsync(
                        session, organizationId, installation,
                        Read<Wire.MoveWorkItemRequest>(request), cancellationToken)),
                WorkItemActions.Transfer => Success(
                    request.RequestId,
                    await TransferItemAsync(
                        session, organizationId, installation,
                        Read<Wire.TransferWorkItemRequest>(request), cancellationToken)),
                WorkSprintActions.Read => Success(
                    request.RequestId,
                    await ListSprintsAsync(
                        organizationId, installation.Id,
                        Read<Wire.WorkBoardReference>(request), cancellationToken)),
                WorkSprintActions.Create => Success(
                    request.RequestId,
                    await CreateSprintAsync(
                        session, organizationId, installation,
                        Read<Wire.CreateWorkSprintRequest>(request), cancellationToken)),
                WorkSprintActions.ManageScope => Success(
                    request.RequestId,
                    await SetItemSprintAsync(
                        session, organizationId, installation,
                        Read<Wire.SetWorkItemSprintRequest>(request), cancellationToken)),
                WorkSprintActions.ManageCapacity => Success(
                    request.RequestId,
                    await SetSprintCapacityAsync(
                        session, organizationId, installation,
                        Read<Wire.SetWorkSprintCapacityRequest>(request), cancellationToken)),
                WorkSprintActions.CarryOver => Success(
                    request.RequestId,
                    await CarryOverSprintAsync(
                        session, organizationId, installation,
                        Read<Wire.CarryOverWorkSprintRequest>(request), cancellationToken)),
                WorkSprintActions.ReadReports => Success(
                    request.RequestId,
                    await ReadSprintReportAsync(
                        organizationId, installation.Id,
                        Read<Wire.WorkBoardReference>(request), cancellationToken)),
                WorkOrchestrationActions.Preflight => Success(
                    request.RequestId,
                    await PreflightOrchestrationAsync(
                        organizationId, installation.Id,
                        Read<Wire.StartWorkSprintExecutionRequest>(request), cancellationToken)),
                WorkOrchestrationActions.Read => Success(
                    request.RequestId,
                    await ReadOrchestrationAsync(
                        organizationId, installation.Id,
                        Read<Wire.ReadWorkOrchestrationRequest>(request), cancellationToken)),
                WorkOrchestrationActions.Start => Success(
                    request.RequestId,
                    await StartOrchestrationAsync(
                        organizationId, installation.Id,
                        Read<Wire.StartWorkSprintExecutionRequest>(request), cancellationToken)),
                WorkOrchestrationActions.Pause or WorkOrchestrationActions.Resume or WorkOrchestrationActions.Cancel => Success(
                    request.RequestId,
                    await ControlOrchestrationAsync(
                        organizationId, installation.Id, request.Capability,
                        Read<Wire.ControlWorkSprintExecutionRequest>(request), cancellationToken)),
                WorkOrchestrationActions.Retry => Success(
                    request.RequestId,
                    await RetryOrchestrationAsync(
                        organizationId, installation.Id,
                        Read<Wire.RetryWorkStageExecutionRequest>(request), cancellationToken)),
                WorkOrchestrationActions.ConfigureSoftwareTemplate => Success(
                    request.RequestId,
                    await ConfigureSoftwareTemplateAsync(
                        organizationId, installation,
                        Read<Wire.ConfigureSoftwareOrchestrationTemplateRequest>(request), cancellationToken)),
                _ => Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound,
                    "The work-management capability is not implemented.")
            };
        }
        catch (JsonException)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed,
                "The capability payload is not valid JSON.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, exception.Message);
        }
    }

    private Task<Wire.WorkSprintPreflightResult> PreflightOrchestrationAsync(
        Guid organizationId, Guid installationId, Wire.StartWorkSprintExecutionRequest input,
        CancellationToken cancellationToken) =>
        orchestration.PreflightAsync(
            organizationId, input.BoardId, input.SprintId, installationId, cancellationToken);

    private async Task<Wire.WorkSprintExecutionResponse?> ReadOrchestrationAsync(
        Guid organizationId, Guid installationId, Wire.ReadWorkOrchestrationRequest input,
        CancellationToken cancellationToken)
    {
        if (input.BoardId == Guid.Empty || (!input.SprintId.HasValue && !input.SprintExecutionId.HasValue))
            throw new ArgumentException("Board and sprint or sprint-execution identity are required.");
        if (input.SprintId.HasValue && input.SprintExecutionId.HasValue &&
            !await db.WorkSprintExecutions.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.BoardId == input.BoardId &&
                x.Id == input.SprintExecutionId && x.SprintId == input.SprintId,
                cancellationToken))
            throw new InvalidOperationException(
                "The sprint and sprint-execution identities do not reference the same execution.");
        var sprintId = input.SprintId;
        if (!sprintId.HasValue)
            sprintId = await db.WorkSprintExecutions.AsNoTracking().Where(x =>
                    x.OrganizationId == organizationId && x.BoardId == input.BoardId &&
                    x.Id == input.SprintExecutionId)
                .Select(x => (Guid?)x.SprintId).SingleOrDefaultAsync(cancellationToken);
        if (!sprintId.HasValue)
            return null;
        var execution = await orchestration.GetExecutionAsync(
            organizationId, input.BoardId, sprintId.Value, installationId, cancellationToken);
        return execution is null ? null : await ToWireExecutionAsync(execution, cancellationToken);
    }

    private async Task<Wire.WorkStageExecutionResponse> RetryOrchestrationAsync(
        Guid organizationId, Guid installationId, Wire.RetryWorkStageExecutionRequest input,
        CancellationToken cancellationToken)
    {
        if (input.ExpectedAssignmentRevision <= 0)
            throw new ArgumentException("Expected assignment revision is required.");
        var stage = await orchestration.RetryAsync(
            organizationId, input.BoardId, input.StageExecutionId, installationId,
            new WorkOrchestrationControlRequest(
                input.ExpectedAssignmentRevision, input.IdempotencyKey, input.Reason), cancellationToken);
        var stageSource = await db.WorkStageExecutions.AsNoTracking()
            .Where(x => x.Id == input.StageExecutionId)
            .Select(x => new
            {
                x.ItemExecution!.WorkItemId,
                x.ItemExecution.SprintExecution!.PolicyRevisionId
            }).SingleAsync(cancellationToken);
        var workItem = await db.CoreWorkTasks.AsNoTracking()
            .SingleAsync(x => x.Id == stageSource.WorkItemId, cancellationToken);
        var maximumAttempts = await db.WorkOrchestrationStages.AsNoTracking()
            .Where(x => x.PolicyRevisionId == stageSource.PolicyRevisionId && x.Key == stage.StageKey)
            .Select(x => x.MaximumAttempts).SingleAsync(cancellationToken);
        return new Wire.WorkStageExecutionResponse(
            stage.Id, stage.StageKey, stage.StageType, stage.Traversal, stage.Status,
            stage.PrincipalKind, stage.OrganizationUserId, stage.AgentInstallationId,
            stage.PlatformAction, stage.AttemptCount, stage.LastOutcomeCode, stage.LastSummary,
            stage.LastError, stage.RetryAt, stage.UpdatedAt)
        {
            AssignmentRevision = workItem.AssignmentRevision,
            MaximumAttempts = maximumAttempts
        };
    }

    private async Task<Wire.WorkSprintExecutionResponse> ToWireExecutionAsync(
        Wire.WorkSprintExecutionResponse execution,
        CancellationToken cancellationToken)
    {
        var revisions = await db.CoreWorkTasks.AsNoTracking()
            .Where(x => execution.Items.Select(i => i.WorkItemId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.AssignmentRevision, cancellationToken);
        var maximumAttempts = await db.WorkOrchestrationStages.AsNoTracking()
            .Where(x => x.PolicyRevisionId == execution.PolicyRevisionId)
            .ToDictionaryAsync(x => x.Key, x => x.MaximumAttempts, StringComparer.Ordinal,
                cancellationToken);
        return new Wire.WorkSprintExecutionResponse(
            execution.Id, execution.BoardId, execution.SprintId, execution.PolicyRevisionId,
            execution.StartedByOrganizationUserId, execution.Status, execution.Revision,
            execution.StartedAt, execution.UpdatedAt, execution.CompletedAt,
            execution.Items.Select(item => new Wire.WorkItemExecutionResponse(
                item.Id, item.WorkItemId, item.ItemIdentifier, item.CurrentStageKey, item.Traversal,
                item.Status, item.BlockedReason,
                item.Stages.Select(stage => new Wire.WorkStageExecutionResponse(
                    stage.Id, stage.StageKey, stage.StageType, stage.Traversal, stage.Status,
                    stage.PrincipalKind, stage.OrganizationUserId, stage.AgentInstallationId,
                    stage.PlatformAction, stage.AttemptCount, stage.LastOutcomeCode, stage.LastSummary,
                    stage.LastError, stage.RetryAt, stage.UpdatedAt)
                {
                    AssignmentRevision = revisions.GetValueOrDefault(item.WorkItemId),
                    MaximumAttempts = maximumAttempts.GetValueOrDefault(stage.StageKey)
                }).ToList(),
                item.UpdatedAt)).ToList());
    }

    private Task<Wire.WorkSprintExecutionResponse> StartOrchestrationAsync(
        Guid organizationId, Guid installationId, Wire.StartWorkSprintExecutionRequest input,
        CancellationToken cancellationToken) =>
        orchestration.StartAsync(
            organizationId, input.BoardId, input.SprintId, installationId,
            new WorkOrchestrationControlRequest(
                input.ExpectedSprintRevision, input.IdempotencyKey), cancellationToken);

    private async Task<Wire.WorkSprintExecutionResponse> ControlOrchestrationAsync(
        Guid organizationId, Guid installationId, string capability,
        Wire.ControlWorkSprintExecutionRequest input, CancellationToken cancellationToken)
    {
        var action = capability switch
        {
            WorkOrchestrationActions.Pause => "pause",
            WorkOrchestrationActions.Resume => "resume",
            WorkOrchestrationActions.Cancel => "cancel",
            _ => throw new ArgumentException("The orchestration action is invalid.")
        };
        return await orchestration.ControlAsync(
            organizationId, input.BoardId, input.SprintId, installationId, action,
            new WorkOrchestrationControlRequest(
                input.ExpectedSprintRevision, input.IdempotencyKey, input.Reason), cancellationToken)
            ?? throw new KeyNotFoundException("Sprint execution was not found.");
    }

    private async Task<Wire.WorkOrchestrationPolicyRevision> ConfigureSoftwareTemplateAsync(
        Guid organizationId,
        AgentInstallation installation,
        Wire.ConfigureSoftwareOrchestrationTemplateRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkOrchestrationActions.ConfigureSoftwareTemplate,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (input.MaximumQualityCycles is < 1 or > 10)
            throw new ArgumentException("Maximum QA cycles must be between 1 and 10.");
        var replay = await ReplayAsync<Wire.WorkOrchestrationPolicyRevision>(
            installation.Id, WorkOrchestrationActions.ConfigureSoftwareTemplate,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        var retry = new Wire.WorkOrchestrationRetryPolicy();
        var stages = new List<Wire.WorkOrchestrationStageDefinition>
        {
            new("ready", "Ready For Development", Wire.WorkOrchestrationStageTypes.Queue,
                input.ReadyColumnId, "Wait until dependencies are complete.", "{}", "{}", 30, null, retry),
            new("development", "In Development", "MemberExecution",
                input.DevelopmentColumnId,
                "Implement the approved ticket, validate it, and publish a reviewable pull request.", "{}",
                "{\"type\":\"object\",\"required\":[\"repositoryConnectionId\",\"sourceBranch\",\"commitSha\",\"pullRequestUrl\",\"summary\"]}",
                3600, null, retry),
            new("dev-complete", "Dev Complete", Wire.WorkOrchestrationStageTypes.Queue,
                input.DevCompleteColumnId,
                "Development is complete and ready for independent testing.", "{}", "{}", 30, null, retry),
            new("quality", "In Testing", "MemberExecution",
                input.QualityColumnId,
                "Validate the exact development commit without modifying tracked source.", "{}",
                "{\"type\":\"object\",\"required\":[\"verdict\",\"summary\",\"criteria\",\"validations\",\"findings\",\"remainingRisks\"]}",
                1800, null, retry),
            new("merge-decision", "Ready To Merge",
                input.MergeMode == Wire.WorkMergeModes.Automatic
                    ? Wire.WorkOrchestrationStageTypes.Queue
                    : Wire.WorkOrchestrationStageTypes.ManagerApproval,
                input.ReadyToMergeColumnId,
                "Authorize merge of the exact QA-approved commit.", "{}", "{}", 86400, 1, retry),
            new("governed-merge", "Governed merge", Wire.WorkOrchestrationStageTypes.TrustedPlatformAction,
                input.ReadyToMergeColumnId,
                "Revalidate and merge the exact QA-approved commit.", "{}", "{}", 300, 1, retry,
                GovernedMergeWorkActionExecutor.ActionName),
            new("done", "Done", Wire.WorkOrchestrationStageTypes.Terminal,
                input.DoneColumnId, "Work is complete.", "{}", "{}", 30, null, retry, null, true),
            new("cancelled", "Cancelled", Wire.WorkOrchestrationStageTypes.Terminal,
                input.DoneColumnId, "Work was rejected.", "{}", "{}", 30, null, retry)
        };
        var transitions = new List<Wire.WorkOrchestrationTransitionDefinition>
        {
            new("ready", "ready", "development"),
            new("development", "completed", "dev-complete"),
            new("dev-complete", "ready", "quality"),
            new("quality", "passed", "merge-decision"),
            new("quality", "changes_requested", "development", input.MaximumQualityCycles),
            new("merge-decision", input.MergeMode == Wire.WorkMergeModes.Automatic ? "ready" : "approved", "governed-merge"),
            new("merge-decision", "rejected", "cancelled"),
            new("governed-merge", "merged", "done")
        };
        var revision = await orchestration.SavePolicyRevisionAsync(
            organizationId, input.BoardId, installation.Id,
            new SaveWorkOrchestrationPolicyRequest(
                "Software delivery", "ready", input.MergeMode,
                new Wire.WorkOrchestrationConcurrencyLimits(100, 25, 10, 5, 1),
                stages, transitions, input.IdempotencyKey), cancellationToken);
        var published = await orchestration.PublishPolicyRevisionAsync(
            organizationId, input.BoardId, installation.Id,
            new PublishWorkOrchestrationPolicyRequest(
                revision.RevisionId, $"{input.IdempotencyKey}:publish"), cancellationToken);
        AddReceipt(
            organizationId, installation.Id, WorkOrchestrationActions.ConfigureSoftwareTemplate,
            input.IdempotencyKey, published.RevisionId, published);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkOrchestrationActions.ConfigureSoftwareTemplate, grant,
            new { published.RevisionId, input.IdempotencyKey }, cancellationToken);
        return published;
    }

    private Task<Wire.WorkItem> MoveItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.MoveWorkItemRequest input,
        CancellationToken cancellationToken) =>
        TransitionItemAsync(
            session,
            organizationId,
            installation,
            WorkItemActions.Move,
            new Wire.TransitionWorkItemRequest(
                input.BoardId,
                input.ItemId,
                input.ExpectedRevision,
                input.IdempotencyKey,
                input.TargetColumnId),
            cancellationToken);

    private async Task<IReadOnlyList<Wire.WorkBoardSummary>> ListBoardsAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkBoardListRequest input,
        CancellationToken cancellationToken)
    {
        var grants = await ActiveGrantsAsync(organizationId, installationId, cancellationToken);
        var organizationRead = grants.Any(x =>
            x.Action == WorkBoardActions.Read && x.ScopeKind == GrantScopeKind.Organization);
        var boardIds = grants.Where(x =>
                x.Action == WorkBoardActions.Read &&
                x.ScopeKind == GrantScopeKind.Board &&
                x.ScopeId.HasValue)
            .Select(x => x.ScopeId!.Value)
            .ToHashSet();
        var teamIds = grants.Where(x =>
                x.Action == WorkBoardActions.Read &&
                x.ScopeKind == GrantScopeKind.Team &&
                x.ScopeId.HasValue)
            .Select(x => x.ScopeId!.Value)
            .ToHashSet();
        if (!organizationRead && boardIds.Count == 0 && teamIds.Count == 0)
            throw new UnauthorizedAccessException("The installation has no board read grant.");

        var query = db.WorkBoards.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Kind == WorkBoardKind.Standard)
            .Where(x => organizationRead || boardIds.Contains(x.Id) ||
                        (x.TeamId.HasValue && teamIds.Contains(x.TeamId.Value)));
        if (!input.IncludeArchived)
            query = query.Where(x => x.ArchivedAt == null);
        if (!string.IsNullOrWhiteSpace(input.Search))
        {
            var search = input.Search.Trim().ToLower();
            query = query.Where(x =>
                x.Name.ToLower().Contains(search) ||
                x.Description.ToLower().Contains(search));
        }
        var boards = await query.OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var result = boards.Select(board =>
        {
                var allowed = grants.Where(x =>
                    x.ScopeKind == GrantScopeKind.Organization ||
                    (x.ScopeKind == GrantScopeKind.Board && x.ScopeId == board.Id) ||
                    (x.ScopeKind == GrantScopeKind.Team && x.ScopeId == board.TeamId))
                .Select(x => x.Action)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            return new Wire.WorkBoardSummary(
                board.Id, board.Name, board.Description, board.IsDefault,
                board.ArchivedAt.HasValue, board.Revision, allowed)
            {
                TeamId = board.TeamId,
                ManagerOrganizationUserId = board.ManagerOrganizationUserId,
                Key = board.Key
            };
        }).ToList();
        await WriteAuditAsync(
            organizationId, installationId, null, WorkBoardActions.Read, null,
            new { count = result.Count, input.Search, input.IncludeArchived }, cancellationToken);
        return result;
    }

    private async Task<Wire.WorkBoardDetail> ReadBoardAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkBoardReference input,
        CancellationToken cancellationToken)
    {
        var boardGrant = await RequireAsync(
            organizationId, installationId, WorkBoardActions.Read, input.BoardId, cancellationToken);
        var itemGrant = await RequireAsync(
            organizationId, installationId, WorkItemActions.Read, input.BoardId, cancellationToken);
        var board = await db.WorkBoards.AsNoTracking()
            .Include(x => x.Columns.OrderBy(column => column.Position))
            .SingleOrDefaultAsync(x =>
                x.Id == input.BoardId && x.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new KeyNotFoundException("Board was not found.");
        var itemRows = await db.CoreWorkTasks.AsNoTracking().Include(x => x.StageAssignments)
            .Where(x => x.BoardId == board.Id && x.BoardColumnId != null)
            .OrderBy(x => x.BoardColumnId)
            .ThenBy(x => x.BoardRank)
            .Select(x => new
            {
                x.Id,
                x.BoardColumnId,
                x.ParentWorkTaskId,
                x.SprintId,
                x.Kind,
                x.Title,
                x.Description,
                x.Status,
                x.Priority,
                x.EstimatePoints,
                x.BoardRank,
                x.Revision,
                x.DueDate,
                x.StructuredMentionsJson
            })
            .ToListAsync(cancellationToken);
        var items = itemRows
            .Select(x => new Wire.WorkItem(
                x.Id, x.BoardColumnId!.Value, x.ParentWorkTaskId, x.SprintId,
                x.Kind.ToString(), x.Title, x.Description, x.Status.ToString(),
                x.Priority.ToString(), x.EstimatePoints, x.BoardRank, x.Revision,
                x.DueDate)
            {
                Mentions = WorkItemMentionCodec.Deserialize(x.StructuredMentionsJson)
            })
            .ToList();
        await WriteAuditAsync(
            organizationId, installationId, board.Id, WorkItemActions.Read, itemGrant,
            new { board.Id, itemCount = items.Count, boardGrantId = boardGrant.GrantId },
            cancellationToken);
        return new Wire.WorkBoardDetail(
            new Wire.WorkBoardSummary(
                board.Id, board.Name, board.Description, board.IsDefault,
                board.ArchivedAt.HasValue, board.Revision,
                [WorkBoardActions.Read, WorkItemActions.Read])
            {
                TeamId = board.TeamId
            },
            board.Columns.Select(x => new Wire.WorkBoardColumn(
                x.Id, x.Name, x.Category.ToString(), x.Position,
                x.WipPolicy.ToString(), x.WipLimit)).ToList(),
            items);
    }

    private async Task<object> ReadBoardOrItemAsync(
        Guid organizationId,
        Guid installationId,
        RequestCapability request,
        CancellationToken cancellationToken) =>
        request.Payload.ToElement().TryGetProperty("itemId", out _)
            ? await ReadItemAsync(
                organizationId,
                installationId,
                Read<Wire.WorkItemReference>(request),
                cancellationToken)
            : await ReadBoardAsync(
                organizationId,
                installationId,
                Read<Wire.WorkBoardReference>(request),
                cancellationToken);

    private async Task<Wire.WorkItem> ReadItemAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkItemReference input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireForItemAsync(
            organizationId,
            installationId,
            WorkItemActions.Read,
            input.BoardId,
            input.ItemId,
            cancellationToken);
        var item = await db.CoreWorkTasks.AsNoTracking().Include(x => x.StageAssignments).SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId &&
            x.Id == input.ItemId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        await WriteAuditAsync(
            organizationId,
            installationId,
            input.BoardId,
            WorkItemActions.Read,
            grant,
            new { input.BoardId, input.ItemId },
            cancellationToken);
        return await ToAgentItemAsync(item, cancellationToken);
    }

    private async Task<IReadOnlyList<Wire.WorkSprint>> ListSprintsAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkBoardReference input,
        CancellationToken cancellationToken)
    {
        await RequireAsync(
            organizationId, installationId, WorkBoardActions.Read,
            input.BoardId, cancellationToken);
        var grant = await RequireAsync(
            organizationId, installationId, WorkSprintActions.Read,
            input.BoardId, cancellationToken);
        if (!await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                x.Id == input.BoardId && x.OrganizationId == organizationId,
                cancellationToken))
            throw new KeyNotFoundException("Board was not found.");
        var sprints = await db.WorkSprints.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BoardId == input.BoardId)
            .OrderByDescending(x => x.Status == WorkSprintStatus.Active)
            .ThenByDescending(x => x.StartsAt)
            .ToListAsync(cancellationToken);
        var counts = await db.CoreWorkTasks.AsNoTracking()
            .Where(x => x.BoardId == input.BoardId && x.SprintId != null)
            .GroupBy(x => x.SprintId!.Value)
            .Select(x => new
            {
                SprintId = x.Key,
                Total = x.Count(),
                Completed = x.Count(item => item.Status == WorkTaskStatus.Completed),
                PlannedPoints = x.Sum(item => item.EstimatePoints ?? 0),
                CompletedPoints = x.Where(item => item.Status == WorkTaskStatus.Completed)
                    .Sum(item => item.EstimatePoints ?? 0)
            })
            .ToDictionaryAsync(x => x.SprintId, cancellationToken);
        var result = sprints.Select(x =>
        {
            counts.TryGetValue(x.Id, out var count);
            return ToAgentSprint(
                x, count?.Total ?? 0, count?.Completed ?? 0,
                count?.PlannedPoints ?? 0, count?.CompletedPoints ?? 0);
        }).ToList();
        await WriteAuditAsync(
            organizationId, installationId, input.BoardId, WorkSprintActions.Read,
            grant, new { count = result.Count }, cancellationToken);
        return result;
    }

    private async Task<Wire.WorkSprint> CreateSprintAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CreateWorkSprintRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkSprintActions.Create,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkSprint>(
            installation.Id, WorkSprintActions.Create,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException("Sprint name is required.");
        if (input.Name.Trim().Length > 160)
            throw new ArgumentException("Sprint name cannot exceed 160 characters.");
        if ((input.Goal?.Trim().Length ?? 0) > 2048)
            throw new ArgumentException("Sprint goal cannot exceed 2048 characters.");
        if (input.StartsAt.HasValue && input.EndsAt.HasValue &&
            input.EndsAt <= input.StartsAt)
            throw new ArgumentException("Sprint end must be after its start.");
        if (input.Sequence is <= 0)
            throw new ArgumentException("Sprint sequence must be positive when supplied.");
        if (!await db.WorkBoards.AnyAsync(x =>
                x.Id == input.BoardId &&
                x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken))
            throw new KeyNotFoundException("Board was not found.");

        var now = DateTimeOffset.UtcNow;
        var sprint = new WorkSprint
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = input.BoardId,
            Name = input.Name.Trim(),
            Goal = input.Goal?.Trim() ?? string.Empty,
            StartsAt = input.StartsAt,
            EndsAt = input.EndsAt,
            Sequence = input.Sequence,
            CreatedAt = now,
            UpdatedAt = now
        };
        var result = ToAgentSprint(sprint, 0, 0);
        db.WorkSprints.Add(sprint);
        AddSprintReceipt(
            organizationId, installation.Id, WorkSprintActions.Create,
            input.IdempotencyKey, sprint.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, null, "sprint.created", sprint.Revision,
            cancellationToken, sprintId: sprint.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId, WorkSprintActions.Create,
            grant, new { sprint.Id, sprint.Name, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkSprint> ChangeSprintStateAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        string action,
        Wire.ChangeWorkSprintStateRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, action, input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkSprint>(
            installation.Id, action, input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != input.SprintId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different sprint.");
            return replay;
        }
        var sprint = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == input.SprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Sprint was not found.");
        if (sprint.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected sprint revision {input.ExpectedRevision}, current revision is {sprint.Revision}.");
        var now = DateTimeOffset.UtcNow;
        switch (action)
        {
            case WorkSprintActions.Start:
                if (sprint.Status != WorkSprintStatus.Planned)
                    throw new InvalidOperationException("Only a planned sprint can be started.");
                if (await db.WorkSprints.AnyAsync(x =>
                        x.BoardId == input.BoardId &&
                        x.Status == WorkSprintStatus.Active &&
                        x.Id != sprint.Id, cancellationToken))
                    throw new InvalidOperationException(
                        "This board already has an active sprint.");
                sprint.Status = WorkSprintStatus.Active;
                sprint.StartedAt = now;
                break;
            case WorkSprintActions.Complete:
                if (sprint.Status != WorkSprintStatus.Active)
                    throw new InvalidOperationException("Only an active sprint can be completed.");
                sprint.Status = WorkSprintStatus.Completed;
                sprint.CompletedAt = now;
                break;
            case WorkSprintActions.Cancel:
                if (sprint.Status is WorkSprintStatus.Completed or WorkSprintStatus.Cancelled)
                    throw new InvalidOperationException(
                        "A completed or cancelled sprint cannot be cancelled.");
                sprint.Status = WorkSprintStatus.Cancelled;
                sprint.CompletedAt = now;
                break;
        }
        sprint.Revision++;
        sprint.UpdatedAt = now;
        var total = await db.CoreWorkTasks.CountAsync(
            x => x.SprintId == sprint.Id, cancellationToken);
        var completed = await db.CoreWorkTasks.CountAsync(
            x => x.SprintId == sprint.Id &&
                 x.Status == WorkTaskStatus.Completed, cancellationToken);
        var plannedPoints = await db.CoreWorkTasks.Where(x => x.SprintId == sprint.Id)
            .SumAsync(x => x.EstimatePoints ?? 0, cancellationToken);
        var completedPoints = await db.CoreWorkTasks.Where(x =>
                x.SprintId == sprint.Id && x.Status == WorkTaskStatus.Completed)
            .SumAsync(x => x.EstimatePoints ?? 0, cancellationToken);
        if (action == WorkSprintActions.Complete)
            await WorkSprintSnapshotFactory.EnsureAsync(db, sprint, cancellationToken);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, sprint.Id, SprintEventTypeFor(action), now, cancellationToken);
        var result = ToAgentSprint(
            sprint, total, completed, plannedPoints, completedPoints);
        AddSprintReceipt(
            organizationId, installation.Id, action,
            input.IdempotencyKey, sprint.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, null, SprintEventTypeFor(action),
            sprint.Revision, cancellationToken, sprintId: sprint.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId, action, grant,
            new { sprint.Id, sprint.Status, sprint.Revision, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItem> SetItemSprintAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.SetWorkItemSprintRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkSprintActions.ManageScope,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkItem>(
            installation.Id, WorkSprintActions.ManageScope,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != input.ItemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item.");
            return replay;
        }
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        if (item.Revision != input.ExpectedItemRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {input.ExpectedItemRevision}, current revision is {item.Revision}.");
        if (input.SprintId.HasValue && !await db.WorkSprints.AnyAsync(x =>
                x.Id == input.SprintId.Value &&
                x.OrganizationId == organizationId &&
                x.BoardId == input.BoardId &&
                (x.Status == WorkSprintStatus.Planned ||
                 x.Status == WorkSprintStatus.Active), cancellationToken))
            throw new ArgumentException(
                "The target sprint must be a planned or active sprint on this board.");

        var previousSprintId = item.SprintId;
        item.SprintId = input.SprintId;
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var result = ToAgentItem(item);
        AddActivity(
            organizationId, input.BoardId, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            WorkSprintActions.ManageScope,
            input.SprintId.HasValue ? "item.sprint.assigned" : "item.sprint.removed",
            grant, new { previousSprintId, sprintId = input.SprintId },
            item.UpdatedAt, input.IdempotencyKey);
        AddSprintReceipt(
            organizationId, installation.Id, WorkSprintActions.ManageScope,
            input.IdempotencyKey, item.Id, result);
        foreach (var sprintId in new[] { previousSprintId, input.SprintId }
                     .Where(x => x.HasValue).Distinct())
            await WorkSprintMetricsRecorder.RecordAsync(
                db, sprintId,
                input.SprintId.HasValue ? "item.sprint.assigned" : "item.sprint.removed",
                item.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id,
            input.SprintId.HasValue ? "item.sprint.assigned" : "item.sprint.removed",
            item.Revision, cancellationToken, sprintId: input.SprintId);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkSprintActions.ManageScope, grant,
            new
            {
                item.Id,
                previousSprintId,
                sprintId = input.SprintId,
                item.Revision,
                input.IdempotencyKey
            },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkSprint> SetSprintCapacityAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.SetWorkSprintCapacityRequest input,
        CancellationToken cancellationToken)
    {
        ValidatePoints(input.CapacityPoints, "Capacity");
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkSprintActions.ManageCapacity,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkSprint>(
            installation.Id, WorkSprintActions.ManageCapacity,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != input.SprintId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different sprint.");
            return replay;
        }
        var sprint = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == input.SprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Sprint was not found.");
        if (sprint.Status is WorkSprintStatus.Completed or WorkSprintStatus.Cancelled)
            throw new InvalidOperationException(
                "Capacity cannot be changed after a sprint is closed.");
        if (sprint.Revision != input.ExpectedSprintRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected sprint revision {input.ExpectedSprintRevision}, current revision is {sprint.Revision}.");
        var previousCapacity = sprint.CapacityPoints;
        sprint.CapacityPoints = input.CapacityPoints;
        sprint.Revision++;
        sprint.UpdatedAt = DateTimeOffset.UtcNow;
        var items = await db.CoreWorkTasks.Where(x => x.SprintId == sprint.Id)
            .ToListAsync(cancellationToken);
        var result = ToAgentSprint(
            sprint,
            items.Count,
            items.Count(x => x.Status == WorkTaskStatus.Completed),
            items.Sum(x => x.EstimatePoints ?? 0),
            items.Where(x => x.Status == WorkTaskStatus.Completed)
                .Sum(x => x.EstimatePoints ?? 0));
        AddSprintReceipt(
            organizationId, installation.Id, WorkSprintActions.ManageCapacity,
            input.IdempotencyKey, sprint.Id, result);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, sprint.Id, "sprint.capacity.changed",
            sprint.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, null, "sprint.capacity.changed",
            sprint.Revision, cancellationToken, sprintId: sprint.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkSprintActions.ManageCapacity, grant,
            new { sprint.Id, previousCapacity, capacityPoints = input.CapacityPoints },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkSprintCarryOver> CarryOverSprintAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CarryOverWorkSprintRequest input,
        CancellationToken cancellationToken)
    {
        if (input.SourceSprintId == input.TargetSprintId)
            throw new ArgumentException("Source and target sprint must be different.");
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkSprintActions.CarryOver,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkSprintCarryOver>(
            installation.Id, WorkSprintActions.CarryOver,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.SourceSprintId != input.SourceSprintId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different carryover.");
            return replay;
        }
        var source = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == input.SourceSprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Source sprint was not found.");
        if (source.Status is not (WorkSprintStatus.Completed or WorkSprintStatus.Cancelled))
            throw new InvalidOperationException(
                "Only a completed or cancelled sprint can be carried over.");
        if (source.Revision != input.ExpectedSourceSprintRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected sprint revision {input.ExpectedSourceSprintRevision}, current revision is {source.Revision}.");
        var target = await db.WorkSprints.SingleOrDefaultAsync(x =>
            x.Id == input.TargetSprintId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId &&
            (x.Status == WorkSprintStatus.Planned ||
             x.Status == WorkSprintStatus.Active), cancellationToken)
            ?? throw new ArgumentException(
                "The target must be a planned or active sprint on this board.");
        var requestedIds = input.ItemIds?.Distinct().ToHashSet();
        if (requestedIds?.Count > 500)
            throw new ArgumentException("At most 500 items can be carried over at once.");
        var candidates = await db.CoreWorkTasks.Where(x =>
                x.BoardId == input.BoardId &&
                x.SprintId == source.Id &&
                x.Status != WorkTaskStatus.Completed)
            .ToListAsync(cancellationToken);
        if (requestedIds is not null)
        {
            var availableIds = candidates.Select(x => x.Id).ToHashSet();
            if (!requestedIds.IsSubsetOf(availableIds))
                throw new ArgumentException(
                    "Every requested item must be incomplete and belong to the source sprint.");
            candidates = candidates.Where(x => requestedIds.Contains(x.Id)).ToList();
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var item in candidates)
        {
            item.SprintId = target.Id;
            item.Revision++;
            item.UpdatedAt = now;
            AddActivity(
                organizationId, input.BoardId, item.Id, installation.Id,
                string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
                WorkSprintActions.CarryOver, "item.sprint.carried-over", grant,
                new { sourceSprintId = source.Id, targetSprintId = target.Id }, now);
        }
        source.Revision++;
        source.UpdatedAt = now;
        target.Revision++;
        target.UpdatedAt = now;
        var result = new Wire.WorkSprintCarryOver(
            source.Id, target.Id, candidates.Select(x => x.Id).ToList(),
            candidates.Sum(x => x.EstimatePoints ?? 0));
        AddSprintReceipt(
            organizationId, installation.Id, WorkSprintActions.CarryOver,
            input.IdempotencyKey, source.Id, result);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, source.Id, "sprint.items.carried-over",
            now, cancellationToken);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, target.Id, "sprint.items.carried-over",
            now, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, null, "sprint.items.carried-over",
            target.Revision, cancellationToken, sprintId: target.Id);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkSprintActions.CarryOver, grant,
            new
            {
                sourceSprintId = source.Id,
                targetSprintId = target.Id,
                result.ItemIds,
                result.CarriedPoints
            },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkSprintReport> ReadSprintReportAsync(
        Guid organizationId,
        Guid installationId,
        Wire.WorkBoardReference input,
        CancellationToken cancellationToken)
    {
        await RequireAsync(
            organizationId, installationId, WorkBoardActions.Read,
            input.BoardId, cancellationToken);
        var grant = await RequireAsync(
            organizationId, installationId, WorkSprintActions.ReadReports,
            input.BoardId, cancellationToken);
        if (!await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                x.Id == input.BoardId && x.OrganizationId == organizationId,
                cancellationToken))
            throw new KeyNotFoundException("Board was not found.");
        var platformReport = await WorkSprintReportBuilder.BuildAsync(
            db, organizationId, input.BoardId, cancellationToken);
        var result = ToWireReport(platformReport);
        await WriteAuditAsync(
            organizationId, installationId, input.BoardId,
            WorkSprintActions.ReadReports, grant,
            new { result.CompletedSprintCount }, cancellationToken);
        return result;
    }

    private async Task<Wire.WorkBoardSummary> CreateBoardAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CreateWorkBoardRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireCreateBoardAsync(
            organizationId, installation.Id, input.TeamId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new ArgumentException("Board name is required.");
        if (input.TeamId.HasValue && !await db.OrganizationTeams.AsNoTracking().AnyAsync(x =>
                x.Id == input.TeamId && x.OrganizationId == organizationId && x.ArchivedAt == null,
                cancellationToken))
            throw new ArgumentException("The selected team is not active in this organization.");
        var manager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installation.Id && x.IsActive,
            cancellationToken) ?? throw new InvalidOperationException(
            "The agent installation must have an active organization-user identity before it can manage a board.");
        var replay = await ReplayAsync<Wire.WorkBoardSummary>(
            installation.Id, WorkBoardActions.Create, input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        var now = DateTimeOffset.UtcNow;
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TeamId = input.TeamId,
            ManagerOrganizationUserId = manager.Id,
            Key = await ResolveBoardKeyAsync(organizationId, input.Key, input.Name, cancellationToken),
            Name = input.Name.Trim(),
            Description = input.Description?.Trim() ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            Columns =
            [
                NewColumn("To Do", WorkBoardColumnCategory.ToDo, 0),
                NewColumn("Done", WorkBoardColumnCategory.Done, 1)
            ]
        };
        var result = new Wire.WorkBoardSummary(
            board.Id, board.Name, board.Description, false, false, board.Revision,
            [WorkBoardActions.Create])
        {
            TeamId = board.TeamId,
            ManagerOrganizationUserId = board.ManagerOrganizationUserId,
            Key = board.Key
        };
        db.WorkBoards.Add(board);
        AddReceipt(
            organizationId, installation.Id, WorkBoardActions.Create,
            input.IdempotencyKey, board.Id, result);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, board.Id, WorkBoardActions.Create, grant,
            new { board.Id, board.Name, input.IdempotencyKey }, cancellationToken, session);
        return result;
    }

    private async Task<ScopedAuthorizationDecision> RequireCreateBoardAsync(
        Guid organizationId,
        Guid installationId,
        Guid? teamId,
        CancellationToken cancellationToken)
    {
        var decision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.AgentInstallation,
            installationId,
            WorkBoardActions.Create,
            teamId.HasValue ? GrantScopeKind.Team : GrantScopeKind.Organization,
            teamId,
            cancellationToken);
        if (decision.Allowed) return decision;
        await WriteAuditAsync(
            organizationId,
            installationId,
            teamId,
            WorkBoardActions.Create,
            null,
            new { action = WorkBoardActions.Create, teamId },
            cancellationToken,
            outcome: "Denied");
        throw new UnauthorizedAccessException(
            teamId.HasValue
                ? "The installation does not have board-create access for the selected team."
                : "The installation does not have organization board-create access.");
    }

    private async Task<Wire.WorkBoardSummary> ConfigureBoardAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.ConfigureWorkBoardRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkBoardActions.Configure,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplayAsync<Wire.WorkBoardSummary>(
            installation.Id, WorkBoardActions.Configure,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != input.BoardId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different board.");
            return replay;
        }

        var name = input.Name.Trim();
        var description = input.Description?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160)
            throw new ArgumentException("Board name must be 1-160 characters.");
        if (description.Length > 2048)
            throw new ArgumentException("Board description must not exceed 2048 characters.");

        var actorId = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId && x.AgentInstallationId == installation.Id && x.IsActive)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException(
                "The installation is not assigned to an active organization member.");
        var board = await db.WorkBoards.SingleOrDefaultAsync(x =>
                x.Id == input.BoardId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("Board was not found.");
        if (board.ManagerOrganizationUserId != actorId)
            throw new UnauthorizedAccessException(
                "Only the assigned board manager may configure board metadata.");
        if (board.ArchivedAt.HasValue)
            throw new InvalidOperationException(
                "Archived boards must be restored before their metadata can be configured.");
        if (board.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected board revision {input.ExpectedRevision}, current revision is {board.Revision}.");

        board.Name = name;
        board.Description = description;
        board.Revision++;
        board.UpdatedAt = DateTimeOffset.UtcNow;
        var result = new Wire.WorkBoardSummary(
            board.Id, board.Name, board.Description, board.IsDefault,
            board.ArchivedAt.HasValue, board.Revision,
            [WorkBoardActions.Read, WorkBoardActions.Configure])
        {
            TeamId = board.TeamId,
            ManagerOrganizationUserId = board.ManagerOrganizationUserId,
            Key = board.Key
        };
        AddReceipt(
            organizationId, installation.Id, WorkBoardActions.Configure,
            input.IdempotencyKey, board.Id, result);
        await QueueRealtimeAsync(
            organizationId, board.Id, null, "board.configured", board.Revision,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, board.Id, WorkBoardActions.Configure, grant,
            new { board.Id, board.Name, board.Revision, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkBoardDetail> ConfigureBoardColumnsAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.ConfigureWorkBoardColumnsRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkBoardActions.ConfigureColumns,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplayAsync<Wire.WorkBoardDetail>(
            installation.Id, WorkBoardActions.ConfigureColumns,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        var board = await db.WorkBoards.Include(x => x.Columns).SingleOrDefaultAsync(x =>
            x.Id == input.BoardId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("Board was not found.");
        if (board.ArchivedAt.HasValue)
            throw new InvalidOperationException(
                "Archived boards must be restored before their columns can be configured.");
        if (board.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected board revision {input.ExpectedRevision}, current revision is {board.Revision}.");
        if (input.Columns.Count == 0)
            throw new ArgumentException("At least one board column is required.");

        var parsed = input.Columns.Select((column, position) =>
        {
            if (!Enum.TryParse<WorkBoardColumnCategory>(column.Category, true, out var category) ||
                !Enum.IsDefined(category))
                throw new ArgumentException("A board column category is invalid.");
            if (!Enum.TryParse<WorkBoardWipPolicy>(column.WipPolicy, true, out var wipPolicy) ||
                !Enum.IsDefined(wipPolicy))
                throw new ArgumentException("A board column WIP policy is invalid.");
            return new
            {
                column.Id,
                Name = column.Name.Trim(),
                Category = category,
                WipPolicy = wipPolicy,
                column.WipLimit,
                Position = position
            };
        }).ToList();
        if (parsed.Any(x => string.IsNullOrWhiteSpace(x.Name)))
            throw new ArgumentException("Every board column requires a name.");
        if (parsed.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parsed.Count)
            throw new ArgumentException("Board column names must be unique.");
        if (!parsed.Any(x => x.Category == WorkBoardColumnCategory.ToDo) ||
            !parsed.Any(x => x.Category == WorkBoardColumnCategory.Done))
            throw new ArgumentException("A board requires at least one To Do column and one Done column.");
        if (parsed.Any(x => x.WipPolicy != WorkBoardWipPolicy.Disabled &&
                            (!x.WipLimit.HasValue || x.WipLimit <= 0)))
            throw new ArgumentException("Warning and hard WIP policies require a positive limit.");

        var requestedIds = parsed.Where(x => x.Id.HasValue).Select(x => x.Id!.Value).ToHashSet();
        if (requestedIds.Count != parsed.Count(x => x.Id.HasValue) ||
            requestedIds.Any(id => board.Columns.All(x => x.Id != id)))
            throw new ArgumentException(
                "A column identifier is duplicated or does not belong to this board.");
        var removed = board.Columns.Where(x => !requestedIds.Contains(x.Id)).ToList();
        var removedIds = removed.Select(x => x.Id).ToHashSet();
        if (removedIds.Count > 0 && await db.CoreWorkTasks.AnyAsync(x =>
                x.BoardId == board.Id && x.BoardColumnId.HasValue &&
                removedIds.Contains(x.BoardColumnId.Value), cancellationToken))
            throw new InvalidOperationException("Move all cards out of a column before removing it.");

        db.WorkBoardColumns.RemoveRange(removed);
        var configured = new List<WorkBoardColumn>();
        foreach (var value in parsed)
        {
            var column = value.Id.HasValue
                ? board.Columns.Single(x => x.Id == value.Id.Value)
                : new WorkBoardColumn { Id = Guid.NewGuid(), BoardId = board.Id };
            column.Name = value.Name;
            column.Category = value.Category;
            column.Position = value.Position;
            column.WipPolicy = value.WipPolicy;
            column.WipLimit = value.WipPolicy == WorkBoardWipPolicy.Disabled ? null : value.WipLimit;
            if (!value.Id.HasValue) db.WorkBoardColumns.Add(column);
            configured.Add(column);
        }
        board.Revision++;
        board.UpdatedAt = DateTimeOffset.UtcNow;

        var itemRows = await db.CoreWorkTasks.Include(x => x.StageAssignments)
            .Where(x => x.OrganizationId == organizationId && x.BoardId == board.Id && x.BoardColumnId != null)
            .OrderBy(x => x.BoardColumnId).ThenBy(x => x.BoardRank)
            .ToListAsync(cancellationToken);
        var items = new List<Wire.WorkItem>(itemRows.Count);
        foreach (var item in itemRows)
            items.Add(await ToAgentItemAsync(item, cancellationToken));
        var result = new Wire.WorkBoardDetail(
            new Wire.WorkBoardSummary(
                board.Id, board.Name, board.Description, board.IsDefault,
                board.ArchivedAt.HasValue, board.Revision,
                [WorkBoardActions.Read, WorkBoardActions.ConfigureColumns, WorkItemActions.Read])
            {
                TeamId = board.TeamId,
                ManagerOrganizationUserId = board.ManagerOrganizationUserId,
                Key = board.Key
            },
            configured.OrderBy(x => x.Position).Select(x => new Wire.WorkBoardColumn(
                x.Id, x.Name, x.Category.ToString(), x.Position,
                x.WipPolicy.ToString(), x.WipLimit)).ToList(),
            items);
        AddReceipt(
            organizationId, installation.Id, WorkBoardActions.ConfigureColumns,
            input.IdempotencyKey, board.Id, result);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, board.Id, WorkBoardActions.ConfigureColumns, grant,
            new { board.Id, board.Revision, columnCount = configured.Count, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItem> CreateItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CreateWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.Create, input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(input.Title))
            throw new ArgumentException("Work item title is required.");
        if (!Enum.TryParse<WorkItemKind>(input.Kind, true, out var kind) || !Enum.IsDefined(kind))
            throw new ArgumentException("Work item kind is invalid.");
        if (!Enum.TryParse<WorkTaskPriority>(input.Priority, true, out var priority) ||
            !Enum.IsDefined(priority))
            throw new ArgumentException("Work item priority is invalid.");
        var replay = await ReplayAsync<Wire.WorkItem>(
            installation.Id, WorkItemActions.Create, input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        var board = await db.WorkBoards
            .Include(x => x.Columns)
            .Include(x => x.OrchestrationPolicies).ThenInclude(x => x.Revisions).ThenInclude(x => x.Stages)
            .SingleOrDefaultAsync(x =>
                x.Id == input.BoardId &&
                x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("Board was not found.");
        var column = input.ColumnId.HasValue
            ? board.Columns.SingleOrDefault(x => x.Id == input.ColumnId.Value)
            : board.Columns.OrderBy(x => x.Position)
                .FirstOrDefault(x => x.Category == WorkBoardColumnCategory.ToDo);
        if (column is null)
            throw new ArgumentException("The requested board column was not found.");
        await EnforceWipAsync(column, null, cancellationToken);
        if (input.ParentItemId.HasValue && !await db.CoreWorkTasks.AnyAsync(x =>
                x.Id == input.ParentItemId &&
                x.OrganizationId == organizationId &&
                x.BoardId == board.Id, cancellationToken))
            throw new ArgumentException("The parent work item must belong to the same board.");
        var executable = kind is not (WorkItemKind.Initiative or WorkItemKind.Epic);
        var deliveryReady = executable && input.Delivery is not null;
        if (executable && input.Planning is null && input.Delivery is null)
            throw new ArgumentException("Executable work items require a planning or delivery specification.");
        if (deliveryReady && !input.AccountableOrganizationUserId.HasValue)
            throw new ArgumentException("Delivery-ready work items require an accountable organization user.");
        if (input.AccountableOrganizationUserId.HasValue && !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.Id == input.AccountableOrganizationUserId && x.OrganizationId == organizationId && x.IsActive,
                cancellationToken))
            throw new ArgumentException("The accountable organization user is not active.");
        var publishedRevisionId = board.OrchestrationPolicies.SingleOrDefault()?.PublishedRevisionId;
        var policyRevision = publishedRevisionId.HasValue
            ? board.OrchestrationPolicies.Single().Revisions.Single(x => x.Id == publishedRevisionId.Value)
            : null;
        var policyStages = policyRevision?.Stages.ToList() ?? [];
        ValidateStageAssignments(
            deliveryReady, policyRevision?.InitialStageKey, policyStages, input.StageAssignments);
        if (input.Planning is not null &&
            (input.Planning.Requirements.Count == 0 || input.Planning.AcceptanceCriteria.Count == 0))
            throw new ArgumentException("The planning specification is incomplete.");
        if (input.Delivery is not null)
        {
            if (input.Delivery.RepositoryId == Guid.Empty ||
                string.IsNullOrWhiteSpace(input.Delivery.BaseBranch) ||
                input.Delivery.Requirements.Count == 0 ||
                input.Delivery.AcceptanceCriteria.Count == 0)
                throw new ArgumentException("The delivery specification is incomplete.");
            var dependencyCount = await db.CoreWorkTasks.CountAsync(x =>
                x.OrganizationId == organizationId &&
                x.BoardId == board.Id &&
                input.Delivery.DependencyItemIds.Contains(x.Id), cancellationToken);
            if (dependencyCount != input.Delivery.DependencyItemIds.Distinct().Count())
                throw new ArgumentException(
                    "Every delivery dependency must already exist on the same board.");
        }

        var normalized = await WorkItemMentionCodec.NormalizeAndValidateAsync(
            db, organizationId, input.Title, input.Description, input.Mentions,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var item = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = board.Id,
            BoardColumnId = column.Id,
            ParentWorkTaskId = input.ParentItemId,
            AccountableOrganizationUserId = input.AccountableOrganizationUserId,
            IdentifierSequence = board.NextItemSequence,
            Identifier = $"{board.Key}-{board.NextItemSequence}",
            Kind = kind,
            Title = normalized.Title,
            Description = normalized.Description,
            StructuredMentionsJson = normalized.MentionsJson,
            Status = StatusFor(column.Category),
            Priority = priority,
            BoardRank = (await db.CoreWorkTasks
                .Where(x => x.BoardColumnId == column.Id)
                .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024,
            DueDate = input.DueDate,
            PlanningSpecificationJson = input.Planning is null
                ? null
                : JsonSerializer.Serialize(input.Planning, JsonOptions),
            DeliverySpecificationJson = input.Delivery is null
                ? null
                : JsonSerializer.Serialize(input.Delivery, JsonOptions),
            IsQaTrackingDefect = input.Delivery?.IsQaTrackingDefect ?? false,
            CreatedAt = now,
            UpdatedAt = now
        };
        var result = ToAgentItem(item);
        db.CoreWorkTasks.Add(item);
        board.NextItemSequence++;
        foreach (var assignment in input.StageAssignments)
            db.WorkItemStageAssignments.Add(new WorkItemStageAssignment
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = board.Id,
                WorkItemId = item.Id, StageKey = assignment.StageKey,
                PrincipalKind = Enum.Parse<WorkOrchestrationPrincipalKind>(assignment.PrincipalKind, true),
                OrganizationUserId = assignment.OrganizationUserId,
                AgentInstallationId = assignment.AgentInstallationId,
                PlatformAction = assignment.PlatformAction, CreatedAt = now
            });
        var dependencyIds = input.Planning?.DependencyItemIds ?? input.Delivery?.DependencyItemIds ?? [];
        if (dependencyIds.Count > 0)
        {
            var dependencyCount = await db.CoreWorkTasks.CountAsync(x =>
                x.OrganizationId == organizationId && x.BoardId == board.Id &&
                dependencyIds.Contains(x.Id), cancellationToken);
            if (dependencyCount != dependencyIds.Distinct().Count())
                throw new ArgumentException("Every planning dependency must already exist on the same board.");
            foreach (var dependencyId in dependencyIds.Distinct())
                db.WorkItemDependencies.Add(new WorkItemDependency
                {
                    WorkItemId = item.Id,
                    DependsOnWorkItemId = dependencyId
                });
        }
        AddActivity(
            organizationId, board.Id, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            WorkItemActions.Create, "item.created", grant,
            new { columnId = column.Id }, now);
        AddReceipt(
            organizationId, installation.Id, WorkItemActions.Create,
            input.IdempotencyKey, item.Id, result);
        await QueueRealtimeAsync(
            organizationId, board.Id, item.Id, "item.created",
            item.Revision, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, board.Id, WorkItemActions.Create, grant,
            new { boardId = board.Id, itemId = item.Id, item.Kind, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItem> FinalizeItemDeliveryAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.FinalizeWorkItemDeliveryRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.FinalizeDelivery,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplayAsync<Wire.WorkItem>(
            installation.Id, WorkItemActions.FinalizeDelivery,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        var board = await db.WorkBoards
            .Include(x => x.OrchestrationPolicies).ThenInclude(x => x.Revisions).ThenInclude(x => x.Stages)
            .SingleOrDefaultAsync(x => x.Id == input.BoardId &&
                                       x.OrganizationId == organizationId &&
                                       x.ArchivedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("Board was not found.");
        var item = await db.CoreWorkTasks
            .Include(x => x.StageAssignments)
            .SingleOrDefaultAsync(x => x.Id == input.ItemId &&
                                       x.BoardId == input.BoardId &&
                                       x.OrganizationId == organizationId,
                cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        if (item.Kind is WorkItemKind.Initiative or WorkItemKind.Epic)
            throw new ArgumentException("Initiatives and epics do not have executable delivery specifications.");
        if (item.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The work item changed before delivery finalization.");
        var planning = DeserializeJson<Wire.WorkItemPlanningSpecification>(
            item.PlanningSpecificationJson)
            ?? throw new InvalidOperationException("The work item has no planning specification to finalize.");
        if (input.Delivery.RepositoryId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.Delivery.BaseBranch) ||
            input.Delivery.Requirements.Count == 0 ||
            input.Delivery.AcceptanceCriteria.Count == 0)
            throw new ArgumentException("Repository, base branch, requirements, and acceptance criteria are required.");
        if (!planning.Requirements.SequenceEqual(input.Delivery.Requirements, StringComparer.Ordinal) ||
            !planning.AcceptanceCriteria.SequenceEqual(input.Delivery.AcceptanceCriteria, StringComparer.Ordinal) ||
            !(planning.Constraints ?? []).SequenceEqual(input.Delivery.Constraints ?? [], StringComparer.Ordinal) ||
            !planning.DependencyItemIds.Order().SequenceEqual(input.Delivery.DependencyItemIds.Order()))
            throw new ArgumentException("Delivery finalization must preserve the approved planning requirements, acceptance criteria, constraints, and dependencies.");
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.Id == input.AccountableOrganizationUserId &&
                x.OrganizationId == organizationId && x.IsActive, cancellationToken))
            throw new ArgumentException("The accountable organization user is not active.");

        var publishedRevisionId = board.OrchestrationPolicies.SingleOrDefault()?.PublishedRevisionId
            ?? throw new InvalidOperationException(
                "Publish an orchestration policy before finalizing executable work.");
        var policyRevision = board.OrchestrationPolicies.Single().Revisions
            .Single(x => x.Id == publishedRevisionId);
        var policyStages = policyRevision.Stages.ToList();
        ValidateStageAssignments(
            true, policyRevision.InitialStageKey, policyStages, input.StageAssignments);

        item.AccountableOrganizationUserId = input.AccountableOrganizationUserId;
        item.DeliverySpecificationJson = JsonSerializer.Serialize(input.Delivery, JsonOptions);
        item.IsQaTrackingDefect = input.Delivery.IsQaTrackingDefect;
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        db.WorkItemStageAssignments.RemoveRange(item.StageAssignments);
        item.StageAssignments.Clear();
        foreach (var assignment in input.StageAssignments)
        {
            var entity = new WorkItemStageAssignment
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, BoardId = board.Id,
                WorkItemId = item.Id, StageKey = assignment.StageKey,
                PrincipalKind = Enum.Parse<WorkOrchestrationPrincipalKind>(assignment.PrincipalKind, true),
                OrganizationUserId = assignment.OrganizationUserId,
                AgentInstallationId = assignment.AgentInstallationId,
                PlatformAction = assignment.PlatformAction,
                CreatedAt = item.UpdatedAt
            };
            item.StageAssignments.Add(entity);
            db.Entry(entity).State = EntityState.Added;
        }
        await ReconcileActiveExecutionAssignmentsAsync(
            organizationId, board.Id, item, input.StageAssignments, cancellationToken);

        var result = ToAgentItem(item);
        AddActivity(
            organizationId, board.Id, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            WorkItemActions.FinalizeDelivery, "item.delivery.finalized", grant,
            new { input.Delivery.RepositoryId, input.Delivery.BaseBranch }, item.UpdatedAt);
        AddReceipt(
            organizationId, installation.Id, WorkItemActions.FinalizeDelivery,
            input.IdempotencyKey, item.Id, result);
        await QueueRealtimeAsync(
            organizationId, board.Id, item.Id, "item.delivery.finalized",
            item.Revision, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, board.Id,
            WorkItemActions.FinalizeDelivery, grant,
            new { boardId = board.Id, itemId = item.Id, input.Delivery.RepositoryId,
                  input.Delivery.BaseBranch, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task ReconcileActiveExecutionAssignmentsAsync(
        Guid organizationId,
        Guid boardId,
        WorkTask workItem,
        IReadOnlyList<Wire.WorkStageAssignment> assignments,
        CancellationToken cancellationToken)
    {
        var execution = await db.WorkSprintExecutions
            .Include(x => x.Items).ThenInclude(x => x.Stages)
            .SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.BoardId == boardId &&
                (x.Status == WorkSprintExecutionStatus.Active ||
                 x.Status == WorkSprintExecutionStatus.Paused) &&
                x.Items.Any(item => item.WorkItemId == workItem.Id), cancellationToken);
        if (execution is null) return;

        var itemExecution = execution.Items.Single(x => x.WorkItemId == workItem.Id);
        var snapshot = JsonSerializer.Deserialize<List<ExecutionAssignmentSnapshot>>(
                           execution.AssignmentSnapshotJson, JsonOptions) ?? [];
        var changed = false;
        foreach (var assignment in assignments)
        {
            var existing = snapshot.SingleOrDefault(x =>
                x.WorkItemId == workItem.Id && x.StageKey == assignment.StageKey);
            if (existing is null)
            {
                snapshot.Add(new ExecutionAssignmentSnapshot(
                    workItem.Id,
                    assignment.StageKey,
                    Enum.Parse<WorkOrchestrationPrincipalKind>(assignment.PrincipalKind, true),
                    assignment.OrganizationUserId,
                    assignment.AgentInstallationId,
                    assignment.PlatformAction));
                changed = true;
            }

            var blocked = itemExecution.Stages.SingleOrDefault(x =>
                x.StageKey == assignment.StageKey &&
                x.Status == WorkStageExecutionStatus.Blocked &&
                x.LastError == "staffing.assignment_missing");
            if (blocked is null) continue;

            var principal = Enum.Parse<WorkOrchestrationPrincipalKind>(
                assignment.PrincipalKind, true);
            blocked.PrincipalKind = principal;
            blocked.OrganizationUserId = assignment.OrganizationUserId;
            blocked.AgentInstallationId = assignment.AgentInstallationId;
            blocked.PlatformAction = assignment.PlatformAction;
            blocked.LastError = null;
            blocked.UpdatedAt = DateTimeOffset.UtcNow;
            var waitingForHuman = blocked.StageType == WorkOrchestrationStageType.ManualWork ||
                                  blocked.StageType == WorkOrchestrationStageType.MemberExecution &&
                                  principal == WorkOrchestrationPrincipalKind.Human;
            blocked.Status = waitingForHuman
                ? WorkStageExecutionStatus.WaitingForHuman
                : WorkStageExecutionStatus.Pending;
            itemExecution.Status = waitingForHuman
                ? WorkItemExecutionStatus.WaitingForHuman
                : WorkItemExecutionStatus.Pending;
            itemExecution.BlockedReason = null;
            itemExecution.UpdatedAt = blocked.UpdatedAt;
            workItem.Status = WorkTaskStatus.Assigned;
            changed = true;
        }

        if (!changed) return;
        execution.AssignmentSnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions);
        execution.Revision++;
        execution.UpdatedAt = DateTimeOffset.UtcNow;
        db.WorkOrchestrationEvents.Add(new WorkOrchestrationEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            SprintExecutionId = execution.Id,
            ItemExecutionId = itemExecution.Id,
            EventType = "orchestration.assignments.reconciled",
            DataJson = JsonSerializer.Serialize(new
            {
                workItemId = workItem.Id,
                stageKeys = assignments.Select(x => x.StageKey).OrderBy(x => x).ToArray()
            }, JsonOptions),
            OccurredAt = execution.UpdatedAt
        });
    }

    private async Task<Wire.WorkItemComment> CommentItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.CommentOnWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireForItemAsync(
            organizationId, installation.Id, WorkItemActions.Comment,
            input.BoardId, input.ItemId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        if (string.IsNullOrWhiteSpace(input.Body))
            throw new ArgumentException("Comment body is required.");
        if (input.Body.Trim().Length > 8192)
            throw new ArgumentException("Comment body cannot exceed 8192 characters.");
        var replay = await ReplayAsync<Wire.WorkItemComment>(
            installation.Id, WorkItemActions.Comment, input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.WorkItemId != input.ItemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item comment.");
            return replay;
        }

        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        var now = DateTimeOffset.UtcNow;
        var comment = new WorkItemComment
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            WorkItemId = item.Id,
            AuthorKind = GrantSubjectKind.AgentInstallation,
            AuthorSubjectId = installation.Id,
            AuthorDisplayName = string.IsNullOrWhiteSpace(session.AgentId)
                ? "Agent"
                : session.AgentId,
            Body = input.Body.Trim(),
            Kind = input.Kind?.Trim(),
            CoordinationSessionId = input.CoordinationSessionId,
            CausationId = input.CausationId?.Trim(),
            ArtifactDigest = input.ArtifactDigest?.Trim(),
            IdempotencyKey = input.IdempotencyKey,
            CreatedAt = now
        };
        var result = new Wire.WorkItemComment(
            comment.Id, comment.WorkItemId, comment.AuthorKind.ToString(),
            comment.AuthorSubjectId, comment.AuthorDisplayName, comment.Body,
            comment.Revision, comment.CreatedAt, comment.EditedAt)
        {
            Kind = comment.Kind,
            CoordinationSessionId = comment.CoordinationSessionId,
            CausationId = comment.CausationId,
            ArtifactDigest = comment.ArtifactDigest
        };
        db.WorkItemComments.Add(comment);
        AddActivity(
            organizationId, input.BoardId, item.Id, installation.Id,
            comment.AuthorDisplayName, WorkItemActions.Comment, "comment.created",
            grant, new { commentId = comment.Id }, now);
        AddReceipt(
            organizationId, installation.Id, WorkItemActions.Comment,
            input.IdempotencyKey, comment.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id, "comment.created",
            item.Revision, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId, WorkItemActions.Comment,
            grant, new { itemId = item.Id, commentId = comment.Id, input.IdempotencyKey },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItemCommentPage> ReadCommentsAsync(
        Guid organizationId,
        AgentInstallation installation,
        Wire.ReadWorkItemCommentsRequest input,
        CancellationToken cancellationToken)
    {
        await RequireForItemAsync(
            organizationId, installation.Id, WorkItemActions.ReadComments,
            input.BoardId, input.ItemId, cancellationToken);
        if (input.Page < 1 || input.PageSize is < 1 or > 200)
            throw new ArgumentException("Comment page and page size are out of range.");
        var query = db.WorkItemComments.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId && x.WorkItemId == input.ItemId && x.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(input.Kind))
            query = query.Where(x => x.Kind == input.Kind);
        var total = await query.CountAsync(cancellationToken);
        var comments = await query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((input.Page - 1) * input.PageSize).Take(input.PageSize)
            .Select(x => new Wire.WorkItemComment(
                x.Id, x.WorkItemId, x.AuthorKind.ToString(), x.AuthorSubjectId,
                x.AuthorDisplayName, x.Body, x.Revision, x.CreatedAt, x.EditedAt)
            {
                Kind = x.Kind,
                CoordinationSessionId = x.CoordinationSessionId,
                CausationId = x.CausationId,
                ArtifactDigest = x.ArtifactDigest
            }).ToListAsync(cancellationToken);
        return new Wire.WorkItemCommentPage(
            comments, input.Page, input.PageSize,
            input.Page * input.PageSize < total,
            comments.Count == 0 ? 0 : comments.Max(x => x.Revision));
    }

    private async Task<Wire.WorkItem> EstimateItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.EstimateWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        ValidatePoints(input.EstimatePoints, "Estimate");
        var grant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.Estimate,
            input.BoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplaySprintAsync<Wire.WorkItem>(
            installation.Id, WorkItemActions.Estimate,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Id != input.ItemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item.");
            return replay;
        }
        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        if (item.Revision != input.ExpectedItemRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {input.ExpectedItemRevision}, current revision is {item.Revision}.");
        var previousEstimate = item.EstimatePoints;
        item.EstimatePoints = input.EstimatePoints;
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var result = ToAgentItem(item);
        AddActivity(
            organizationId, input.BoardId, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            WorkItemActions.Estimate, "item.estimate.changed", grant,
            new { previousEstimate, estimatePoints = input.EstimatePoints },
            item.UpdatedAt, input.IdempotencyKey);
        AddSprintReceipt(
            organizationId, installation.Id, WorkItemActions.Estimate,
            input.IdempotencyKey, item.Id, result);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, item.SprintId, "item.estimate.changed",
            item.UpdatedAt, cancellationToken);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id, "item.estimate.changed",
            item.Revision, cancellationToken, sprintId: item.SprintId);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId,
            WorkItemActions.Estimate, grant,
            new { item.Id, previousEstimate, estimatePoints = input.EstimatePoints },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItemTransfer> TransferItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        Wire.TransferWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        if (input.BoardId == input.TargetBoardId)
            throw new ArgumentException(
                "Use move_work_item when the source and target board are the same.");
        var sourceGrant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.Transfer,
            input.BoardId, cancellationToken);
        var targetGrant = await RequireAsync(
            organizationId, installation.Id, WorkItemActions.Transfer,
            input.TargetBoardId, cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplayAsync<Wire.WorkItemTransfer>(
            installation.Id, WorkItemActions.Transfer,
            input.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (replay.Item.Id != input.ItemId)
                throw new InvalidOperationException(
                    "The idempotency key was already used for a different work item transfer.");
            return replay;
        }

        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        if (item.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {input.ExpectedRevision}, current revision is {item.Revision}.");
        if (item.ParentWorkTaskId.HasValue ||
            await db.CoreWorkTasks.AnyAsync(
                x => x.ParentWorkTaskId == item.Id, cancellationToken))
            throw new InvalidOperationException(
                "A hierarchical work item must be detached or transferred with its hierarchy.");

        var targetBoard = await db.WorkBoards
            .Include(x => x.Columns)
            .SingleOrDefaultAsync(x =>
                x.Id == input.TargetBoardId &&
                x.OrganizationId == organizationId &&
                x.ArchivedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("Target board was not found.");
        var targetColumn = input.TargetColumnId.HasValue
            ? targetBoard.Columns.SingleOrDefault(x => x.Id == input.TargetColumnId.Value)
            : targetBoard.Columns.OrderBy(x => x.Position)
                .FirstOrDefault(x => x.Category == WorkBoardColumnCategory.ToDo);
        if (targetColumn is null)
            throw new ArgumentException(
                "The target column does not belong to the target board.");
        await EnforceWipAsync(targetColumn, null, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var sourceColumnId = item.BoardColumnId;
        var sourceSprintId = item.SprintId;
        item.BoardId = targetBoard.Id;
        item.BoardColumnId = targetColumn.Id;
        item.SprintId = null;
        item.BoardRank = (await db.CoreWorkTasks
            .Where(x => x.BoardColumnId == targetColumn.Id)
            .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024;
        item.Status = StatusFor(targetColumn.Category);
        item.Revision++;
        item.UpdatedAt = now;
        var result = new Wire.WorkItemTransfer(
            input.BoardId, targetBoard.Id, ToAgentItem(item));
        AddActivity(
            organizationId, targetBoard.Id, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            WorkItemActions.Transfer, "item.transferred", targetGrant,
            new
            {
                sourceBoardId = input.BoardId,
                sourceColumnId,
                sourceSprintId,
                targetBoardId = targetBoard.Id,
                targetColumnId = targetColumn.Id,
                sourceGrantId = sourceGrant.GrantId
            },
            now,
            input.IdempotencyKey);
        AddReceipt(
            organizationId, installation.Id, WorkItemActions.Transfer,
            input.IdempotencyKey, item.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id, "item.transferred.out",
            item.Revision, cancellationToken, targetBoard.Id);
        await QueueRealtimeAsync(
            organizationId, targetBoard.Id, item.Id, "item.transferred.in",
            item.Revision, cancellationToken, input.BoardId);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, sourceSprintId, "item.transferred.out",
            now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, targetBoard.Id, WorkItemActions.Transfer,
            targetGrant,
            new
            {
                sourceBoardId = input.BoardId,
                targetBoardId = targetBoard.Id,
                targetColumnId = targetColumn.Id,
                itemId = item.Id,
                input.IdempotencyKey
            },
            cancellationToken, session);
        return result;
    }

    private async Task<Wire.WorkItem> TransitionItemAsync(
        AgentSession session,
        Guid organizationId,
        AgentInstallation installation,
        string action,
        Wire.TransitionWorkItemRequest input,
        CancellationToken cancellationToken)
    {
        var grant = await RequireForItemAsync(
            organizationId,
            installation.Id,
            action,
            input.BoardId,
            input.ItemId,
            cancellationToken);
        ValidateIdempotencyKey(input.IdempotencyKey);
        var replay = await ReplayAsync<Wire.WorkItem>(
            installation.Id, action, input.IdempotencyKey, cancellationToken);
        if (replay is not null) return replay;

        var item = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.Id == input.ItemId &&
            x.OrganizationId == organizationId &&
            x.BoardId == input.BoardId, cancellationToken)
            ?? throw new KeyNotFoundException("Work item was not found.");
        if (item.AssignedAgentInstallationId.HasValue &&
            item.AssignedAgentInstallationId != installation.Id)
            throw new UnauthorizedAccessException(
                "This work item is assigned to a different agent installation.");
        if (action == WorkItemActions.Complete &&
            item.AssignedAgentInstallationId == installation.Id &&
            !string.IsNullOrWhiteSpace(item.DevelopmentBriefJson))
        {
            var published = await (
                from publication in db.SourceControlPublications.AsNoTracking()
                join workspace in db.SourceControlWorkspaces.AsNoTracking()
                    on new { publication.OrganizationId, Id = publication.WorkspaceId }
                    equals new { workspace.OrganizationId, workspace.Id }
                where publication.OrganizationId == organizationId &&
                      workspace.AgentInstallationId == installation.Id &&
                      workspace.WorkItemId == item.Id &&
                      workspace.AssignmentRevision == item.AssignmentRevision &&
                      workspace.Status == SourceControlWorkspaceStatus.Published &&
                      publication.Status != SourceControlPublicationStatus.Superseded
                orderby publication.CreatedAt descending
                select publication)
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
            var validations = published is null
                ? []
                : JsonSerializer.Deserialize<IReadOnlyList<GitValidationResult>>(
                    published.ValidationResultsJson, JsonOptions) ?? [];
            if (validations.Count == 0 ||
                validations.Any(x => !x.Succeeded || x.ExitCode != 0))
                throw new InvalidOperationException(
                    "A development ticket can be completed only after successful validation and a current reviewable source publication.");
        }
        if (item.Revision != input.ExpectedRevision)
            throw new DbUpdateConcurrencyException(
                $"Expected work item revision {input.ExpectedRevision}, current revision is {item.Revision}.");
        var columns = await db.WorkBoardColumns
            .Where(x => x.BoardId == input.BoardId)
            .OrderBy(x => x.Position)
            .ToListAsync(cancellationToken);
        var target = ResolveTransitionColumn(action, input.TargetColumnId, item, columns);
        await EnforceWipAsync(target, item.Id, cancellationToken);

        var sourceColumnId = item.BoardColumnId;
        item.BoardColumnId = target.Id;
        item.BoardRank = (await db.CoreWorkTasks
            .Where(x => x.BoardColumnId == target.Id && x.Id != item.Id)
            .MaxAsync(x => (long?)x.BoardRank, cancellationToken) ?? 0) + 1024;
        item.Status = StatusFor(target.Category);
        item.Revision++;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var result = ToAgentItem(item);
        AddActivity(
            organizationId, input.BoardId, item.Id, installation.Id,
            string.IsNullOrWhiteSpace(session.AgentId) ? "Agent" : session.AgentId,
            action, EventTypeFor(action), grant,
            new { sourceColumnId, targetColumnId = target.Id, item.BoardRank },
            item.UpdatedAt);
        AddReceipt(
            organizationId, installation.Id, action,
            input.IdempotencyKey, item.Id, result);
        await QueueRealtimeAsync(
            organizationId, input.BoardId, item.Id, EventTypeFor(action),
            item.Revision, cancellationToken);
        await WorkSprintMetricsRecorder.RecordAsync(
            db, item.SprintId, EventTypeFor(action),
            item.UpdatedAt, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(
            organizationId, installation.Id, input.BoardId, action, grant,
            new
            {
                input.BoardId,
                item.Id,
                targetColumnId = target.Id,
                item.Revision,
                input.IdempotencyKey
            }, cancellationToken, session);
        return result;
    }

    private static WorkBoardColumn ResolveTransitionColumn(
        string action,
        Guid? targetColumnId,
        WorkTask item,
        IReadOnlyList<WorkBoardColumn> columns)
    {
        WorkBoardColumn? target = targetColumnId.HasValue
            ? columns.SingleOrDefault(x => x.Id == targetColumnId.Value)
            : action switch
            {
                WorkItemActions.Complete => columns.FirstOrDefault(x =>
                    x.Category == WorkBoardColumnCategory.Done),
                WorkItemActions.Start => columns.FirstOrDefault(x =>
                    x.Category == WorkBoardColumnCategory.InProgress),
                WorkItemActions.Cancel => columns.FirstOrDefault(x =>
                    x.Category == WorkBoardColumnCategory.Cancelled),
                WorkItemActions.Reopen => columns.FirstOrDefault(x =>
                    x.Category == WorkBoardColumnCategory.ToDo),
                _ => null
            };
        if (target is null)
            throw new ArgumentException("A valid target column is required for this transition.");
        var valid = action switch
        {
            WorkItemActions.Complete => target.Category == WorkBoardColumnCategory.Done,
            WorkItemActions.Start =>
                item.Status is WorkTaskStatus.Ready or WorkTaskStatus.Assigned &&
                target.Category == WorkBoardColumnCategory.InProgress,
            WorkItemActions.Cancel => target.Category == WorkBoardColumnCategory.Cancelled,
            WorkItemActions.Reopen =>
                item.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled &&
                target.Category is WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.InProgress,
            WorkItemActions.Move =>
                item.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) &&
                target.Category is WorkBoardColumnCategory.ToDo or WorkBoardColumnCategory.InProgress,
            _ => false
        };
        if (!valid)
            throw new UnauthorizedAccessException(
                $"The '{action}' grant cannot perform the requested state transition.");
        return target;
    }

    private void AddActivity(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid installationId,
        string actorDisplayName,
        string action,
        string eventType,
        ScopedAuthorizationDecision grant,
        object data,
        DateTimeOffset occurredAt,
        string? idempotencyKey = null) =>
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            BoardId = boardId,
            WorkItemId = itemId,
            EventType = eventType,
            Action = action,
            ActorKind = GrantSubjectKind.AgentInstallation,
            ActorSubjectId = installationId,
            ActorDisplayName = actorDisplayName,
            AuthorizingGrantId = grant.GrantId,
            AuthorizingGrantRevision = grant.GrantRevision,
            IdempotencyKey = idempotencyKey,
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            OccurredAt = occurredAt
        });

    private async Task QueueRealtimeAsync(
        Guid organizationId,
        Guid boardId,
        Guid? itemId,
        string changeType,
        long revision,
        CancellationToken cancellationToken,
        Guid? relatedBoardId = null,
        Guid? sprintId = null)
    {
        var recipients = await ResolveRealtimeRecipientsAsync(
            organizationId, boardId, cancellationToken,
            requireSprintRead: sprintId.HasValue && !itemId.HasValue);
        var now = DateTimeOffset.UtcNow;
        db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            RecipientOrganizationUserIdsJson =
                JsonSerializer.Serialize(recipients, JsonOptions),
            EventType = AppRealtimeEvents.WorkBoardChanged,
            Subject = $"organizations/{organizationId:D}/work/boards/{boardId:D}",
            DataJson = JsonSerializer.Serialize(new
            {
                boardId,
                itemId,
                changeType,
                revision,
                relatedBoardId,
                sprintId
            }, JsonOptions),
            Status = ApplicationRealtimeOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
    }

    private async Task<IReadOnlyList<Guid>> ResolveRealtimeRecipientsAsync(
        Guid organizationId,
        Guid boardId,
        CancellationToken cancellationToken,
        bool requireSprintRead = false)
    {
        var now = DateTimeOffset.UtcNow;
        var grants = await db.ScopedActionGrants.AsNoTracking().Where(x =>
                x.OrganizationId == organizationId &&
                x.SubjectKind == GrantSubjectKind.OrganizationUser &&
                x.RevokedAt == null &&
                (!x.ExpiresAt.HasValue || x.ExpiresAt > now) &&
                (x.ScopeKind == GrantScopeKind.Organization ||
                 (x.ScopeKind == GrantScopeKind.Board && x.ScopeId == boardId)) &&
                (x.Action == WorkBoardActions.Read ||
                 x.Action == WorkItemActions.Read ||
                 x.Action == WorkSprintActions.Read))
            .Select(x => new { x.SubjectId, x.Action })
            .ToListAsync(cancellationToken);
        var boardReaders = grants.Where(x => x.Action == WorkBoardActions.Read)
            .Select(x => x.SubjectId).ToHashSet();
        var detailReaders = grants.Where(x => x.Action ==
                (requireSprintRead ? WorkSprintActions.Read : WorkItemActions.Read))
            .Select(x => x.SubjectId).ToHashSet();
        boardReaders.IntersectWith(detailReaders);
        return await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                boardReaders.Contains(x.Id) &&
                x.OrganizationId == organizationId &&
                x.EmployeeType == EmployeeType.Human &&
                x.IsActive)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<ScopedAuthorizationDecision> RequireAsync(
        Guid organizationId,
        Guid installationId,
        string action,
        Guid? boardId,
        CancellationToken cancellationToken)
    {
        var decision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.AgentInstallation,
            installationId,
            action,
            boardId.HasValue ? GrantScopeKind.Board : GrantScopeKind.Organization,
            boardId,
            cancellationToken);
        if (decision.Allowed) return decision;
        if (boardId.HasValue)
        {
            var teamId = await db.WorkBoards.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.Id == boardId.Value)
                .Select(x => x.TeamId)
                .SingleOrDefaultAsync(cancellationToken);
            if (teamId.HasValue)
            {
                decision = await authorization.AuthorizeAsync(
                    organizationId,
                    GrantSubjectKind.AgentInstallation,
                    installationId,
                    action,
                    GrantScopeKind.Team,
                    teamId,
                    cancellationToken);
                if (decision.Allowed) return decision;
            }
        }
        await WriteAuditAsync(
            organizationId, installationId, boardId, action, null,
            new { action, boardId }, cancellationToken, outcome: "Denied");
        throw new UnauthorizedAccessException(
            $"The installation does not have '{action}' on the requested scope.");
    }

    private async Task RejectPersonalBoardReferencesAsync(
        Guid organizationId,
        RequestCapability request,
        CancellationToken cancellationToken)
    {
        if (request.Payload.IsEmpty)
            return;

        var payload = request.Payload.ToElement();
        if (payload.ValueKind != JsonValueKind.Object)
            return;

        var boardIds = payload.EnumerateObject()
            .Where(x => x.Name.EndsWith("BoardId", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value.ValueKind == JsonValueKind.String &&
                         Guid.TryParse(x.Value.GetString(), out var boardId)
                ? boardId
                : Guid.Empty)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToArray();
        if (boardIds.Length == 0)
            return;

        if (await db.WorkBoards.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId &&
                x.Kind == WorkBoardKind.Personal &&
                boardIds.Contains(x.Id), cancellationToken))
            throw new UnauthorizedAccessException(
                "Personal to-do boards are accessible only through personal-todo actions.");
    }

    private async Task<ScopedAuthorizationDecision> RequireForItemAsync(
        Guid organizationId,
        Guid installationId,
        string action,
        Guid boardId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var itemDecision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.AgentInstallation,
            installationId,
            action,
            GrantScopeKind.WorkItem,
            itemId,
            cancellationToken);
        return itemDecision.Allowed
            ? itemDecision
            : await RequireAsync(
                organizationId,
                installationId,
                action,
                boardId,
                cancellationToken);
    }

    private async Task<List<ScopedActionGrant>> ActiveGrantsAsync(
        Guid organizationId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.ScopedActionGrants.AsNoTracking().Where(x =>
            x.OrganizationId == organizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == installationId &&
            x.RevokedAt == null &&
            (!x.ExpiresAt.HasValue || x.ExpiresAt > now))
            .ToListAsync(cancellationToken);
    }

    private async Task EnforceWipAsync(
        WorkBoardColumn column,
        Guid? excludedItemId,
        CancellationToken cancellationToken)
    {
        if (column.WipPolicy != WorkBoardWipPolicy.HardLimit || !column.WipLimit.HasValue)
            return;
        var count = await db.CoreWorkTasks.CountAsync(x =>
            x.BoardColumnId == column.Id &&
            (!excludedItemId.HasValue || x.Id != excludedItemId.Value), cancellationToken);
        if (count >= column.WipLimit.Value)
            throw new InvalidOperationException(
                $"Column '{column.Name}' has reached its WIP limit of {column.WipLimit.Value}.");
    }

    private async Task<T?> ReplayAsync<T>(
        Guid installationId,
        string action,
        string idempotencyKey,
        CancellationToken cancellationToken) where T : class
    {
        var json = await db.WorkItemMutationReceipts.AsNoTracking()
            .Where(x =>
                x.AgentInstallationId == installationId &&
                x.Action == action &&
                x.IdempotencyKey == idempotencyKey)
            .Select(x => x.ResultJson)
            .SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private void AddReceipt<T>(
        Guid organizationId,
        Guid installationId,
        string action,
        string idempotencyKey,
        Guid resourceId,
        T result) =>
        db.WorkItemMutationReceipts.Add(new WorkItemMutationReceipt
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            AgentInstallationId = installationId,
            Action = action,
            IdempotencyKey = idempotencyKey,
            ResourceId = resourceId,
            ResultJson = JsonSerializer.Serialize(result, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        });

    private async Task<T?> ReplaySprintAsync<T>(
        Guid installationId,
        string action,
        string idempotencyKey,
        CancellationToken cancellationToken) where T : class
    {
        var json = await db.WorkSprintMutationReceipts.AsNoTracking()
            .Where(x =>
                x.ActorKind == GrantSubjectKind.AgentInstallation &&
                x.ActorSubjectId == installationId &&
                x.Action == action &&
                x.IdempotencyKey == idempotencyKey)
            .Select(x => x.ResultJson)
            .SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private void AddSprintReceipt<T>(
        Guid organizationId,
        Guid installationId,
        string action,
        string idempotencyKey,
        Guid resourceId,
        T result) =>
        db.WorkSprintMutationReceipts.Add(new WorkSprintMutationReceipt
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorKind = GrantSubjectKind.AgentInstallation,
            ActorSubjectId = installationId,
            Action = action,
            IdempotencyKey = idempotencyKey,
            ResourceId = resourceId,
            ResultJson = JsonSerializer.Serialize(result, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow
        });

    private Task WriteAuditAsync(
        Guid organizationId,
        Guid installationId,
        Guid? boardId,
        string action,
        ScopedAuthorizationDecision? grant,
        object metadata,
        CancellationToken cancellationToken,
        AgentSession? session = null,
        string outcome = "Completed") =>
        audit.AppendAsync(new AuditEventWriteRequest(
            action,
            "WorkManagement",
            "Inbound",
            outcome,
            organizationId,
            boardId.HasValue ? "WorkBoard" : "Organization",
            boardId ?? organizationId,
            $"{outcome} {action}.",
            JsonSerializer.Serialize(new
            {
                action,
                grantId = grant?.GrantId,
                grantRevision = grant?.GrantRevision,
                data = metadata
            }, JsonOptions),
            Actor: new AuditActor(
                "Agent",
                true,
                AgentId: session?.AgentId,
                InstallationId: installationId,
                RuntimeInstanceId: ParseGuid(session?.RuntimeInstanceId),
                TickId: ParseGuid(session?.TickId),
                SessionId: session?.SessionId)),
            cancellationToken);

    private static T Read<T>(RequestCapability request) =>
        JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions)
        ?? throw new ArgumentException("The capability payload is required.");

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
            throw new ArgumentException("A non-empty idempotency key of at most 160 characters is required.");
    }

    private static void ValidatePoints(decimal? points, string label)
    {
        if (points is < 0 or > 999999.99m)
            throw new ArgumentException(
                $"{label} points must be between 0 and 999999.99.");
    }

    private static WorkTaskStatus StatusFor(WorkBoardColumnCategory category) => category switch
    {
        WorkBoardColumnCategory.ToDo => WorkTaskStatus.Ready,
        WorkBoardColumnCategory.InProgress => WorkTaskStatus.Running,
        WorkBoardColumnCategory.Done => WorkTaskStatus.Completed,
        WorkBoardColumnCategory.Cancelled => WorkTaskStatus.Cancelled,
        _ => WorkTaskStatus.Ready
    };

    private static string EventTypeFor(string action) => action switch
    {
        WorkItemActions.Start => "item.started",
        WorkItemActions.Complete => "item.completed",
        WorkItemActions.Cancel => "item.cancelled",
        WorkItemActions.Reopen => "item.reopened",
        _ => "item.moved"
    };

    private static string SprintEventTypeFor(string action) => action switch
    {
        WorkSprintActions.Start => "sprint.started",
        WorkSprintActions.Complete => "sprint.completed",
        WorkSprintActions.Cancel => "sprint.cancelled",
        _ => "sprint.changed"
    };

    private static WorkBoardColumn NewColumn(
        string name,
        WorkBoardColumnCategory category,
        int position) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = category,
            Position = position,
            WipPolicy = WorkBoardWipPolicy.Disabled
        };

    private static Wire.WorkItem ToAgentItem(WorkTask item) => new(
        item.Id,
        item.BoardColumnId!.Value,
        item.ParentWorkTaskId,
        item.SprintId,
        item.Kind.ToString(),
        item.Title,
        item.Description,
        item.Status.ToString(),
        item.Priority.ToString(),
        item.EstimatePoints,
        item.BoardRank,
        item.Revision,
        item.DueDate,
        item.AssignedWorkerId,
        item.AssignedEmployeeId,
        item.AssignedAgentInstallationId,
        null,
        DeserializeDevelopmentBrief(item.DevelopmentBriefJson))
    {
        Planning = DeserializeJson<Wire.WorkItemPlanningSpecification>(
            item.PlanningSpecificationJson),
        Quality = DeserializeJson<Wire.SoftwareQualityBrief>(item.QualityBriefJson),
        Delivery = DeserializeJson<Wire.WorkItemDeliverySpecification>(
            item.DeliverySpecificationJson),
        Identifier = item.Identifier,
        AccountableOrganizationUserId = item.AccountableOrganizationUserId,
        Mentions = WorkItemMentionCodec.Deserialize(item.StructuredMentionsJson),
        StageAssignments = item.StageAssignments.Select(x => new Wire.WorkStageAssignment(
            x.StageKey, x.PrincipalKind.ToString(), x.OrganizationUserId,
            x.AgentInstallationId, x.PlatformAction)).ToList()
    };

    private static void ValidateStageAssignments(
        bool executable,
        string? initialStageKey,
        IReadOnlyList<WorkOrchestrationStage> stages,
        IReadOnlyList<Wire.WorkStageAssignment> assignments)
    {
        if (!executable) return;
        if (stages.Count == 0)
            throw new ArgumentException("The board must have a published orchestration policy before executable work is created.");
        var required = stages.Where(x => x.Type is WorkOrchestrationStageType.AgentExecution or
                WorkOrchestrationStageType.ManualWork or WorkOrchestrationStageType.MemberExecution or
                WorkOrchestrationStageType.ManagerApproval or
                WorkOrchestrationStageType.TrustedPlatformAction)
            .ToDictionary(x => x.Key, StringComparer.Ordinal);
        if (assignments.Select(x => x.StageKey).Distinct(StringComparer.Ordinal).Count() != assignments.Count ||
            assignments.Any(x => !required.ContainsKey(x.StageKey)))
            throw new ArgumentException("A stage may have only one assignment and must belong to the published work policy.");
        foreach (var assignment in assignments)
        {
            var stage = required[assignment.StageKey];
            if (!Enum.TryParse<WorkOrchestrationPrincipalKind>(assignment.PrincipalKind, true, out var principal))
                throw new ArgumentException($"Stage '{assignment.StageKey}' has an invalid principal kind.");
            if (principal == WorkOrchestrationPrincipalKind.Unassigned)
                throw new ArgumentException($"Stage '{assignment.StageKey}' must be omitted until it has an assignee.");
            if (stage.Type == WorkOrchestrationStageType.AgentExecution &&
                (principal != WorkOrchestrationPrincipalKind.AgentInstallation || !assignment.AgentInstallationId.HasValue))
                throw new ArgumentException($"Agent stage '{stage.Key}' requires an exact installation.");
            if (stage.Type == WorkOrchestrationStageType.ManualWork &&
                (principal != WorkOrchestrationPrincipalKind.Human || !assignment.OrganizationUserId.HasValue))
                throw new ArgumentException($"Manual stage '{stage.Key}' requires a human assignee.");
            if (stage.Type == WorkOrchestrationStageType.MemberExecution &&
                !((principal == WorkOrchestrationPrincipalKind.Human && assignment.OrganizationUserId.HasValue) ||
                  (principal == WorkOrchestrationPrincipalKind.AgentInstallation && assignment.AgentInstallationId.HasValue)))
                throw new ArgumentException($"Member stage '{stage.Key}' requires an exact human or agent assignee.");
            if (stage.Type == WorkOrchestrationStageType.ManagerApproval && principal != WorkOrchestrationPrincipalKind.BoardManager)
                throw new ArgumentException($"Approval stage '{stage.Key}' must use the board manager.");
            if (stage.Type == WorkOrchestrationStageType.TrustedPlatformAction &&
                (principal != WorkOrchestrationPrincipalKind.PlatformAction || string.IsNullOrWhiteSpace(assignment.PlatformAction)))
                throw new ArgumentException($"Platform stage '{stage.Key}' requires a trusted action.");
        }
        var initialStage = string.IsNullOrWhiteSpace(initialStageKey)
            ? null
            : stages.SingleOrDefault(x => x.Key == initialStageKey);
        if (initialStage is null)
            throw new ArgumentException("The published work policy does not have a valid initial work stage.");
        if ((initialStage.Type is WorkOrchestrationStageType.AgentExecution or
                WorkOrchestrationStageType.ManualWork or WorkOrchestrationStageType.MemberExecution) &&
            assignments.All(x => !x.StageKey.Equals(initialStageKey, StringComparison.Ordinal)))
            throw new ArgumentException(
                $"Initial stage '{initialStageKey}' requires an exact assignment before work is executable.");
    }

    private async Task<string> ResolveBoardKeyAsync(
        Guid organizationId, string? requested, string name, CancellationToken cancellationToken)
    {
        var seed = string.IsNullOrWhiteSpace(requested)
            ? new string(name.Where(char.IsLetterOrDigit).Take(6).ToArray())
            : requested.Trim();
        seed = seed.ToUpperInvariant();
        if (seed.Length < 2) seed = $"B{seed}1";
        if (seed.Length > 12 || !char.IsLetter(seed[0]) || seed.Any(x => !char.IsLetterOrDigit(x)))
            throw new ArgumentException("Board key must be 2-12 uppercase letters or digits and begin with a letter.");
        var candidate = seed;
        var suffix = 2;
        while (await db.WorkBoards.AsNoTracking().AnyAsync(
                   x => x.OrganizationId == organizationId && x.Key == candidate, cancellationToken))
        {
            var tail = (suffix++).ToString();
            candidate = $"{seed[..Math.Min(seed.Length, 12 - tail.Length)]}{tail}";
        }
        return candidate;
    }

    private async Task<Wire.WorkItem> ToAgentItemAsync(
        WorkTask item,
        CancellationToken cancellationToken)
    {
        var employeeName = item.AssignedEmployeeId.HasValue
            ? await db.CoreOrganizationUsers.AsNoTracking()
                .Where(x => x.Id == item.AssignedEmployeeId)
                .Select(x => x.DisplayName)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        var result = ToAgentItem(item);
        return result with { AssignedDisplayName = employeeName };
    }

    private static Wire.SoftwareDevelopmentBrief? DeserializeDevelopmentBrief(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Wire.SoftwareDevelopmentBrief>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? DeserializeJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static Wire.WorkSprint ToAgentSprint(
        WorkSprint sprint,
        int itemCount,
        int completedItemCount,
        decimal plannedPoints = 0,
        decimal completedPoints = 0) => new(
        sprint.Id, sprint.BoardId, sprint.Name, sprint.Goal,
        sprint.Status.ToString(), sprint.StartsAt, sprint.EndsAt,
        sprint.StartedAt, sprint.CompletedAt, sprint.CapacityPoints,
        itemCount, completedItemCount, plannedPoints, completedPoints, sprint.Revision)
    {
        Sequence = sprint.Sequence
    };

    private static Wire.WorkSprintReport ToWireReport(
        WorkSprintReportResponse report) => new(
        report.BoardId,
        report.CompletedSprintCount,
        report.AverageVelocity,
        report.TotalCompletedPoints,
        report.AverageCapacityUtilizationPercent,
        report.Sprints.Select(snapshot => new Wire.WorkSprintSnapshot(
            snapshot.Id,
            snapshot.SprintId,
            snapshot.SprintName,
            snapshot.Goal,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            snapshot.CapacityPoints,
            snapshot.CommittedItemCount,
            snapshot.CompletedItemCount,
            snapshot.CommittedPoints,
            snapshot.CompletedPoints,
            snapshot.Items.Select(item => new Wire.WorkSprintSnapshotItem(
                item.ItemId,
                item.Kind,
                item.Title,
                item.Status,
                item.EstimatePoints,
                item.Completed)).ToList())).ToList(),
        report.Burndown.Select(series => new Wire.WorkSprintBurndownSeries(
            series.SprintId,
            series.SprintName,
            series.Status,
            series.CapacityPoints,
            series.Points.Select(point => new Wire.WorkSprintMetricPoint(
                point.Id,
                point.OccurredAt,
                point.Reason,
                point.ScopeItemCount,
                point.CompletedItemCount,
                point.ScopePoints,
                point.CompletedPoints,
                point.RemainingPoints)).ToList())).ToList(),
        report.ActiveForecast is null
            ? null
            : new Wire.WorkSprintForecast(
                report.ActiveForecast.SprintId,
                report.ActiveForecast.SprintName,
                report.ActiveForecast.RemainingPoints,
                report.ActiveForecast.AverageVelocity,
                report.ActiveForecast.ProjectedSprintsRequired,
                report.ActiveForecast.IsOverCapacity));

    private static Guid? ParseGuid(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;

    private sealed record ExecutionAssignmentSnapshot(
        Guid WorkItemId,
        string StageKey,
        WorkOrchestrationPrincipalKind PrincipalKind,
        Guid? OrganizationUserId,
        Guid? AgentInstallationId,
        string? PlatformAction);

    private static CapabilityResult Success<T>(string requestId, T payload) => new()
    {
        RequestId = requestId,
        Succeeded = true,
        ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))
    };

    private static CapabilityResult Failure(
        string requestId,
        PlatformCapabilityErrorCode code,
        string message) => new()
        {
            RequestId = requestId,
            Succeeded = false,
            ContentType = "application/json",
            Error = message,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(
                new PlatformCapabilityError(code, message), JsonOptions))
        };

}
