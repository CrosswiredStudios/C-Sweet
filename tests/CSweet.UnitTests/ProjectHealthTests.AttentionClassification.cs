using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Communications;
using CSweet.Domain.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Communications;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthTests
{
    [Fact]
    public async Task UnrelatedActiveWorkDoesNotHideARealOwnerDecision()
    {
        await using var f = await Fixture.Create(); var (incident, ticket) = await PrepareOwnerReport(f);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Producer.AgentInstallationId!.Value, Status = AgentWorkStatus.Leased, AttemptCount = 1,
            CreatedAt = f.Clock.Now, DeadlineAt = f.Clock.Now.AddMinutes(10) };
        f.Db.AddRange(work, new WorkExecutionContext { Id = Guid.NewGuid(), OrganizationId = f.Org,
            AgentInstallationId = work.AgentInstallationId, AgentWorkItemId = work.Id, RootWorkItemId = ticket.Id },
            new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1,
                ClaimedAt = f.Clock.Now, LeaseExpiresAt = f.Clock.Now.AddMinutes(5) });
        await f.Db.SaveChangesAsync();
        var attention = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        Assert.Equal(incident.Id, attention.Id); Assert.Equal("Pending", attention.ResolutionDecision!.Status);
    }

    [Theory]
    [InlineData(WorkStageExecutionStatus.Pending)]
    [InlineData(WorkStageExecutionStatus.Dispatching)]
    [InlineData(WorkStageExecutionStatus.Running)]
    [InlineData(WorkStageExecutionStatus.Backoff)]
    [InlineData(WorkStageExecutionStatus.WaitingForApproval)]
    [InlineData(WorkStageExecutionStatus.WaitingForHuman)]
    public async Task CurrentRecoveryOrReviewDoesNotAskOwnerToActEvenWithAFreshReport(WorkStageExecutionStatus status)
    {
        await using var f = await Fixture.Create(); var (incident, ticket) = await PrepareOwnerReport(f);
        ticket.Status = WorkTaskStatus.Ready; ticket.BlockReason = null;
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id, CurrentStageKey = "quality" };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id,
            StageKey = "quality", Status = status, UpdatedAt = f.Clock.Now };
        f.Db.AddRange(execution, stage); incident.Fingerprint = $"stage:{stage.Id:N}";
        await f.Db.SaveChangesAsync();
        var report = ProjectHealthService.OwnerReport(incident)! with { SourceHash = (await f.Service.CurrentIncidentStateAsync(incident, default)).Hash };
        incident.OwnerReportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        var decision = Assert.Single(f.Db.ExecutiveDecisions);
        var answer = await new ExecutiveDecisionService(f.Db, new ChatTurnService(f.Db)).AnswerAsync(f.Org,
            decision.ConversationId, decision.Id, f.Ceo.Id, new("reconcile", null, "obsolete-retry"));
        Assert.False(answer.Succeeded); Assert.Empty(f.Db.ChatTurns);
        await f.Review();
        Assert.Equal("Open", incident.Status); // Recovery tracking is separate from owner input.
        Assert.Equal(ExecutiveDecisionStatus.Cancelled, decision.Status);
        Assert.All(f.Db.UserNotifications, x => Assert.NotNull(x.DismissedAt));
        Assert.Single(f.Db.CoreConversationMessages);
    }

    [Theory]
    [InlineData(AgentRuntimeStatus.Queued)]
    [InlineData(AgentRuntimeStatus.Starting)]
    [InlineData(AgentRuntimeStatus.WaitingForMcpSession)]
    public async Task QueuedExecutionIsABoundedWaitRatherThanAnIdleIncident(AgentRuntimeStatus runtimeStatus)
    {
        await using var f = await Fixture.Create();
        f.Clock.Now += TimeSpan.FromHours(1);
        var ticket = f.Ticket(WorkTaskStatus.Ready); f.Db.Add(ticket);
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id, CurrentStageKey = "quality" };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id, StageKey = "quality",
            Status = WorkStageExecutionStatus.Dispatching };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Producer.AgentInstallationId!.Value, Name = "work.execution.run", SourceType = "WorkStageExecution",
            SourceId = stage.Id.ToString(), Status = AgentWorkStatus.Pending, CreatedAt = f.Clock.Now, DeadlineAt = f.Clock.Now.AddMinutes(10) };
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = work.AgentInstallationId, QueuedAt = f.Clock.Now };
        if (runtimeStatus != AgentRuntimeStatus.Queued) runtime.TransitionTo(AgentRuntimeStatus.Starting, f.Clock.Now);
        if (runtimeStatus == AgentRuntimeStatus.WaitingForMcpSession) runtime.TransitionTo(runtimeStatus, f.Clock.Now);
        f.Db.AddRange(execution, stage, work, runtime, new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = stage.Id,
            AgentWorkItemId = work.Id, Status = WorkExecutionAttemptStatus.Pending, Attempt = 1 });
        await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Empty(f.Db.ProjectIncidents);
        var state = Assert.Single(f.Db.ProjectHealthStates);
        Assert.Contains("queued", state.WaitingReason); Assert.False(state.HasActiveWork);
        var progress = state.LastProgressAt;
        f.Clock.Now += TimeSpan.FromMinutes(11); await f.Review();
        Assert.Equal(progress, state.LastProgressAt);
        Assert.Equal($"stage:{stage.Id:N}", Assert.Single(f.Db.ProjectIncidents).Fingerprint);
    }

    [Theory]
    [InlineData(IncidentDispositions.Investigating)]
    [InlineData(IncidentDispositions.AwaitingRecovery)]
    public async Task BoundedManagerRecoveryHidesOwnerPromptWithoutConcealingAnExpiredFailure(string disposition)
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        incident.Disposition = disposition; incident.ReviewAt = f.Clock.Now.AddMinutes(5);
        incident.ActionReference = "existing-recovery"; await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        f.Clock.Now += TimeSpan.FromMinutes(6);
        Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
    }

    [Fact]
    public async Task StaleReportKeepsHistoryButCannotPublishItsOldRequestAgain()
    {
        await using var f = await Fixture.Create(); var (incident, ticket) = await PrepareOwnerReport(f);
        var savedReport = incident.OwnerReportJson; var savedMessage = Assert.Single(f.Db.CoreConversationMessages).Content;
        ticket.Revision++; ticket.BlockReason = "A different current blocker";
        f.Db.ProjectIncidentDeliveries.Add(new() { Id = Guid.NewGuid(), IncidentId = incident.Id, RecipientId = f.Ceo.Id,
            SenderId = f.Chief.Id, Revision = ++incident.Revision, NextAttemptAt = f.Clock.Now });
        await f.Db.SaveChangesAsync();
        var presentation = await f.Service.PresentAsync(incident, default);
        Assert.DoesNotContain("scope reconciliation is blocked", presentation.Title);
        Assert.DoesNotContain(ProjectHealthService.OwnerReport(incident)!.NextStep, presentation.NextStep);
        Assert.Contains("No response to the previous report", presentation.NextStep);
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        await f.Service.DeliverAsync(default);
        Assert.Equal(savedReport, incident.OwnerReportJson);
        Assert.Equal(savedMessage, Assert.Single(f.Db.CoreConversationMessages).Content);
        Assert.Equal(ExecutiveDecisionStatus.Cancelled, Assert.Single(f.Db.ExecutiveDecisions).Status);
        Assert.All(f.Db.UserNotifications, x => Assert.NotNull(x.DismissedAt));
    }
}
