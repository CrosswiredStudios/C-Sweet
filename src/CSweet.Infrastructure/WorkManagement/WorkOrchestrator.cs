using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Application.Setup;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator(
    CSweetDbContext db,
    AgentWorkInbox inbox,
    IAgentRuntimeManager runtimes,
    IEnumerable<ITrustedWorkActionExecutor> trustedActions,
    TimeProvider timeProvider,
    ILogger<WorkOrchestrator> logger) : IWorkOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PulseAsync(CancellationToken cancellationToken = default)
    {
        var executionIds = await db.WorkSprintExecutions.AsNoTracking()
            .Where(x => x.Status == WorkSprintExecutionStatus.Active)
            .OrderBy(x => x.StartedAt).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var executionId in executionIds)
        {
            try { await ReconcileExecutionAsync(executionId, cancellationToken); }
            catch (Exception exception)
            {
                logger.LogError(exception, "Board orchestration reconciliation failed for sprint execution {ExecutionId}.", executionId);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task ReconcileExecutionAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var execution = await db.WorkSprintExecutions
            .Include(x => x.Items).ThenInclude(x => x.WorkItem)!.ThenInclude(x => x!.Dependencies)
            .Include(x => x.Items).ThenInclude(x => x.Stages).ThenInclude(x => x.Attempts)
            .SingleAsync(x => x.Id == executionId, cancellationToken);
        var policy = await db.WorkOrchestrationPolicyRevisions.AsNoTracking()
            .Include(x => x.Stages).Include(x => x.Transitions)
            .SingleAsync(x => x.Id == execution.PolicyRevisionId, cancellationToken);
        var now = timeProvider.GetUtcNow();

        foreach (var stage in execution.Items.SelectMany(x => x.Stages)
                     .Where(x => x.Attempts.Any(a => a.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running)))
            await ReconcileAttemptAsync(execution, policy, stage, now, cancellationToken);

        foreach (var stage in execution.Items.SelectMany(x => x.Stages)
                     .Where(x => x.Status == WorkStageExecutionStatus.Backoff && x.RetryAt <= now))
        {
            stage.Status = WorkStageExecutionStatus.Pending;
            stage.UpdatedAt = now;
        }

        var advancing = execution.Items.Select(x => x.Stages.OrderByDescending(s => s.CreatedAt).First())
            .Where(x => x.Status == WorkStageExecutionStatus.Pending &&
                        x.StageType is WorkOrchestrationStageType.Queue or WorkOrchestrationStageType.Terminal)
            .ToList();
        foreach (var stage in advancing)
        {
            var definition = policy.Stages.Single(x => x.Key == stage.StageKey);
            if (stage.StageType == WorkOrchestrationStageType.Terminal)
                CompleteTerminal(execution, stage, definition, now);
            else
                Advance(execution, policy, stage, "ready", "Queue eligibility satisfied.", "{}", now);
        }

        foreach (var stage in execution.Items.Select(x => x.Stages.OrderByDescending(s => s.CreatedAt).First())
                     .Where(x => x.Status == WorkStageExecutionStatus.Pending &&
                                 x.StageType == WorkOrchestrationStageType.TrustedPlatformAction).ToList())
            await ExecuteTrustedActionAsync(execution, policy, stage, now, cancellationToken);

        if (execution.Items.All(x => x.Status is WorkItemExecutionStatus.Completed or WorkItemExecutionStatus.Cancelled))
        {
            execution.Status = WorkSprintExecutionStatus.Completed;
            execution.CompletedAt = now; execution.UpdatedAt = now; execution.Revision++;
            var sprint = await db.WorkSprints.SingleAsync(x => x.Id == execution.SprintId, cancellationToken);
            sprint.Status = WorkSprintStatus.Completed; sprint.CompletedAt = now; sprint.UpdatedAt = now; sprint.Revision++;
            AddEvent(execution, null, null, null, "sprint.execution.completed", new { execution.SprintId });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var candidates = execution.Items
            .Where(item => item.Status == WorkItemExecutionStatus.Pending && DependenciesComplete(item, execution.Items))
            .Select(item => item.Stages.OrderByDescending(x => x.CreatedAt).First())
            .Where(stage => stage.Status == WorkStageExecutionStatus.Pending &&
                            (stage.StageType == WorkOrchestrationStageType.AgentExecution ||
                             (stage.StageType == WorkOrchestrationStageType.MemberExecution &&
                              stage.PrincipalKind == WorkOrchestrationPrincipalKind.AgentInstallation)))
            .OrderByDescending(stage => stage.ItemExecution!.WorkItem!.Priority)
            .ThenBy(stage => stage.ItemExecution!.WorkItem!.BoardRank)
            .ThenBy(stage => stage.ItemExecution!.WorkItem!.CreatedAt)
            .ThenBy(stage => stage.ItemExecution!.ItemIdentifier, StringComparer.Ordinal)
            .ToList();
        foreach (var stage in candidates)
        {
            if (!await HasCapacityAsync(execution, policy, stage, cancellationToken)) continue;
            await DispatchAsync(execution, policy, stage, now, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileAttemptAsync(
        WorkSprintExecution execution,
        WorkOrchestrationPolicyRevision policy,
        WorkStageExecution stage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var attempt = stage.Attempts.Single(x => x.Status is WorkExecutionAttemptStatus.Pending or WorkExecutionAttemptStatus.Running);
        if (!attempt.AgentWorkItemId.HasValue) return;
        var state = await inbox.ReadStateAsync(attempt.AgentWorkItemId.Value, cancellationToken);
        if (state.Status == AgentWorkStatus.Leased)
        {
            attempt.Status = WorkExecutionAttemptStatus.Running;
            attempt.StartedAt ??= now; stage.Status = WorkStageExecutionStatus.Running;
            stage.ItemExecution!.Status = WorkItemExecutionStatus.Running;
            return;
        }
        if (state.Status == AgentWorkStatus.Pending) return;
        if (state.Status == AgentWorkStatus.Cancelled)
        {
            attempt.Status = WorkExecutionAttemptStatus.Cancelled; attempt.CompletedAt = now;
            return;
        }
        if (state.Status == AgentWorkStatus.DeadLetter)
        {
            await RevokeAttemptGrantsAsync(execution, stage, now, cancellationToken);
            attempt.Status = WorkExecutionAttemptStatus.Failed; attempt.ErrorCategory = "infrastructure";
            attempt.ErrorMessage = state.Error; attempt.CompletedAt = now;
            ScheduleRetryOrFail(execution, policy, stage, state.Error ?? "Agent work was dead-lettered.", now);
            return;
        }
        if (state.Completion?.Succeeded != true || state.Completion.Value is not { } value)
        {
            await RevokeAttemptGrantsAsync(execution, stage, now, cancellationToken);
            attempt.Status = WorkExecutionAttemptStatus.Failed; attempt.ErrorCategory = "infrastructure";
            attempt.ErrorMessage = state.Completion?.Error ?? "Agent returned no result."; attempt.CompletedAt = now;
            ScheduleRetryOrFail(execution, policy, stage, attempt.ErrorMessage, now); return;
        }
        Shared.WorkExecutionOutcomeV1? outcome;
        try { outcome = value.Deserialize<Shared.WorkExecutionOutcomeV1>(JsonOptions); }
        catch (JsonException exception)
        {
            attempt.Status = WorkExecutionAttemptStatus.Failed;
            attempt.ErrorCategory = "validation";
            attempt.ErrorMessage = $"Invalid execution result: {exception.Message}";
            attempt.CompletedAt = now;
            await RevokeAttemptGrantsAsync(execution, stage, now, cancellationToken);
            FailStage(stage, attempt.ErrorMessage, now); return;
        }
        var error = ValidateOutcome(policy, stage, attempt, outcome);
        if (error is null)
            error = await RecordQualityValidationAsync(
                execution, stage, outcome!, now, cancellationToken);
        if (error is not null)
        {
            attempt.Status = WorkExecutionAttemptStatus.Failed;
            attempt.ErrorCategory = "validation";
            attempt.ErrorMessage = error;
            attempt.CompletedAt = now;
            await RevokeAttemptGrantsAsync(execution, stage, now, cancellationToken);
            FailStage(stage, error, now); return;
        }
        attempt.Status = WorkExecutionAttemptStatus.Completed; attempt.CompletedAt = now;
        attempt.ResultJson = JsonSerializer.Serialize(outcome, JsonOptions);
        await RevokeAttemptGrantsAsync(execution, stage, now, cancellationToken);
        stage.LastOutcomeCode = outcome!.OutcomeCode; stage.LastSummary = outcome.Summary; stage.UpdatedAt = now;
        switch (outcome.Disposition)
        {
            case Shared.WorkExecutionDispositions.Blocked:
                stage.Status = WorkStageExecutionStatus.Blocked;
                stage.ItemExecution!.Status = WorkItemExecutionStatus.Blocked;
                stage.ItemExecution.BlockedReason = outcome.Summary;
                stage.ItemExecution.WorkItem!.Status = WorkTaskStatus.Blocked;
                break;
            case Shared.WorkExecutionDispositions.Failed:
                FailStage(stage, outcome.Summary, now); break;
            default:
                Advance(execution, policy, stage, outcome.OutcomeCode, outcome.Summary,
                    outcome.Output.GetRawText(), now); break;
        }
        AddEvent(execution, stage.ItemExecutionId, stage.Id, attempt.Id,
            "attempt.result.accepted", new { outcome.Disposition, outcome.OutcomeCode, outcome.Summary });
    }

    private async Task<string?> RecordQualityValidationAsync(
        WorkSprintExecution execution,
        WorkStageExecution stage,
        Shared.WorkExecutionOutcomeV1 outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(stage.StageKey, "quality", StringComparison.Ordinal) ||
            outcome.Disposition != Shared.WorkExecutionDispositions.Completed)
            return null;
        if (outcome.OutcomeCode is not ("passed" or "changes_requested"))
            return "The quality stage returned an unsupported source-control outcome.";
        if (!stage.AgentInstallationId.HasValue)
            return "The quality stage has no validator installation.";

        var commitEvidence = outcome.Evidence
            .Where(x => string.Equals(x.Kind, "commit", StringComparison.Ordinal))
            .Select(x => x.Value?.Trim().ToLowerInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (commitEvidence.Length != 1 ||
            commitEvidence[0]!.Length is not (40 or 64) ||
            commitEvidence[0]!.Any(x => !Uri.IsHexDigit(x)))
            return "QA must identify exactly one valid source commit SHA.";

        var item = stage.ItemExecution!.WorkItem!;
        var publication = await (
            from candidate in db.SourceControlPublications
            join workspace in db.SourceControlWorkspaces.AsNoTracking()
                on new { candidate.OrganizationId, Id = candidate.WorkspaceId }
                equals new { workspace.OrganizationId, workspace.Id }
            where candidate.OrganizationId == execution.OrganizationId &&
                  workspace.WorkItemId == item.Id &&
                  workspace.AssignmentRevision == item.AssignmentRevision &&
                  candidate.Status != SourceControlPublicationStatus.Superseded
            orderby candidate.CreatedAt descending
            select candidate)
            .FirstOrDefaultAsync(cancellationToken);
        if (publication is null)
            return "QA evidence has no current source publication for this assignment revision.";
        if (!string.Equals(publication.CommitSha, commitEvidence[0], StringComparison.OrdinalIgnoreCase))
            return "QA evidence does not match the exact current publication SHA.";

        var stale = await (
            from validation in db.SourceControlValidations
            join candidate in db.SourceControlPublications.AsNoTracking()
                on new { validation.OrganizationId, Id = validation.PublicationId }
                equals new { candidate.OrganizationId, candidate.Id }
            join workspace in db.SourceControlWorkspaces.AsNoTracking()
                on new { candidate.OrganizationId, Id = candidate.WorkspaceId }
                equals new { workspace.OrganizationId, workspace.Id }
            where validation.OrganizationId == execution.OrganizationId &&
                  workspace.WorkItemId == item.Id &&
                  workspace.AssignmentRevision == item.AssignmentRevision &&
                  validation.PublicationId != publication.Id &&
                  validation.Status != SourceControlValidationStatus.Superseded
            select validation)
            .ToListAsync(cancellationToken);
        foreach (var validation in stale)
        {
            validation.Status = SourceControlValidationStatus.Superseded;
            validation.SupersededAt = now;
            validation.UpdatedAt = now;
        }

        var record = await db.SourceControlValidations.SingleOrDefaultAsync(x =>
            x.OrganizationId == execution.OrganizationId &&
            x.PublicationId == publication.Id &&
            x.ValidatorAgentInstallationId == stage.AgentInstallationId.Value &&
            x.CommitSha == publication.CommitSha,
            cancellationToken);
        if (record is null)
        {
            record = new SourceControlValidation
            {
                Id = Guid.NewGuid(),
                OrganizationId = execution.OrganizationId,
                PublicationId = publication.Id,
                ValidatorAgentInstallationId = stage.AgentInstallationId.Value,
                CommitSha = publication.CommitSha,
                CreatedAt = now
            };
            db.SourceControlValidations.Add(record);
        }
        record.Status = outcome.OutcomeCode == "passed"
            ? SourceControlValidationStatus.Passed
            : SourceControlValidationStatus.Failed;
        record.ResultsJson = outcome.Output.GetRawText();
        record.FailureMessage = record.Status == SourceControlValidationStatus.Failed
            ? outcome.Summary
            : null;
        record.UpdatedAt = now;
        record.CompletedAt = now;
        record.SupersededAt = null;

        if (publication.Status != SourceControlPublicationStatus.BranchPublishedExternalMerge)
        {
            publication.Status = record.Status == SourceControlValidationStatus.Passed
                ? SourceControlPublicationStatus.AwaitingLeadAuthorization
                : SourceControlPublicationStatus.AwaitingValidation;
            publication.UpdatedAt = now;
            publication.Revision++;
        }
        return null;
    }

    private async Task DispatchAsync(
        WorkSprintExecution execution,
        WorkOrchestrationPolicyRevision policy,
        WorkStageExecution stage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var definition = policy.Stages.Single(x => x.Key == stage.StageKey);
        var installationId = stage.AgentInstallationId
            ?? throw new InvalidOperationException("Agent stage lacks an exact installation assignment.");
        var attemptNumber = stage.Attempts.Count + 1;
        var attempt = new WorkExecutionAttempt
        {
            Id = Guid.NewGuid(), StageExecutionId = stage.Id, Attempt = attemptNumber,
            IdempotencyKey = $"orchestration:{execution.Id:N}:{stage.ItemExecutionId:N}:{stage.StageKey}:{stage.Traversal}:{attemptNumber}",
            Status = WorkExecutionAttemptStatus.Pending, CreatedAt = now
        };
        var item = stage.ItemExecution!.WorkItem!;
        var assignment = new Shared.WorkExecutionAssignmentV1(
            execution.Id, stage.ItemExecutionId, stage.Id, attempt.Id,
            execution.OrganizationId, execution.BoardId, execution.SprintId, item.Id,
            item.AssignmentRevision,
            item.Identifier!.Split('-')[0], item.Identifier, execution.PolicyRevisionId,
            stage.StageKey, stage.Traversal, attemptNumber, now.AddSeconds(definition.TimeoutSeconds),
            definition.Instructions,
            JsonSerializer.SerializeToElement(new
            {
                item.Id, item.Identifier, item.Title, item.Description,
                kind = item.Kind.ToString(), priority = item.Priority.ToString(),
                item.DevelopmentBriefJson, item.DeliverySpecificationJson, item.QualityBriefJson
            }, JsonOptions),
            JsonSerializer.SerializeToElement(new { }, JsonOptions),
            stage.ItemExecution.Stages.SelectMany(x => x.Attempts)
                .Where(x => !string.IsNullOrWhiteSpace(x.ResultJson))
                .Select(x => JsonSerializer.Deserialize<Shared.WorkExecutionOutcomeV1>(x.ResultJson!, JsonOptions)!)
                .ToList(), []);
        var payload = JsonSerializer.SerializeToElement(assignment, JsonOptions);
        var work = await inbox.EnqueueAsync(
            execution.OrganizationId.ToString(), installationId, AgentWorkKind.Capability,
            Shared.WorkManagementCapabilityNames.ExecutionRunV1, payload, attempt.IdempotencyKey,
            assignment.Deadline, execution.Id.ToString("N"), stage.Id.ToString("N"),
            "WorkStageExecution", stage.Id.ToString("D"), maximumAttempts: 1,
            cancellationToken: cancellationToken);
        attempt.AgentWorkItemId = work.Id;
        stage.Attempts.Add(attempt); stage.Status = WorkStageExecutionStatus.Running;
        stage.UpdatedAt = now; stage.ItemExecution.Status = WorkItemExecutionStatus.Running;
        stage.ItemExecution.UpdatedAt = now; item.Status = WorkTaskStatus.Running; item.UpdatedAt = now; item.Revision++;
        EnsureAttemptGrants(
            execution, item.Id, installationId, attempt.Id, stage.StageKey, now);
        AddEvent(execution, stage.ItemExecutionId, stage.Id, attempt.Id,
            "attempt.dispatched", new { workId = work.Id, installationId, attempt = attemptNumber });
        await db.SaveChangesAsync(cancellationToken);
        await runtimes.EnsureRuntimeQueuedAsync(installationId,
            $"Board orchestration {item.Identifier} stage {stage.StageKey}", cancellationToken: cancellationToken);
    }

    private async Task ExecuteTrustedActionAsync(
        WorkSprintExecution execution, WorkOrchestrationPolicyRevision policy,
        WorkStageExecution stage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var action = trustedActions.SingleOrDefault(x => x.Action == stage.PlatformAction);
        if (action is null)
        {
            Block(stage, $"Trusted platform action '{stage.PlatformAction}' is not registered.", now); return;
        }
        stage.Status = WorkStageExecutionStatus.Running; stage.ItemExecution!.Status = WorkItemExecutionStatus.Running;
        var result = await action.ExecuteAsync(new(
            execution.OrganizationId, execution.BoardId, execution.Id, stage.ItemExecutionId,
            stage.Id, stage.ItemExecution.WorkItemId, stage.ItemExecution.ItemIdentifier,
            stage.PlatformAction!, JsonSerializer.SerializeToElement(new { }, JsonOptions)), cancellationToken);
        if (result.Disposition == Shared.WorkExecutionDispositions.Blocked)
            Block(stage, result.Summary, now);
        else if (result.Disposition == Shared.WorkExecutionDispositions.Failed)
            FailStage(stage, result.Summary, now);
        else
            Advance(execution, policy, stage, result.OutcomeCode, result.Summary, result.Output.GetRawText(), now);
    }

    private static void CompleteTerminal(
        WorkSprintExecution execution, WorkStageExecution stage,
        WorkOrchestrationStage definition, DateTimeOffset now)
    {
        stage.Status = WorkStageExecutionStatus.Completed; stage.CompletedAt = now; stage.UpdatedAt = now;
        stage.ItemExecution!.Status = definition.IsSuccessfulTerminal
            ? WorkItemExecutionStatus.Completed : WorkItemExecutionStatus.Cancelled;
        stage.ItemExecution.CompletedAt = now; stage.ItemExecution.UpdatedAt = now;
        stage.ItemExecution.WorkItem!.Status = definition.IsSuccessfulTerminal
            ? WorkTaskStatus.Completed : WorkTaskStatus.Cancelled;
        stage.ItemExecution.WorkItem.UpdatedAt = now; stage.ItemExecution.WorkItem.Revision++;
        if (definition.ColumnId.HasValue) stage.ItemExecution.WorkItem.BoardColumnId = definition.ColumnId;
        execution.UpdatedAt = now; execution.Revision++;
    }

    private static void Advance(
        WorkSprintExecution execution, WorkOrchestrationPolicyRevision policy,
        WorkStageExecution stage, string outcomeCode, string summary, string outputJson, DateTimeOffset now)
    {
        var transition = policy.Transitions.SingleOrDefault(x =>
            x.FromStageKey == stage.StageKey && x.OutcomeCode == outcomeCode)
            ?? throw new InvalidOperationException($"Outcome '{outcomeCode}' is not valid for stage '{stage.StageKey}'.");
        var item = stage.ItemExecution!;
        var nextTraversal = item.Traversal + (transition.MaximumTraversals.HasValue ? 1 : 0);
        if (transition.MaximumTraversals.HasValue && nextTraversal > transition.MaximumTraversals.Value)
        {
            FailStage(stage, "The workflow traversal limit was reached.", now); return;
        }
        stage.Status = WorkStageExecutionStatus.Completed; stage.CompletedAt = now;
        stage.LastOutcomeCode = outcomeCode; stage.LastSummary = summary; stage.UpdatedAt = now;
        item.Traversal = nextTraversal; item.UpdatedAt = now;
        var snapshot = JsonSerializer.Deserialize<List<AssignmentSnapshot>>(execution.AssignmentSnapshotJson, JsonOptions) ?? [];
        var assignments = snapshot.Where(x => x.WorkItemId == item.WorkItemId).Select(x => new WorkItemStageAssignment
        {
            StageKey = x.StageKey, PrincipalKind = x.PrincipalKind,
            OrganizationUserId = x.OrganizationUserId, AgentInstallationId = x.AgentInstallationId,
            PlatformAction = x.PlatformAction
        }).ToList();
        var next = policy.Stages.Single(x => x.Key == transition.ToStageKey);
        WorkOrchestrationService.CreateStageExecution(
            item, next, assignments, execution.StartedByOrganizationUserId, now);
        if (next.ColumnId.HasValue) item.WorkItem!.BoardColumnId = next.ColumnId;
        item.WorkItem!.Status = item.Status == WorkItemExecutionStatus.Blocked
            ? WorkTaskStatus.Blocked
            : next.Type switch
        {
            WorkOrchestrationStageType.ManagerApproval => WorkTaskStatus.WaitingForApproval,
            WorkOrchestrationStageType.Terminal when next.IsSuccessfulTerminal => WorkTaskStatus.Completed,
            _ => WorkTaskStatus.Assigned
        };
        item.WorkItem.UpdatedAt = now; item.WorkItem.Revision++;
        execution.UpdatedAt = now; execution.Revision++;
        _ = outputJson;
    }

    private static void ScheduleRetryOrFail(
        WorkSprintExecution execution, WorkOrchestrationPolicyRevision policy,
        WorkStageExecution stage, string error, DateTimeOffset now)
    {
        var definition = policy.Stages.Single(x => x.Key == stage.StageKey);
        if (stage.Attempts.Count >= definition.MaximumAttempts)
        {
            FailStage(stage, error, now); return;
        }
        var exponent = Math.Max(0, stage.Attempts.Count - 1);
        var seconds = Math.Min(definition.InitialRetryDelaySeconds * Math.Pow(2, exponent), definition.MaximumRetryDelaySeconds);
        var jitter = Math.Abs(stage.Id.GetHashCode()) % Math.Max(1, (int)Math.Ceiling(seconds * .2));
        stage.Status = WorkStageExecutionStatus.Backoff; stage.RetryAt = now.AddSeconds(seconds + jitter);
        stage.LastError = error; stage.UpdatedAt = now; stage.ItemExecution!.Status = WorkItemExecutionStatus.Pending;
        execution.UpdatedAt = now; execution.Revision++;
    }

    private static string? ValidateOutcome(
        WorkOrchestrationPolicyRevision policy, WorkStageExecution stage,
        WorkExecutionAttempt attempt, Shared.WorkExecutionOutcomeV1? outcome)
    {
        if (outcome is null) return "The execution result was empty.";
        if (outcome.StageExecutionId != stage.Id || outcome.AttemptId != attempt.Id)
            return "The execution result identifiers do not match the assigned attempt.";
        if (outcome.Disposition is not (Shared.WorkExecutionDispositions.Completed or
            Shared.WorkExecutionDispositions.Blocked or Shared.WorkExecutionDispositions.Failed))
            return $"Unknown disposition '{outcome.Disposition}'.";
        if (!TokenRegex().IsMatch(outcome.OutcomeCode)) return "The outcome code is invalid.";
        if (outcome.Disposition == Shared.WorkExecutionDispositions.Completed &&
            !policy.Transitions.Any(x => x.FromStageKey == stage.StageKey && x.OutcomeCode == outcome.OutcomeCode))
            return $"Unknown outcome '{outcome.OutcomeCode}' for stage '{stage.StageKey}'.";
        var schema = policy.Stages.Single(x => x.Key == stage.StageKey).OutputSchemaJson;
        return ValidateBasicSchema(schema, outcome.Output);
    }

    private static string? ValidateBasicSchema(string schemaJson, JsonElement value)
    {
        using var schema = JsonDocument.Parse(schemaJson);
        return ValidateSchemaValue(schema.RootElement, value, "output");
    }

    private static string? ValidateSchemaValue(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var valid = type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true
            };
            if (!valid) return $"{path} does not match schema type '{type.GetString()}'.";
        }
        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array &&
            !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
            return $"{path} is not one of the allowed values.";
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                foreach (var property in required.EnumerateArray())
                    if (property.GetString() is { } name && !value.TryGetProperty(name, out _))
                        return $"{path} is missing required property '{name}'.";
            var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object
                ? declared : default;
            foreach (var property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object &&
                    properties.TryGetProperty(property.Name, out var childSchema))
                {
                    var error = ValidateSchemaValue(childSchema, property.Value, $"{path}.{property.Name}");
                    if (error is not null) return error;
                }
                else if (schema.TryGetProperty("additionalProperties", out var additional) &&
                         additional.ValueKind == JsonValueKind.False)
                    return $"{path} contains undeclared property '{property.Name}'.";
            }
        }
        if (value.ValueKind == JsonValueKind.Array &&
            schema.TryGetProperty("items", out var itemSchema) && itemSchema.ValueKind == JsonValueKind.Object)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var error = ValidateSchemaValue(itemSchema, item, $"{path}[{index++}]");
                if (error is not null) return error;
            }
        }
        return null;
    }

    private async Task<bool> HasCapacityAsync(
        WorkSprintExecution execution, WorkOrchestrationPolicyRevision policy,
        WorkStageExecution stage, CancellationToken cancellationToken)
    {
        var active = new[] { WorkStageExecutionStatus.Dispatching, WorkStageExecutionStatus.Running };
        if (await db.WorkStageExecutions.CountAsync(x => active.Contains(x.Status), cancellationToken) >= policy.GlobalConcurrencyLimit) return false;
        if (await db.WorkStageExecutions.CountAsync(x => active.Contains(x.Status) &&
                x.ItemExecution!.SprintExecution!.OrganizationId == execution.OrganizationId, cancellationToken) >= policy.OrganizationConcurrencyLimit) return false;
        if (await db.WorkStageExecutions.CountAsync(x => active.Contains(x.Status) &&
                x.ItemExecution!.SprintExecution!.BoardId == execution.BoardId, cancellationToken) >= policy.BoardConcurrencyLimit) return false;
        var definition = policy.Stages.Single(x => x.Key == stage.StageKey);
        var stageLimit = definition.ConcurrencyLimit ?? policy.DefaultStageConcurrencyLimit;
        if (await db.WorkStageExecutions.CountAsync(x => active.Contains(x.Status) && x.StageKey == stage.StageKey &&
                x.ItemExecution!.SprintExecution!.BoardId == execution.BoardId, cancellationToken) >= stageLimit) return false;
        if (definition.ColumnId.HasValue)
        {
            var column = await db.WorkBoardColumns.AsNoTracking().SingleAsync(
                x => x.Id == definition.ColumnId.Value, cancellationToken);
            if (column.WipPolicy == WorkBoardWipPolicy.HardLimit && column.WipLimit.HasValue)
            {
                var inColumn = await db.WorkItemExecutions.CountAsync(x =>
                    x.SprintExecution!.BoardId == execution.BoardId &&
                    x.WorkItem!.BoardColumnId == column.Id &&
                    x.Id != stage.ItemExecutionId &&
                    x.Status != WorkItemExecutionStatus.Completed &&
                    x.Status != WorkItemExecutionStatus.Cancelled,
                    cancellationToken);
                if (inColumn >= column.WipLimit.Value) return false;
            }
        }
        if (!stage.AgentInstallationId.HasValue) return true;
        var installationLimit = await InstallationConcurrencyLimitAsync(
            stage.AgentInstallationId.Value, cancellationToken);
        var assigneeLimit = Math.Min(policy.DefaultAssigneeConcurrencyLimit, installationLimit);
        return await db.WorkStageExecutions.CountAsync(x =>
            active.Contains(x.Status) && x.AgentInstallationId == stage.AgentInstallationId,
            cancellationToken) < assigneeLimit;
    }

    private async Task<int> InstallationConcurrencyLimitAsync(
        Guid installationId, CancellationToken cancellationToken)
    {
        var manifest = await db.AgentInstallations.AsNoTracking()
            .Where(x => x.Id == installationId)
            .Select(x => x.PackageVersion!.ManifestJson)
            .SingleAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(manifest);
            if (document.RootElement.TryGetProperty("runtime", out var runtime) &&
                runtime.TryGetProperty("maximumConcurrentJobs", out var maximum) &&
                maximum.TryGetInt32(out var value) && value > 0)
                return value;
        }
        catch (JsonException)
        {
            // Preflight will block installations with invalid manifests. Keep this
            // scheduler pass conservative if persisted data is damaged.
        }
        return 1;
    }

    private static bool DependenciesComplete(WorkItemExecution item, IEnumerable<WorkItemExecution> sprintItems)
    {
        var executions = sprintItems.ToDictionary(x => x.WorkItemId);
        return item.WorkItem!.Dependencies.All(dependency =>
            executions.TryGetValue(dependency.DependsOnWorkItemId, out var execution)
                ? execution.Status == WorkItemExecutionStatus.Completed
                // Sprint preflight requires every dependency outside this execution to
                // already be complete. Treat it as satisfied from the immutable start
                // snapshot instead of relying on an optional navigation property.
                : true);
    }

    private void EnsureAttemptGrants(
        WorkSprintExecution execution,
        Guid workItemId,
        Guid installationId,
        Guid attemptId,
        string stageKey,
        DateTimeOffset now)
    {
        var actions = new HashSet<string>(StringComparer.Ordinal)
        {
            WorkItemActions.Read,
            WorkItemActions.Comment
        };
        if (string.Equals(stageKey, "development", StringComparison.Ordinal))
        {
            actions.UnionWith([
                CSweet.Agent.SDK.GitWorkspaceCapabilities.Prepare,
                CSweet.Agent.SDK.GitWorkspaceCapabilities.Refresh,
                CSweet.Agent.SDK.GitWorkspaceCapabilities.Inspect,
                CSweet.Agent.SDK.GitWorkspaceCapabilities.Publish,
                CSweet.Agent.SDK.GitWorkspaceCapabilities.Cleanup
            ]);
        }
        else if (string.Equals(stageKey, "quality", StringComparison.Ordinal))
        {
            actions.UnionWith([
                CSweet.Agent.SDK.GitWorkspaceCapabilities.Prepare,
                CSweet.Agent.SDK.GitWorkspaceCapabilities.Inspect,
                CSweet.Agent.SDK.GitWorkspaceCapabilities.Cleanup
            ]);
        }
        else if (string.Equals(stageKey, "merge-decision", StringComparison.Ordinal))
        {
            actions.UnionWith([
                CSweet.Agent.SDK.GitMergeCapabilities.Review,
                CSweet.Agent.SDK.GitMergeCapabilities.Authorize
            ]);
        }

        foreach (var action in actions)
            db.ScopedActionGrants.Add(new ScopedActionGrant
            {
                Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId,
                SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = installationId,
                Action = action, ScopeKind = GrantScopeKind.WorkItem, ScopeId = workItemId,
                CanDelegate = false, GrantedBySubjectKind = GrantSubjectKind.AutomationIdentity,
                GrantedBySubjectId = execution.Id, GrantedAt = now, ExpiresAt = now.AddDays(1)
            });
        _ = attemptId;
    }

    private async Task RevokeAttemptGrantsAsync(
        WorkSprintExecution execution, WorkStageExecution stage, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!stage.AgentInstallationId.HasValue) return;
        var grants = await db.ScopedActionGrants.Where(x =>
            x.OrganizationId == execution.OrganizationId &&
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == stage.AgentInstallationId.Value &&
            x.ScopeKind == GrantScopeKind.WorkItem &&
            x.ScopeId == stage.ItemExecution!.WorkItemId &&
            x.GrantedBySubjectKind == GrantSubjectKind.AutomationIdentity &&
            x.GrantedBySubjectId == execution.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            grant.RevokedAt = now;
            grant.Revision++;
        }
    }

    private static void Block(WorkStageExecution stage, string reason, DateTimeOffset now)
    {
        stage.Status = WorkStageExecutionStatus.Blocked; stage.LastError = reason; stage.UpdatedAt = now;
        stage.ItemExecution!.Status = WorkItemExecutionStatus.Blocked;
        stage.ItemExecution.BlockedReason = reason; stage.ItemExecution.UpdatedAt = now;
        stage.ItemExecution.WorkItem!.Status = WorkTaskStatus.Blocked;
    }

    private static void FailStage(WorkStageExecution stage, string error, DateTimeOffset now)
    {
        stage.Status = WorkStageExecutionStatus.Failed; stage.LastError = error; stage.UpdatedAt = now;
        stage.ItemExecution!.Status = WorkItemExecutionStatus.Failed;
        stage.ItemExecution.BlockedReason = error; stage.ItemExecution.UpdatedAt = now;
        stage.ItemExecution.WorkItem!.Status = WorkTaskStatus.Failed;
        stage.ItemExecution.WorkItem.UpdatedAt = now; stage.ItemExecution.WorkItem.Revision++;
    }

    private void AddEvent(
        WorkSprintExecution execution, Guid? itemId, Guid? stageId, Guid? attemptId,
        string eventType, object data) => db.WorkOrchestrationEvents.Add(new WorkOrchestrationEvent
        {
            Id = Guid.NewGuid(), OrganizationId = execution.OrganizationId, BoardId = execution.BoardId,
            SprintExecutionId = execution.Id, ItemExecutionId = itemId, StageExecutionId = stageId,
            AttemptId = attemptId, EventType = eventType,
            DataJson = JsonSerializer.Serialize(data, JsonOptions), OccurredAt = timeProvider.GetUtcNow()
        });

    private sealed record AssignmentSnapshot(
        Guid WorkItemId, string StageKey, WorkOrchestrationPrincipalKind PrincipalKind,
        Guid? OrganizationUserId, Guid? AgentInstallationId, string? PlatformAction);

    [GeneratedRegex("^[a-z][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
