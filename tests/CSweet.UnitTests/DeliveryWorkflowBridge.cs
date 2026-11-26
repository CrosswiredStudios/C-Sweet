using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Api.Compute;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public static class DeliveryWorkflowBridge
{
    internal static Task<ComputeDashboard?> ReadComputeAsync(CSweetDbContext db, Guid business, Guid user, bool legacy)
        => ComputeDashboardEndpoints.ReadAsync(db, business, user, Path.GetTempPath(), Path.GetTempPath(), default, legacyLocalSetupEnabled: legacy);

    internal static Task<OverviewIssueDirectory> ReadOverviewAsync(CSweetDbContext db, Guid business, Guid actor, bool legacy)
    {
        var clock = TimeProvider.System;
        var todos = new PersonalTodoService(db, clock);
        var authorization = new ScopedActionAuthorizationService(db);
        var issues = new WorkIssueService(db, authorization, clock);
        var blocked = new BlockedWorkAttentionReader(db, todos, clock, new(db, todos, authorization, issues));
        var reader = new OverviewIssueReader(db, todos, blocked, new ExecutiveDecisionService(db, new ChatTurnService(db)),
            new(db, new(db, clock), clock), issues, new(legacy), clock);
        return reader.ReadAsync(business, new(actor, null), false, default);
    }

    internal static async Task AcceptQaWaitAsync(CSweetDbContext db, WorkExecutionAssignment assignment, WorkExecutionOutcomeV1 outcome)
    {
        var stage = await db.WorkStageExecutions.Include(s => s.Attempts).Include(s => s.ItemExecution!)
            .ThenInclude(i => i.SprintExecution).SingleOrDefaultAsync(s => s.Id == assignment.StageExecutionId);
        if (stage is null)
        {
            var sprint = new WorkSprintExecution { Id = assignment.TaskSprintExecutionId, OrganizationId = assignment.OrganizationId,
                BoardId = assignment.BoardId, SprintId = assignment.TaskSprintId, PolicyRevisionId = assignment.TaskPolicyRevisionId };
            var item = new WorkItemExecution { Id = assignment.ExecutionId, SprintExecution = sprint, WorkItemId = assignment.TaskItemId,
                CurrentStageKey = assignment.StageKey };
            stage = new WorkStageExecution { Id = assignment.StageExecutionId, ItemExecution = item, StageKey = assignment.StageKey,
                AgentInstallationId = assignment.AgentInstallationId };
            db.Add(stage);
        }
        stage.Status = WorkStageExecutionStatus.Running;
        var attempt = new WorkExecutionAttempt { Id = assignment.AttemptId, StageExecutionId = stage.Id,
            Attempt = assignment.Attempt, Status = WorkExecutionAttemptStatus.Running };
        stage.Attempts.Add(attempt);
        db.Add(attempt);
        var orchestrator = new WorkOrchestrator(db, null!, null!, null!, [], TimeProvider.System, null!);
        Assert.Null(orchestrator.AcceptComputePreparationWait(stage.ItemExecution!.SprintExecution!, stage, attempt, outcome, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        Assert.Equal(0, WorkOrchestrator.RetryAttemptCount(stage));
    }

    public static void ValidateReview(ReviewGitMergeRequest request)
    {
        var tool = new McpToolCatalog([]).List(new HashSet<string> { GitMergeCapabilities.Review }).Single();
        var result = McpGatewayEndpoints.ValidateToolInput("workflow-review", JsonSerializer.SerializeToElement(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), tool.InputSchema);
        if (result is not null) throw new InvalidOperationException(result.Error);
    }
}
