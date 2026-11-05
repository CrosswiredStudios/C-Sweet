using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ExecutorUpdateRecoveryTests
{
    [Theory]
    [InlineData("specialist-execution", "The MCP service rejected the request: Internal Server Error", true)]
    [InlineData("specialist-execution", "The MCP service rejected the request: Forbidden", false)]
    [InlineData("technical-review", "The MCP service rejected the request: Internal Server Error", false)]
    public void Package_recovery_does_not_generalize_a_transport_failure_to_other_errors_or_review_stages(string stageKey, string summary, bool expected)
    {
        var stage = new WorkStageExecution { StageKey = stageKey };
        stage.Attempts.Add(new() { Attempt = 1, ResultJson = System.Text.Json.JsonSerializer.Serialize(
            new CSweet.WorkManagement.Contracts.WorkExecutionOutcomeV1(Guid.NewGuid(), Guid.NewGuid(),
                CSweet.WorkManagement.Contracts.WorkExecutionDispositions.Blocked, "blocked", summary,
                System.Text.Json.JsonSerializer.SerializeToElement(new { }), [], []), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) });
        Assert.Equal(expected, WorkOrchestrator.IsPackageRecoverable(stage));
    }
    [Theory]
    [InlineData("updated", true)] [InlineData("same-version", false)]
    [InlineData("downgrade", false)] [InlineData("malformed-telemetry", false)]
    [InlineData("wrong-tenant", false)] [InlineData("disabled", false)]
    [InlineData("configuration-pending", false)] [InlineData("exhausted", true)]
    [InlineData("retryable-transport", true)]
    [InlineData("idle-next-start", true)] [InlineData("old-runtime-next-start", false)]
    [InlineData("product-finding", false)] [InlineData("paused", false)]
    [InlineData("cancelled-ticket", false)] [InlineData("updated-before-failure", false)]
    public async Task Only_a_verified_new_executor_recovers_a_known_infrastructure_failure(string scenario, bool recovers)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        var org = Guid.NewGuid();
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), Version = scenario switch {
            "same-version" => "5.4.10", "downgrade" => "5.4.9", _ => "5.4.11" } };
        var installation = new AgentInstallation { Id = Guid.NewGuid(), PackageVersion = package,
            BusinessId = (scenario == "wrong-tenant" ? Guid.NewGuid() : org).ToString(),
            UpdatedAt = scenario == "updated-before-failure" ? now.AddHours(-2) : now,
            IsEnabled = scenario != "disabled", ConfigurationSyncStatus = scenario == "configuration-pending"
                ? AgentConfigurationSyncStatus.Restarting : scenario is "idle-next-start" or "old-runtime-next-start"
                    ? AgentConfigurationSyncStatus.PendingNextStart : AgentConfigurationSyncStatus.Current };
        if (scenario == "old-runtime-next-start")
        {
            var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = installation.Id };
            runtime.TransitionTo(AgentRuntimeStatus.Starting, now);
            runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now);
            runtime.TransitionTo(AgentRuntimeStatus.Running, now);
            db.AgentRuntimeInstances.Add(runtime);
        }
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = org,
            BoardId = Guid.NewGuid(), Status = scenario == "paused" ? WorkSprintExecutionStatus.Paused : WorkSprintExecutionStatus.Active };
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, AssignmentRevision = 7,
            Status = scenario == "cancelled-ticket" ? WorkTaskStatus.Cancelled : WorkTaskStatus.Failed,
            BlockReason = "failure", WaitingReason = "failure", ClaimEventId = Guid.NewGuid(), ClaimExpiresAt = now.AddHours(1) };
        var item = new WorkItemExecution { Id = Guid.NewGuid(), WorkItem = ticket, WorkItemId = ticket.Id,
            SprintExecution = execution, CurrentStageKey = "quality", Status = WorkItemExecutionStatus.Failed };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecution = item,
            StageKey = "quality", AgentInstallationId = installation.Id,
            PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation, Status = WorkStageExecutionStatus.Failed,
            LastError = scenario == "product-finding" ? "Required browser assertion failed." :
                scenario == "retryable-transport" ? "agent-failure:v1;code=runtime.transport;retryable=true" :
                "The QA compute operation is unavailable or its generation changed." };
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), AgentWorkItemId = Guid.NewGuid(),
            Attempt = 38, Status = WorkExecutionAttemptStatus.Failed, CompletedAt = now.AddMinutes(-5),
            ResultJson = "{\"checkpoint\":\"retained\"}" };
        stage.Attempts.Add(attempt); item.Stages.Add(stage); execution.Items.Add(item);
        var completedReview = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "technical-review",
            Status = WorkStageExecutionStatus.Completed, LastSummary = "Approved integrated Story commit" };
        item.Stages.Add(completedReview);
        db.AddRange(execution, installation, new AgentRunLog { Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id, AgentWorkItemId = attempt.AgentWorkItemId,
            AgentPackageVersion = scenario == "malformed-telemetry" ? "legacy" : "5.4.10", StartedAt = now.AddMinutes(-10) });
        await db.SaveChangesAsync();
        var policy = new WorkOrchestrationPolicyRevision();
        policy.Stages.Add(new() { Key = "quality", MaximumAttempts = scenario == "exhausted" ? 1 : 5 });
        var orchestrator = new WorkOrchestrator(db, null!, null!, null!, [], TimeProvider.System, null!);
        await orchestrator.RecoverUpdatedExecutorsAsync(execution, policy, now, default);
        await db.SaveChangesAsync();
        Assert.Equal(recovers ? WorkStageExecutionStatus.Pending : WorkStageExecutionStatus.Failed, stage.Status);
        Assert.Equal(recovers ? 1 : 0, await db.WorkOrchestrationEvents.CountAsync());
        Assert.Equal(7, ticket.AssignmentRevision);
        Assert.Equal("{\"checkpoint\":\"retained\"}", attempt.ResultJson);
        Assert.Equal(WorkStageExecutionStatus.Completed, completedReview.Status);
        Assert.Single(stage.Attempts);
        if (!recovers) return;
        Assert.Equal(WorkTaskStatus.Ready, ticket.Status); Assert.Null(ticket.BlockReason);
        Assert.Null(ticket.ClaimEventId); Assert.Null(ticket.ClaimExpiresAt);
        // Repeated discovery and a repeated failure on the same attempt cannot spend another retry.
        stage.Status = WorkStageExecutionStatus.Failed; ticket.Status = WorkTaskStatus.Failed;
        await orchestrator.RecoverUpdatedExecutorsAsync(execution, policy, now.AddMinutes(1), default);
        await db.SaveChangesAsync();
        Assert.Equal(WorkStageExecutionStatus.Failed, stage.Status);
        Assert.Single(await db.WorkOrchestrationEvents.ToListAsync());
    }
}
