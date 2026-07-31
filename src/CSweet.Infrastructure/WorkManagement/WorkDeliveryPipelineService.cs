using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// Durable, single-ticket delivery coordinator. Agent judgment is accepted only
/// through scoped assignments; sequencing, retries and merge eligibility stay here.
/// </summary>
public sealed class WorkDeliveryPipelineService(
    CSweetDbContext db,
    IScopedActionAuthorizationService authorization,
    IPluginSecretStore secrets,
    IAgentRuntimeManager runtimeManager,
    IHttpClientFactory httpClients) : IWorkDeliveryPipelineService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DeliveryPipelineResponse?> GetAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken = default)
    {
        await RequireBoardManagerAsync(organizationId, boardId, applicationUserId, cancellationToken);
        var pipeline = await db.WorkDeliveryPipelines.AsNoTracking().SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.BoardId == boardId, cancellationToken);
        return pipeline is null ? null : ToResponse(pipeline);
    }

    public async Task<DeliveryPipelineResponse> ConfigureAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        ConfigureDeliveryPipelineRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireBoardManagerAsync(organizationId, boardId, applicationUserId, cancellationToken);
        var board = await db.WorkBoards.SingleOrDefaultAsync(
            x => x.Id == boardId && x.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("The board was not found.");
        await ValidateConfigurationAsync(organizationId, boardId, request, cancellationToken);
        var pipeline = await db.WorkDeliveryPipelines.SingleOrDefaultAsync(
            x => x.BoardId == boardId, cancellationToken);
        if (pipeline is null)
        {
            if (request.ExpectedRevision != 0)
                throw new DbUpdateConcurrencyException("The delivery pipeline does not exist.");
            pipeline = new WorkDeliveryPipeline
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                BoardId = board.Id,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.WorkDeliveryPipelines.Add(pipeline);
        }
        else if (pipeline.Revision != request.ExpectedRevision)
        {
            throw new DbUpdateConcurrencyException(
                "The delivery pipeline changed before it could be configured.");
        }

        pipeline.DeveloperInstallationId = request.DeveloperInstallationId;
        pipeline.QualityInstallationId = request.QualityInstallationId;
        pipeline.DevelopmentColumnId = request.DevelopmentColumnId;
        pipeline.QualityColumnId = request.QualityColumnId;
        pipeline.DoneColumnId = request.DoneColumnId;
        pipeline.RepositoryConnectionId = request.RepositoryConnectionId;
        pipeline.BaseBranch = RequireGitReference(request.BaseBranch);
        pipeline.MergeStrategy = NormalizeMergeStrategy(request.MergeStrategy);
        pipeline.IsEnabled = request.IsEnabled;
        pipeline.Status = request.IsEnabled
            ? DeliveryPipelineStatuses.Idle
            : DeliveryPipelineStatuses.Disabled;
        pipeline.Stage = "Idle";
        pipeline.LastError = null;
        pipeline.ResumeAction = null;
        pipeline.Revision++;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(pipeline);
    }

    public async Task<DeliveryPipelineResponse> ChangeStateAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        string action,
        ChangeDeliveryPipelineStateRequest request,
        CancellationToken cancellationToken = default)
    {
        await RequireBoardManagerAsync(organizationId, boardId, applicationUserId, cancellationToken);
        var pipeline = await db.WorkDeliveryPipelines.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.BoardId == boardId, cancellationToken)
            ?? throw new KeyNotFoundException("The delivery pipeline was not found.");
        if (pipeline.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The delivery pipeline revision is stale.");

        switch (action.ToLowerInvariant())
        {
            case "enable":
            case "resume":
                pipeline.IsEnabled = true;
                pipeline.Status = DeliveryPipelineStatuses.Idle;
                if (pipeline.ActiveWorkItemId.HasValue &&
                    pipeline.Stage == "QA")
                {
                    var item = await db.CoreWorkTasks.SingleAsync(
                        x => x.Id == pipeline.ActiveWorkItemId, cancellationToken);
                    await AssignAsync(
                        pipeline,
                        item,
                        pipeline.QualityInstallationId,
                        pipeline.QualityColumnId,
                        [WorkItemActions.Read, WorkItemActions.QualitySubmit],
                        $"pipeline:{pipeline.Id:D}:qa-resume:{pipeline.Revision + 1}",
                        cancellationToken);
                    pipeline.Status = DeliveryPipelineStatuses.Running;
                }
                pipeline.LastError = null;
                pipeline.ResumeAction = null;
                break;
            case "pause":
                pipeline.Status = DeliveryPipelineStatuses.Paused;
                pipeline.LastError = string.IsNullOrWhiteSpace(request.Reason)
                    ? "Paused by a board manager."
                    : request.Reason.Trim();
                pipeline.ResumeAction = "Review the active delivery and resume the pipeline.";
                break;
            case "disable":
                pipeline.IsEnabled = false;
                pipeline.Status = DeliveryPipelineStatuses.Disabled;
                pipeline.ResumeAction = null;
                break;
            default:
                throw new ArgumentException("Pipeline action must be enable, pause, resume, or disable.");
        }
        pipeline.Revision++;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (action.Equals("resume", StringComparison.OrdinalIgnoreCase) &&
            pipeline.ActiveWorkItemId.HasValue &&
            pipeline.Stage == "QA")
            await runtimeManager.EnsureRuntimeQueuedAsync(
                pipeline.QualityInstallationId,
                $"QA assignment resumed for ticket {pipeline.ActiveWorkItemId:D}.",
                cancellationToken: cancellationToken);
        return ToResponse(pipeline);
    }

    public async Task<int> PulseAsync(CancellationToken cancellationToken = default)
    {
        var pipelineIds = await db.WorkDeliveryPipelines.AsNoTracking()
            .Where(x => x.IsEnabled && x.Status != DeliveryPipelineStatuses.Paused)
            .OrderBy(x => x.UpdatedAt)
            .Select(x => x.Id)
            .Take(25)
            .ToListAsync(cancellationToken);
        var advanced = 0;
        foreach (var pipelineId in pipelineIds)
        {
            try
            {
                if (await AdvanceAsync(pipelineId, cancellationToken))
                    advanced++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (exception is HttpRequestException or TimeoutException or DbUpdateException)
                    await RecordInfrastructureFailureAsync(
                        pipelineId, exception.Message, cancellationToken);
                else
                    await PauseAsync(
                        pipelineId,
                        $"Coordinator transition failed: {Bounded(exception.Message)}",
                        "Correct the configuration or infrastructure failure, then resume.",
                        cancellationToken);
            }
        }
        return advanced;
    }

    public async Task<bool> RoutePublishedDevelopmentAsync(
        Guid organizationId,
        Guid boardId,
        Guid itemId,
        Guid developerInstallationId,
        long expectedItemRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var pipeline = await db.WorkDeliveryPipelines.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.BoardId == boardId &&
            x.IsEnabled &&
            x.ActiveWorkItemId == itemId, cancellationToken);
        if (pipeline is null)
            return false;
        if (pipeline.DeveloperInstallationId != developerInstallationId)
            throw new UnauthorizedAccessException("Only the configured developer may publish this delivery.");

        var item = await db.CoreWorkTasks.SingleAsync(x =>
            x.Id == itemId && x.BoardId == boardId, cancellationToken);
        if (item.Revision != expectedItemRevision)
            throw new DbUpdateConcurrencyException("The work item changed before QA routing.");
        var workspace = await db.GitTicketWorkspaces.AsNoTracking()
            .Where(x =>
                x.OrganizationId == organizationId &&
                x.WorkItemId == itemId &&
                x.AgentInstallationId == developerInstallationId &&
                x.Status == GitTicketWorkspaceStatus.Published &&
                x.CommitSha != null &&
                x.PullRequestUrl != null)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "A published branch and pull request are required before QA.");
        if (workspace.RepositoryConnectionId != pipeline.RepositoryConnectionId)
            throw new InvalidOperationException("The published workspace uses the wrong repository.");

        var specification = DeserializeSpecification(item)
            ?? throw new InvalidOperationException("The ticket has no delivery specification.");
        var maximumCycles = await ReadMaximumQaCyclesAsync(
            pipeline.QualityInstallationId, cancellationToken);
        var cycle = item.QualityCycle + 1;
        item.QualityCycle = cycle;
        item.QualityBriefJson = JsonSerializer.Serialize(new SoftwareQualityBrief(
            pipeline.RepositoryConnectionId,
            pipeline.BaseBranch,
            workspace.BranchName,
            workspace.CommitSha!,
            new Uri(workspace.PullRequestUrl!),
            specification.Requirements,
            specification.AcceptanceCriteria,
            cycle,
            maximumCycles,
            specification.Constraints), JsonOptions);
        await AssignAsync(
            pipeline,
            item,
            pipeline.QualityInstallationId,
            pipeline.QualityColumnId,
            [WorkItemActions.Read, WorkItemActions.QualitySubmit],
            $"pipeline:{pipeline.Id:D}:item:{item.Id:D}:qa:{cycle}",
            cancellationToken);
        pipeline.Stage = "QA";
        pipeline.Status = DeliveryPipelineStatuses.Running;
        pipeline.QualityCycle = cycle;
        pipeline.MergeStatus = DeliveryMergeStatuses.None;
        pipeline.SourcePullRequestUrl = workspace.PullRequestUrl;
        pipeline.SourceCommitSha = workspace.CommitSha;
        pipeline.Revision++;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await runtimeManager.EnsureRuntimeQueuedAsync(
            pipeline.QualityInstallationId,
            $"Quality cycle {cycle} assigned for ticket {item.Id:D}.",
            cancellationToken: cancellationToken);
        return true;
    }

    public async Task<QualityRunResult> SubmitQualityAsync(
        Guid organizationId,
        Guid qualityInstallationId,
        SubmitQualityResultRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateQualityResult(request);
        var replay = await db.WorkQualityRuns.AsNoTracking().SingleOrDefaultAsync(x =>
            x.QualityInstallationId == qualityInstallationId &&
            x.WorkItemId == request.ItemId &&
            x.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (replay is not null)
            return DeserializeRunResult(replay);

        var pipeline = await db.WorkDeliveryPipelines.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.BoardId == request.BoardId &&
            x.IsEnabled &&
            x.ActiveWorkItemId == request.ItemId, cancellationToken)
            ?? throw new InvalidOperationException("No active delivery assignment matches this QA result.");
        if (pipeline.QualityInstallationId != qualityInstallationId)
            throw new UnauthorizedAccessException("The quality result targets another installation.");
        var item = await db.CoreWorkTasks.SingleAsync(
            x => x.Id == request.ItemId && x.BoardId == request.BoardId, cancellationToken);
        if (item.AssignedAgentInstallationId != qualityInstallationId ||
            item.AssignmentRevision != request.AssignmentRevision)
            throw new DbUpdateConcurrencyException("The QA assignment revision is stale.");
        var brief = DeserializeQualityBrief(item)
            ?? throw new InvalidOperationException("The QA assignment has no quality brief.");
        if (!string.Equals(brief.SourceCommitSha, request.SourceCommitSha, StringComparison.Ordinal))
            throw new InvalidOperationException("The QA result does not match the assigned commit.");
        if (request.Criteria.Count != brief.AcceptanceCriteria.Count ||
            !brief.AcceptanceCriteria.All(expected =>
                request.Criteria.Any(actual =>
                    string.Equals(
                        actual.Criterion.Trim(),
                        expected.Trim(),
                        StringComparison.Ordinal))))
            throw new InvalidOperationException(
                "The QA result does not cover the assigned acceptance criteria exactly.");
        var currentWorkspace = await db.GitTicketWorkspaces.AsNoTracking()
            .Where(x => x.WorkItemId == item.Id && x.CommitSha != null)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstAsync(cancellationToken);
        if (!string.Equals(currentWorkspace.CommitSha, request.SourceCommitSha, StringComparison.Ordinal))
        {
            pipeline.Status = DeliveryPipelineStatuses.Paused;
            pipeline.LastError = "The pull-request head changed after QA inspected it.";
            pipeline.ResumeAction = "Review the changed PR head and resume for a new QA assignment.";
            pipeline.Revision++;
            pipeline.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(pipeline.LastError);
        }

        var now = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid();
        var defects = new List<Guid>();
        if (request.Verdict == QualityVerdicts.Blocked)
        {
            pipeline.Status = DeliveryPipelineStatuses.Paused;
            pipeline.LastError = Bounded(request.Summary);
            pipeline.ResumeAction = "Resolve the QA blocker, then resume to create a new QA assignment.";
        }
        else if (request.Verdict == QualityVerdicts.Failed)
        {
            foreach (var finding in request.Findings)
                defects.Add(await EnsureDefectAsync(item, finding, request.SourceCommitSha, now, cancellationToken));
            if (brief.QualityCycle >= brief.MaximumReworkCycles)
            {
                pipeline.Status = DeliveryPipelineStatuses.Paused;
                pipeline.LastError =
                    $"QA rework limit ({brief.MaximumReworkCycles}) reached for '{item.Title}'.";
                pipeline.ResumeAction = "Review the ticket and QA defects, then resume or reconfigure.";
            }
            else
            {
                var development = DeserializeDevelopmentBrief(item)
                    ?? throw new InvalidOperationException("The ticket has no developer brief for rework.");
                item.DevelopmentBriefJson = JsonSerializer.Serialize(development with
                {
                    ResumeBranch = brief.SourceBranch,
                    ResumeCommitSha = brief.SourceCommitSha,
                    ReworkFindings = request.Findings
                }, JsonOptions);
                await AssignAsync(
                    pipeline,
                    item,
                    pipeline.DeveloperInstallationId,
                    pipeline.DevelopmentColumnId,
                    [WorkItemActions.Read, WorkItemActions.Start, WorkItemActions.Comment, WorkItemActions.Complete],
                    $"pipeline:{pipeline.Id:D}:item:{item.Id:D}:rework:{brief.QualityCycle}",
                    cancellationToken);
                pipeline.Stage = "Development";
                pipeline.Status = DeliveryPipelineStatuses.Running;
            }
        }
        else
        {
            item.MergeStatus = DeliveryMergeStatuses.Queued;
            pipeline.Stage = "Merge";
            pipeline.MergeStatus = DeliveryMergeStatuses.Queued;
            pipeline.Status = DeliveryPipelineStatuses.Running;
        }

        pipeline.Revision++;
        pipeline.UpdatedAt = now;
        var result = new QualityRunResult(
            runId,
            item.Id,
            brief.QualityCycle,
            request.Verdict,
            pipeline.Status,
            item.MergeStatus,
            defects,
            now);
        db.WorkQualityRuns.Add(new WorkQualityRun
        {
            Id = runId,
            OrganizationId = organizationId,
            BoardId = request.BoardId,
            WorkItemId = item.Id,
            QualityInstallationId = qualityInstallationId,
            AssignmentRevision = request.AssignmentRevision,
            QualityCycle = brief.QualityCycle,
            SourceCommitSha = request.SourceCommitSha,
            Verdict = request.Verdict,
            ResultJson = JsonSerializer.Serialize(new StoredQualityRun(request, result), JsonOptions),
            IdempotencyKey = request.IdempotencyKey,
            CreatedAt = now
        });
        AddActivity(
            pipeline,
            item,
            "delivery.quality.recorded",
            "work.item.quality.submit",
            new
            {
                result.QualityRunId,
                result.QualityCycle,
                result.Verdict,
                defectItemIds = defects,
                request.SourceCommitSha
            },
            now);
        await db.SaveChangesAsync(cancellationToken);
        if (request.Verdict == QualityVerdicts.Failed &&
            pipeline.Status != DeliveryPipelineStatuses.Paused)
            await runtimeManager.EnsureRuntimeQueuedAsync(
                pipeline.DeveloperInstallationId,
                $"QA rework assigned for ticket {item.Id:D}.",
                cancellationToken: cancellationToken);
        return result;
    }

    private async Task<bool> AdvanceAsync(Guid pipelineId, CancellationToken cancellationToken)
    {
        var pipeline = await db.WorkDeliveryPipelines.SingleAsync(
            x => x.Id == pipelineId, cancellationToken);
        if (!pipeline.IsEnabled || pipeline.Status == DeliveryPipelineStatuses.Paused)
            return false;
        pipeline.ConsecutiveInfrastructureFailures = 0;

        if (pipeline.ActiveSprintId is null)
        {
            var sprint = await db.WorkSprints
                .Where(x =>
                    x.BoardId == pipeline.BoardId &&
                    x.Status == WorkSprintStatus.Planned &&
                    x.Sequence != null)
                .OrderBy(x => x.Sequence)
                .ThenBy(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (sprint is null)
            {
                if (pipeline.Status == DeliveryPipelineStatuses.Completed)
                    return false;
                pipeline.Status = DeliveryPipelineStatuses.Completed;
                pipeline.Stage = "Idle";
                pipeline.ActiveWorkItemId = null;
                pipeline.Revision++;
                pipeline.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            sprint.Status = WorkSprintStatus.Active;
            sprint.StartedAt ??= DateTimeOffset.UtcNow;
            sprint.Revision++;
            sprint.UpdatedAt = DateTimeOffset.UtcNow;
            pipeline.ActiveSprintId = sprint.Id;
            pipeline.Status = DeliveryPipelineStatuses.Running;
            pipeline.Stage = "Development";
            pipeline.Revision++;
            pipeline.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (pipeline.ActiveWorkItemId is not null)
        {
            if (pipeline.Stage == "Merge")
                return await TryGovernedMergeAsync(pipeline, cancellationToken);
            return false;
        }

        var sprintItems = await db.CoreWorkTasks
            .Where(x => x.BoardId == pipeline.BoardId && x.SprintId == pipeline.ActiveSprintId)
            .OrderBy(x => x.BoardRank)
            .ThenByDescending(x => x.Priority)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var deliveryItems = sprintItems.Where(x =>
            !x.IsQaTrackingDefect &&
            x.Kind is WorkItemKind.Story or WorkItemKind.Task or WorkItemKind.Bug).ToList();
        var remaining = deliveryItems.Where(x => x.Status != WorkTaskStatus.Completed).ToList();
        if (remaining.Count == 0)
        {
            if (sprintItems.Any(x => x.IsQaTrackingDefect && x.Status != WorkTaskStatus.Completed) ||
                deliveryItems.Any(x => x.MergedAt is null))
                throw new InvalidOperationException(
                    "The sprint has unresolved QA defects or code-bearing work without a merge.");
            var sprint = await db.WorkSprints.SingleAsync(
                x => x.Id == pipeline.ActiveSprintId, cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            sprint.Status = WorkSprintStatus.Completed;
            sprint.CompletedAt = completedAt;
            sprint.Revision++;
            sprint.UpdatedAt = completedAt;
            if (!await db.WorkSprintSnapshots.AnyAsync(
                    x => x.SprintId == sprint.Id, cancellationToken))
            {
                var committedPoints = sprintItems.Sum(x => x.EstimatePoints ?? 0);
                db.WorkSprintSnapshots.Add(new CSweet.Domain.WorkManagement.WorkSprintSnapshot
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = pipeline.OrganizationId,
                    BoardId = pipeline.BoardId,
                    SprintId = sprint.Id,
                    SprintName = sprint.Name,
                    Goal = sprint.Goal,
                    StartedAt = sprint.StartedAt,
                    CompletedAt = completedAt,
                    CapacityPoints = sprint.CapacityPoints,
                    CommittedItemCount = sprintItems.Count,
                    CompletedItemCount = sprintItems.Count,
                    CommittedPoints = committedPoints,
                    CompletedPoints = committedPoints,
                    ScopeJson = JsonSerializer.Serialize(
                        sprintItems.Select(x => new
                        {
                            x.Id,
                            x.Title,
                            kind = x.Kind.ToString(),
                            x.EstimatePoints,
                            x.MergeCommitSha
                        }),
                        JsonOptions),
                    CreatedAt = completedAt
                });
            }
            pipeline.ActiveSprintId = null;
            pipeline.Stage = "Idle";
            pipeline.Status = DeliveryPipelineStatuses.Idle;
            pipeline.Revision++;
            pipeline.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var dependencyRows = await db.WorkItemDependencies.AsNoTracking()
            .Where(x => remaining.Select(item => item.Id).Contains(x.WorkItemId))
            .ToListAsync(cancellationToken);
        var dependencyIds = dependencyRows.Select(x => x.DependsOnWorkItemId).Distinct().ToList();
        var dependencyStates = await db.CoreWorkTasks.AsNoTracking()
            .Where(x => dependencyIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var eligible = remaining.FirstOrDefault(item =>
        {
            var dependencies = dependencyRows.Where(x => x.WorkItemId == item.Id);
            return dependencies.All(x =>
                dependencyStates.TryGetValue(x.DependsOnWorkItemId, out var dependency) &&
                dependency.Status == WorkTaskStatus.Completed &&
                dependency.MergedAt is not null);
        });
        if (eligible is null)
            throw new InvalidOperationException("No ticket is eligible; dependencies are unresolved.");
        var specification = DeserializeSpecification(eligible)
            ?? throw new InvalidOperationException(
                $"Ticket '{eligible.Title}' has no complete delivery specification.");
        if (specification.RepositoryConnectionId != pipeline.RepositoryConnectionId ||
            specification.Requirements.Count == 0 ||
            specification.AcceptanceCriteria.Count == 0)
            throw new InvalidOperationException(
                $"Ticket '{eligible.Title}' has an invalid delivery specification.");
        eligible.DevelopmentBriefJson = JsonSerializer.Serialize(new SoftwareDevelopmentBrief(
            pipeline.RepositoryConnectionId,
            specification.BaseBranch ?? pipeline.BaseBranch,
            "confined-polyglot",
            specification.Requirements,
            specification.AcceptanceCriteria,
            specification.Constraints)
        {
            QualityGateColumnId = pipeline.QualityColumnId
        }, JsonOptions);
        await AssignAsync(
            pipeline,
            eligible,
            pipeline.DeveloperInstallationId,
            pipeline.DevelopmentColumnId,
            [WorkItemActions.Read, WorkItemActions.Start, WorkItemActions.Comment, WorkItemActions.Complete],
            $"pipeline:{pipeline.Id:D}:sprint:{pipeline.ActiveSprintId:D}:item:{eligible.Id:D}",
            cancellationToken);
        pipeline.ActiveWorkItemId = eligible.Id;
        pipeline.Stage = "Development";
        pipeline.Status = DeliveryPipelineStatuses.Running;
        pipeline.Revision++;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await runtimeManager.EnsureRuntimeQueuedAsync(
            pipeline.DeveloperInstallationId,
            $"Delivery ticket {eligible.Id:D} assigned.",
            cancellationToken: cancellationToken);
        return true;
    }

    private async Task<bool> TryGovernedMergeAsync(
        WorkDeliveryPipeline pipeline,
        CancellationToken cancellationToken)
    {
        var item = await db.CoreWorkTasks.SingleAsync(
            x => x.Id == pipeline.ActiveWorkItemId, cancellationToken);
        var brief = DeserializeQualityBrief(item)
            ?? throw new InvalidOperationException("The merge has no approved QA brief.");
        var passedRun = await db.WorkQualityRuns.AsNoTracking()
            .Where(x =>
                x.WorkItemId == item.Id &&
                x.SourceCommitSha == brief.SourceCommitSha &&
                x.Verdict == QualityVerdicts.Passed)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No QA pass authorizes this commit.");
        var connection = await db.GitRepositoryConnections.AsNoTracking().SingleAsync(
            x => x.Id == pipeline.RepositoryConnectionId, cancellationToken);
        var grant = await db.GitRepositoryConnectionGrants.AsNoTracking().SingleOrDefaultAsync(x =>
            x.RepositoryConnectionId == connection.Id &&
            x.AgentInstallationId == pipeline.DeveloperInstallationId &&
            x.RevokedAt == null, cancellationToken);
        if (!connection.AllowedOperations.HasFlag(GitAllowedOperation.MergeQaApprovedPullRequest) ||
            grant?.CanMergeQaApprovedPullRequest != true)
            throw new InvalidOperationException("The governed merge grant is unavailable.");
        if (connection.Provider != GitRepositoryProvider.GitHub)
            throw new InvalidOperationException("Governed merge currently requires the GitHub provider.");

        var token = await secrets.GetAsync(
            pipeline.DeveloperInstallationId,
            SoftwareDevelopmentWorkService.CredentialKey(connection.Id, "github-api-token"),
            cancellationToken)
            ?? throw new InvalidOperationException("The GitHub merge credential is unavailable.");
        var (owner, repository, number) = ParsePullRequest(brief.PullRequestUrl);
        if (!string.Equals(
                $"{owner}/{repository}",
                connection.PermittedRepositoryPath,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The approved pull request is outside the repository grant.");
        var client = httpClients.CreateClient();
        using var readRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repository}/pulls/{number}");
        AddGitHubHeaders(readRequest, token);
        using var readResponse = await client.SendAsync(readRequest, cancellationToken);
        if (!readResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub pull-request inspection failed with HTTP {(int)readResponse.StatusCode}.");
        using var pullRequest = JsonDocument.Parse(
            await readResponse.Content.ReadAsStringAsync(cancellationToken));
        var remoteHead = pullRequest.RootElement.GetProperty("head")
            .GetProperty("sha").GetString();
        var remoteBase = pullRequest.RootElement.GetProperty("base")
            .GetProperty("ref").GetString();
        if (!string.Equals(remoteBase, pipeline.BaseBranch, StringComparison.Ordinal))
            throw new InvalidOperationException("The pull request targets a different base branch.");
        if (!string.Equals(remoteHead, brief.SourceCommitSha, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(remoteHead))
                throw new InvalidOperationException("GitHub returned an invalid PR head.");
            var cycle = item.QualityCycle + 1;
            item.QualityCycle = cycle;
            item.QualityBriefJson = JsonSerializer.Serialize(brief with
            {
                SourceCommitSha = remoteHead,
                QualityCycle = cycle
            }, JsonOptions);
            item.MergeStatus = DeliveryMergeStatuses.None;
            await AssignAsync(
                pipeline,
                item,
                pipeline.QualityInstallationId,
                pipeline.QualityColumnId,
                [WorkItemActions.Read, WorkItemActions.QualitySubmit],
                $"pipeline:{pipeline.Id:D}:item:{item.Id:D}:head-changed:{cycle}",
                cancellationToken);
            pipeline.Stage = "QA";
            pipeline.QualityCycle = cycle;
            pipeline.MergeStatus = DeliveryMergeStatuses.None;
            pipeline.SourceCommitSha = remoteHead;
            pipeline.Revision++;
            pipeline.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await runtimeManager.EnsureRuntimeQueuedAsync(
                pipeline.QualityInstallationId,
                $"PR head changed; QA cycle {cycle} assigned for ticket {item.Id:D}.",
                cancellationToken: cancellationToken);
            return true;
        }
        string? mergeSha = null;
        if (pullRequest.RootElement.TryGetProperty("merged", out var alreadyMerged) &&
            alreadyMerged.GetBoolean())
            mergeSha = pullRequest.RootElement.GetProperty("merge_commit_sha").GetString();

        if (mergeSha is null)
        {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"https://api.github.com/repos/{owner}/{repository}/pulls/{number}/merge")
        {
            Content = JsonContent.Create(new
            {
                sha = brief.SourceCommitSha,
                merge_method = pipeline.MergeStrategy.ToLowerInvariant()
            })
        };
        AddGitHubHeaders(request, token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.MethodNotAllowed)
        {
            pipeline.MergeStatus = DeliveryMergeStatuses.Queued;
            pipeline.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GitHub rejected the governed merge with HTTP {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("merged", out var merged) || !merged.GetBoolean())
            return false;
        mergeSha = document.RootElement.TryGetProperty("sha", out var sha)
            ? sha.GetString()
            : null;
        }
        if (string.IsNullOrWhiteSpace(mergeSha))
            throw new InvalidOperationException("GitHub confirmed merge without a merge commit SHA.");

        var now = DateTimeOffset.UtcNow;
        item.MergeStatus = DeliveryMergeStatuses.Merged;
        item.MergeCommitSha = mergeSha;
        item.MergedAt = now;
        item.MergeQualityRunId = passedRun.Id;
        item.MergeAuthorizationGrantId = grant.Id;
        item.MergeAuthorizationGrantRevision = grant.Revision;
        item.Status = WorkTaskStatus.Completed;
        item.BoardColumnId = pipeline.DoneColumnId;
        item.Revision++;
        item.UpdatedAt = now;
        var workspace = await db.GitTicketWorkspaces
            .Where(x => x.WorkItemId == item.Id && x.CommitSha == brief.SourceCommitSha)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstAsync(cancellationToken);
        workspace.MergeStatus = DeliveryMergeStatuses.Merged;
        workspace.MergeCommitSha = mergeSha;
        workspace.MergedAt = now;
        foreach (var defect in await db.CoreWorkTasks.Where(x =>
                     x.ParentWorkTaskId == item.Id &&
                     x.IsQaTrackingDefect &&
                     x.Status != WorkTaskStatus.Completed).ToListAsync(cancellationToken))
        {
            defect.Status = WorkTaskStatus.Completed;
            defect.BoardColumnId = pipeline.DoneColumnId;
            defect.Revision++;
            defect.UpdatedAt = now;
        }
        AddActivity(
            pipeline,
            item,
            "delivery.merge.completed",
            "work.item.merge.qa-approved",
            new
            {
                qualityRunId = passedRun.Id,
                sourceCommitSha = brief.SourceCommitSha,
                mergeCommitSha = mergeSha,
                pullRequestUrl = brief.PullRequestUrl
            },
            now);
        pipeline.ActiveWorkItemId = null;
        pipeline.Stage = "Development";
        pipeline.MergeStatus = DeliveryMergeStatuses.Merged;
        pipeline.QualityCycle = 0;
        pipeline.Revision++;
        pipeline.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        _ = passedRun;
        return true;
    }

    private async Task AssignAsync(
        WorkDeliveryPipeline pipeline,
        WorkTask item,
        Guid installationId,
        Guid columnId,
        IReadOnlyList<string> actions,
        string eventKey,
        CancellationToken cancellationToken)
    {
        var employee = await db.CoreOrganizationUsers.SingleOrDefaultAsync(x =>
            x.OrganizationId == pipeline.OrganizationId &&
            x.AgentInstallationId == installationId &&
            x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException(
                "The configured agent is not linked to an active organization employee.");
        var now = DateTimeOffset.UtcNow;
        if (item.AssignedAgentInstallationId.HasValue)
        {
            var oldGrants = await db.ScopedActionGrants.Where(x =>
                x.OrganizationId == pipeline.OrganizationId &&
                x.SubjectKind == GrantSubjectKind.AgentInstallation &&
                x.SubjectId == item.AssignedAgentInstallationId &&
                x.ScopeKind == GrantScopeKind.WorkItem &&
                x.ScopeId == item.Id &&
                x.RevokedAt == null).ToListAsync(cancellationToken);
            foreach (var oldGrant in oldGrants)
            {
                oldGrant.RevokedAt = now;
                oldGrant.Revision++;
            }
        }
        item.AssignedAgentInstallationId = installationId;
        item.AssignedEmployeeId = employee.Id;
        item.AssignedWorkerId = employee.WorkerId;
        item.BoardColumnId = columnId;
        item.Status = WorkTaskStatus.Assigned;
        item.AssignmentRevision++;
        item.Revision++;
        item.UpdatedAt = now;
        foreach (var action in actions)
            db.ScopedActionGrants.Add(new ScopedActionGrant
            {
                Id = Guid.NewGuid(),
                OrganizationId = pipeline.OrganizationId,
                SubjectKind = GrantSubjectKind.AgentInstallation,
                SubjectId = installationId,
                Action = action,
                ScopeKind = GrantScopeKind.WorkItem,
                ScopeId = item.Id,
                CanDelegate = false,
                GrantedBySubjectKind = GrantSubjectKind.AutomationIdentity,
                GrantedBySubjectId = pipeline.Id,
                GrantedAt = now
            });
        var assigned = new WorkItemAssignedEvent(
            pipeline.BoardId, item.Id, item.AssignmentRevision, installationId);
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = pipeline.OrganizationId,
            TargetInstallationId = installationId,
            EventType = WorkItemEvents.Assigned,
            DataJson = JsonSerializer.Serialize(assigned, JsonOptions),
            IdempotencyKey = eventKey,
            Status = AgentPlatformEventOutboxStatus.Pending,
            NextAttemptAt = now,
            OccurredAt = now
        });
        AddActivity(
            pipeline,
            item,
            "delivery.assignment.created",
            "work.item.assign",
            new
            {
                installationId,
                item.AssignmentRevision,
                columnId,
                eventKey
            },
            now);
    }

    private void AddActivity(
        WorkDeliveryPipeline pipeline,
        WorkTask item,
        string eventType,
        string action,
        object data,
        DateTimeOffset occurredAt) =>
        db.WorkItemActivities.Add(new WorkItemActivity
        {
            Id = Guid.NewGuid(),
            OrganizationId = pipeline.OrganizationId,
            BoardId = pipeline.BoardId,
            WorkItemId = item.Id,
            EventType = eventType,
            Action = action,
            ActorKind = GrantSubjectKind.AutomationIdentity,
            ActorSubjectId = pipeline.Id,
            ActorDisplayName = "C-Sweet Delivery Coordinator",
            DataJson = JsonSerializer.Serialize(data, JsonOptions),
            OccurredAt = occurredAt
        });

    private async Task<Guid> EnsureDefectAsync(
        WorkTask parent,
        QualityFinding finding,
        string sourceCommit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalized = string.Join('|',
            sourceCommit,
            finding.Title.Trim().ToUpperInvariant(),
            finding.Description.Trim().ToUpperInvariant());
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var existing = await db.CoreWorkTasks.SingleOrDefaultAsync(x =>
            x.ParentWorkTaskId == parent.Id &&
            x.QualityFindingFingerprint == fingerprint, cancellationToken);
        if (existing is not null)
            return existing.Id;
        var defect = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = parent.OrganizationId,
            BoardId = parent.BoardId,
            BoardColumnId = parent.BoardColumnId,
            SprintId = parent.SprintId,
            ParentWorkTaskId = parent.Id,
            Title = $"QA: {Bounded(finding.Title, 480)}",
            Description = JsonSerializer.Serialize(new
            {
                sourceCommit,
                finding.Description,
                finding.ReproductionSteps,
                finding.ExpectedBehavior,
                finding.ActualBehavior,
                finding.Evidence
            }, JsonOptions),
            Kind = WorkItemKind.Bug,
            Status = WorkTaskStatus.Ready,
            Priority = ParsePriority(finding.Severity),
            BoardRank = parent.BoardRank + 1,
            IsQaTrackingDefect = true,
            QualityFindingFingerprint = fingerprint,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.CoreWorkTasks.Add(defect);
        return defect.Id;
    }

    private static void ValidateQualityResult(SubmitQualityResultRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("A bounded idempotency key is required.");
        if (request.Summary.Length > 8_192 || request.Criteria.Count > 200 ||
            request.Validations.Count > 200 || request.Findings.Count > 100)
            throw new ArgumentException("The quality outcome exceeds its bounded schema.");
        if (request.Verdict is not (
            QualityVerdicts.Passed or QualityVerdicts.Failed or QualityVerdicts.Blocked))
            throw new ArgumentException("The quality verdict is invalid.");
        if (request.Verdict == QualityVerdicts.Passed &&
            (request.Validations.Count == 0 ||
             request.Validations.Any(x => x.Status != QualityResultStatuses.Passed || x.ExitCode != 0) ||
             request.Criteria.Count == 0 ||
             request.Criteria.Any(x => x.Status != QualityResultStatuses.Passed) ||
             request.Findings.Count != 0))
            throw new ArgumentException("A passing result must satisfy every QA pass invariant.");
        if (request.Verdict == QualityVerdicts.Failed && request.Findings.Count == 0)
            throw new ArgumentException("A failed result requires at least one confirmed finding.");
        if (request.Verdict == QualityVerdicts.Blocked && request.Findings.Count != 0)
            throw new ArgumentException("A blocked result cannot create defects.");
    }

    private async Task ValidateConfigurationAsync(
        Guid organizationId,
        Guid boardId,
        ConfigureDeliveryPipelineRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DeveloperInstallationId == request.QualityInstallationId)
            throw new ArgumentException("Developer and QA installations must be distinct.");
        var businessId = organizationId.ToString("D");
        var installations = await db.AgentInstallations.AsNoTracking().Where(x =>
            (x.Id == request.DeveloperInstallationId || x.Id == request.QualityInstallationId) &&
            x.BusinessId == businessId &&
            x.IsEnabled &&
            x.RevisionStatus == PluginRevisionStatus.Active)
            .Join(
                db.AgentPackageVersions.AsNoTracking(),
                installation => installation.PackageVersionId,
                package => package.Id,
                (installation, package) => new
                {
                    installation.Id,
                    package.AgentId,
                    package.ManifestJson
                })
            .ToListAsync(cancellationToken);
        if (installations.Count != 2 ||
            installations.Single(x => x.Id == request.DeveloperInstallationId).AgentId !=
                "com.csweet.software-developer" ||
            installations.Single(x => x.Id == request.QualityInstallationId).AgentId !=
                "com.csweet.software-qa")
            throw new ArgumentException("Both configured agent installations must be active.");
        var qualityManifest = installations.Single(
            x => x.Id == request.QualityInstallationId).ManifestJson;
        if (!ManifestRequires(qualityManifest, WorkItemActions.QualitySubmit))
            throw new ArgumentException(
                "The configured QA package does not request scoped quality submission.");
        var columns = await db.WorkBoardColumns.AsNoTracking().Where(x =>
            x.BoardId == boardId &&
            (x.Id == request.DevelopmentColumnId ||
             x.Id == request.QualityColumnId ||
             x.Id == request.DoneColumnId)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (columns.Count != 3 ||
            columns[request.DevelopmentColumnId].Category == WorkBoardColumnCategory.Done ||
            columns[request.QualityColumnId].Category == WorkBoardColumnCategory.Done ||
            columns[request.DoneColumnId].Category != WorkBoardColumnCategory.Done)
            throw new ArgumentException("Development, QA, and Done columns are invalid.");
        var connection = await db.GitRepositoryConnections.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.RepositoryConnectionId && x.OrganizationId == organizationId,
            cancellationToken) ?? throw new ArgumentException("The repository connection was not found.");
        var grants = await db.GitRepositoryConnectionGrants.AsNoTracking().Where(x =>
            x.RepositoryConnectionId == connection.Id &&
            x.RevokedAt == null &&
            (x.AgentInstallationId == request.DeveloperInstallationId ||
             x.AgentInstallationId == request.QualityInstallationId)).ToListAsync(cancellationToken);
        if (!grants.Any(x =>
                x.AgentInstallationId == request.DeveloperInstallationId &&
                x.CanReadFetch &&
                x.CanPushTicketBranch) ||
            !grants.Any(x =>
                x.AgentInstallationId == request.QualityInstallationId &&
                x.CanReadFetch))
            throw new ArgumentException("The developer and QA repository grants are incomplete.");
        if (request.IsEnabled &&
            (!connection.AllowedOperations.HasFlag(
                 GitAllowedOperation.MergeQaApprovedPullRequest) ||
             !grants.Any(x =>
                 x.AgentInstallationId == request.DeveloperInstallationId &&
                 x.CanMergeQaApprovedPullRequest)))
            throw new ArgumentException(
                "Enabling delivery requires a separately approved governed merge grant.");
    }

    private async Task RequireBoardManagerAsync(
        Guid organizationId,
        Guid boardId,
        Guid applicationUserId,
        CancellationToken cancellationToken)
    {
        var member = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.IsActive &&
            x.EmployeeType == EmployeeType.Human, cancellationToken)
            ?? throw new UnauthorizedAccessException("The current user is not an active organization member.");
        var decision = await authorization.AuthorizeAsync(
            organizationId,
            GrantSubjectKind.OrganizationUser,
            member.Id,
            WorkBoardActions.Configure,
            GrantScopeKind.Board,
            boardId,
            cancellationToken);
        if (!decision.Allowed)
            throw new UnauthorizedAccessException("The current user cannot configure this board.");
    }

    private async Task<int> ReadMaximumQaCyclesAsync(
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var json = await db.AgentInstallationConfigurations.AsNoTracking()
            .Where(x => x.AgentInstallationId == installationId)
            .Select(x => x.SettingsJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return 3;
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("maxQaReworkCycles", out var value) ||
            !value.TryGetInt32(out var cycles))
            return 3;
        if (cycles is < 0 or > 20)
            throw new InvalidOperationException("maxQaReworkCycles must be between 0 and 20.");
        return cycles;
    }

    private async Task PauseAsync(
        Guid pipelineId,
        string error,
        string resumeAction,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var pipeline = await db.WorkDeliveryPipelines.SingleOrDefaultAsync(
            x => x.Id == pipelineId, cancellationToken);
        if (pipeline is null)
            return;
        pipeline.Status = DeliveryPipelineStatuses.Paused;
        pipeline.LastError = Bounded(error);
        pipeline.ResumeAction = resumeAction;
        pipeline.Revision++;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordInfrastructureFailureAsync(
        Guid pipelineId,
        string error,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var pipeline = await db.WorkDeliveryPipelines.SingleOrDefaultAsync(
            x => x.Id == pipelineId, cancellationToken);
        if (pipeline is null)
            return;
        pipeline.ConsecutiveInfrastructureFailures++;
        pipeline.LastFailureAt = DateTimeOffset.UtcNow;
        pipeline.LastError =
            $"Infrastructure attempt {pipeline.ConsecutiveInfrastructureFailures}/3 failed: {Bounded(error)}";
        if (pipeline.ConsecutiveInfrastructureFailures >= 3)
        {
            pipeline.Status = DeliveryPipelineStatuses.Paused;
            pipeline.ResumeAction =
                "Restore the required infrastructure, then resume the delivery pipeline.";
        }
        pipeline.Revision++;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static WorkItemDeliverySpecification? DeserializeSpecification(WorkTask item) =>
        Deserialize<WorkItemDeliverySpecification>(item.DeliverySpecificationJson);

    private static SoftwareDevelopmentBrief? DeserializeDevelopmentBrief(WorkTask item) =>
        Deserialize<SoftwareDevelopmentBrief>(item.DevelopmentBriefJson);

    private static SoftwareQualityBrief? DeserializeQualityBrief(WorkTask item) =>
        Deserialize<SoftwareQualityBrief>(item.QualityBriefJson);

    private static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static QualityRunResult DeserializeRunResult(WorkQualityRun run) =>
        JsonSerializer.Deserialize<StoredQualityRun>(run.ResultJson, JsonOptions)?.Result
        ?? throw new InvalidOperationException("The stored quality result is invalid.");

    private static (string Owner, string Repository, int Number) ParsePullRequest(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The approved pull request is not a GitHub HTTPS URL.");
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 4 || segments[2] != "pull" ||
            !int.TryParse(segments[3], out var number))
            throw new InvalidOperationException("The approved pull request URL is malformed.");
        return (segments[0], segments[1], number);
    }

    private static void AddGitHubHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("CSweet-Delivery-Coordinator/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
    }

    private static string NormalizeMergeStrategy(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "squash" => "Squash",
            "merge" => "Merge",
            "rebase" => "Rebase",
            _ => throw new ArgumentException("Merge strategy must be Squash, Merge, or Rebase.")
        };

    private static bool ManifestRequires(string manifestJson, string capability)
    {
        using var document = JsonDocument.Parse(manifestJson);
        return document.RootElement.TryGetProperty("requires", out var requires) &&
            requires.ValueKind == JsonValueKind.Array &&
            requires.EnumerateArray().Any(x =>
                x.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), capability, StringComparison.Ordinal));
    }

    private static string RequireGitReference(string value)
    {
        var result = value.Trim();
        if (result.Length is < 1 or > 256 ||
            result.StartsWith('-') ||
            result.Contains("..", StringComparison.Ordinal) ||
            result.Any(char.IsWhiteSpace))
            throw new ArgumentException("The base branch is invalid.");
        return result;
    }

    private static WorkTaskPriority ParsePriority(string severity) => severity switch
    {
        QualitySeverities.Critical => WorkTaskPriority.Critical,
        QualitySeverities.High => WorkTaskPriority.High,
        QualitySeverities.Medium => WorkTaskPriority.Medium,
        _ => WorkTaskPriority.Low
    };

    private static string Bounded(string value, int max = 4096) =>
        value.Length <= max ? value : value[..max];

    private static DeliveryPipelineResponse ToResponse(WorkDeliveryPipeline pipeline) => new(
        pipeline.Id,
        pipeline.OrganizationId,
        pipeline.BoardId,
        pipeline.DeveloperInstallationId,
        pipeline.QualityInstallationId,
        pipeline.DevelopmentColumnId,
        pipeline.QualityColumnId,
        pipeline.DoneColumnId,
        pipeline.RepositoryConnectionId,
        pipeline.BaseBranch,
        pipeline.MergeStrategy,
        pipeline.IsEnabled,
        pipeline.Status,
        pipeline.Stage,
        pipeline.ActiveSprintId,
        pipeline.ActiveWorkItemId,
        pipeline.QualityCycle,
        pipeline.MergeStatus,
        pipeline.SourcePullRequestUrl,
        pipeline.SourceCommitSha,
        pipeline.LastError,
        pipeline.ResumeAction,
        pipeline.Revision,
        pipeline.UpdatedAt);

    private sealed record StoredQualityRun(
        SubmitQualityResultRequest Request,
        QualityRunResult Result);
}
