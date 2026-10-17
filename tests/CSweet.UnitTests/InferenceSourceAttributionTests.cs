using CSweet.Contracts.Analytics;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Analytics;
using CSweet.UI.Services;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class InferenceSourceAttributionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T23:00:00Z");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordination_UsesExactDelivery_AfterSessionAdvances_AndPreservesTaskOwnership(bool taskSource)
    {
        await using var db = WorkEfficiencyTests.Database();
        var org = Guid.NewGuid(); var installation = Guid.NewGuid(); var project = Guid.NewGuid();
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project };
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, Board = board, BoardId = board.Id,
            CreatedAt = Now.AddDays(-1), UpdatedAt = Now.AddHours(-1) };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = org.ToString(),
            AgentInstallationId = installation, SourceType = "agent-coordination", Status = AgentWorkStatus.Completed };
        var session = new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project,
            TargetInstallationId = installation, SourceWorkItemId = taskSource ? item.Id : null,
            CurrentAgentWorkItemId = Guid.NewGuid(), CreatedAt = Now.AddHours(-1) };
        session.Turns.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, SessionId = session.Id });
        db.AddRange(board, item, work, session, new Workstream { Id = project, OrganizationId = org });
        await db.SaveChangesAsync();
        var log = WorkEfficiencyTests.Call(org); log.StartedAt = Now; log.ProviderStartedAt = Now;
        log.AgentInstallationId = installation; log.AgentWorkItemId = work.Id;
        await InferenceAttribution.CaptureAsync(db, log, work.Id);
        Assert.Equal(project, log.WorkstreamId);
        Assert.Equal(taskSource ? item.Id : (Guid?)null, log.WorkItemId);
        Assert.Equal("Coordination", log.AttributionKind);
        db.Add(log); await db.SaveChangesAsync();
        var report = await new WorkEfficiencyService(db, new WorkEfficiencyTests.Clock(Now)).GetAsync(org);
        Assert.Equal(15, report.Projects.Single().Total.TotalTokens);
        Assert.Equal(taskSource ? 15 : 0, report.WorkItems.Single().Total.TotalTokens);
        item.ParentWorkTaskId = Guid.NewGuid(); await db.SaveChangesAsync();
        await InferenceSourceAttribution.CaptureAsync(db, log, work.Id, historical: true);
        Assert.Equal("[]", log.AncestorWorkItemIdsJson);
    }

    [Fact]
    public async Task Backfill_IsIdempotent_SkipsRunningAndForeignSources_AndKeepsTokens()
    {
        await using var db = WorkEfficiencyTests.Database();
        var org = Guid.NewGuid(); var installation = Guid.NewGuid(); var project = Guid.NewGuid();
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = org.ToString(), AgentInstallationId = installation,
            SourceType = "agent-coordination" };
        var session = new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project,
            InitiatorInstallationId = installation, CurrentAgentWorkItemId = work.Id, CreatedAt = Now.AddHours(-1) };
        db.AddRange(work, session, new Workstream { Id = project, OrganizationId = org });
        AgentRunLog Call() { var l = WorkEfficiencyTests.Call(org); l.StartedAt = Now; l.ProviderStartedAt = Now;
            l.AgentInstallationId = installation; l.AgentWorkItemId = work.Id; return l; }
        var good = Call(); var running = Call(); running.CompletedAt = null; running.Status = "Running";
        var foreign = Call(); foreign.OrganizationId = Guid.NewGuid();
        var wrongInstallation = Call(); wrongInstallation.AgentInstallationId = Guid.NewGuid();
        db.AddRange(good, running, foreign, wrongInstallation); await db.SaveChangesAsync();
        Assert.Equal(1, await InferenceSourceAttribution.BackfillAsync(db));
        Assert.Equal(0, await InferenceSourceAttribution.BackfillAsync(db));
        var repaired = await db.AgentRunLogs.SingleAsync(x => x.Id == good.Id);
        Assert.Equal(project, repaired.WorkstreamId); Assert.Equal(10, repaired.ReportedInputTokens);
        Assert.Equal(5, repaired.ReportedOutputTokens);
        Assert.Equal(3, await db.AgentRunLogs.CountAsync(x => x.WorkstreamId == null));
    }

    [Fact]
    public async Task HistoricalClaim_LinksSimpleCommitment_ButDoesNotGuessPlanChildFocus()
    {
        await using var db = WorkEfficiencyTests.Database(); var org = Guid.NewGuid(); var installation = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, Status = WorkTaskStatus.Completed,
            CreatedAt = Now.AddDays(-1), UpdatedAt = Now.AddHours(1) };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = org.ToString(), AgentInstallationId = installation,
            SourceType = "platform-event", SourceId = eventId.ToString() };
        db.AddRange(item, work, new WorkItemActivity { Id = Guid.NewGuid(), OrganizationId = org, WorkItemId = item.Id,
            EventType = "agent.ticket.claimed", ActorKind = CSweet.Domain.Security.GrantSubjectKind.AgentInstallation,
            ActorSubjectId = installation, OccurredAt = Now.AddMinutes(-1), IdempotencyKey = $"agent-ticket-claim:{eventId:N}:{item.Id:N}:1" });
        await db.SaveChangesAsync();
        var log = WorkEfficiencyTests.Call(org); log.StartedAt = Now; log.ProviderStartedAt = Now; log.AgentInstallationId = installation;
        await InferenceSourceAttribution.CaptureAsync(db, log, work.Id, historical: true);
        Assert.Equal(item.Id, log.WorkItemId); Assert.Null(log.WorkstreamId);
        Assert.Equal("ClaimHistorical", log.AttributionKind);
        item.PlanningSpecificationJson = System.Text.Json.JsonSerializer.Serialize(new { personalPlan = new { rootItemId = item.Id } });
        await db.SaveChangesAsync(); log.WorkItemId = null;
        await InferenceSourceAttribution.CaptureAsync(db, log, work.Id, historical: true);
        Assert.Null(log.WorkItemId);
    }

    [Fact]
    public async Task ChatProject_UsesInboxTurn_NotCallerTelemetry_AndSkipsChangedHistoricalConversation()
    {
        await using var db = WorkEfficiencyTests.Database(); var org = Guid.NewGuid(); var installation = Guid.NewGuid();
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org };
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org, AgentInstallationId = installation };
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = project.Id,
            CreatedAt = Now.AddHours(-2), UpdatedAt = Now.AddHours(-1) };
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = org, Conversation = chat,
            ConversationId = chat.Id, TargetAgentOrganizationUserId = employee.Id };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = org.ToString(), AgentInstallationId = installation,
            SourceType = "chat-turn", SourceId = turn.Id.ToString() };
        db.AddRange(project, employee, chat, turn, work); await db.SaveChangesAsync();
        var log = WorkEfficiencyTests.Call(org); log.StartedAt = Now; log.ProviderStartedAt = Now;
        log.AgentInstallationId = installation; log.ChatTurnId = Guid.NewGuid();
        await InferenceSourceAttribution.CaptureAsync(db, log, work.Id, historical: false);
        Assert.Equal(project.Id, log.WorkstreamId); Assert.Null(log.WorkItemId);
        chat.UpdatedAt = Now.AddHours(1); await db.SaveChangesAsync();
        log.WorkstreamId = null;
        await InferenceSourceAttribution.CaptureAsync(db, log, work.Id, historical: true);
        Assert.Null(log.WorkstreamId);
        log.MemoryErasedAt = Now;
        await InferenceSourceAttribution.CaptureAsync(db, log, work.Id, historical: false);
        Assert.Null(log.WorkstreamId);
    }

    [Fact]
    public async Task HistoricalExecution_UsesDispatchInterval_InsteadOfCurrentTask()
    {
        await using var db = WorkEfficiencyTests.Database(); var org = Guid.NewGuid(); var installation = Guid.NewGuid();
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = org.ToString(), AgentInstallationId = installation };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItem = work, AgentWorkItemId = work.Id,
            ClaimedAt = Now.AddMinutes(-2), LeaseExpiresAt = Now.AddMinutes(10), FinishedAt = Now.AddMinutes(2) };
        var task = Guid.NewGuid(); var parent = Guid.NewGuid();
        db.AddRange(work, attempt);
        await db.SaveChangesAsync();
        db.WorkExecutionIntervals.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, AgentWorkAttemptId = attempt.Id,
            WorkItemId = task, AncestorWorkItemIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { parent }),
            StartedAt = Now.AddMinutes(-1), ConfirmedThrough = Now.AddMinutes(1), EndedAt = Now.AddMinutes(1) });
        await db.SaveChangesAsync();
        var log = WorkEfficiencyTests.Call(org); log.StartedAt = Now; log.ProviderStartedAt = Now;
        log.AgentInstallationId = installation; log.AgentWorkItemId = work.Id; log.AgentWorkAttemptId = attempt.Id;
        await InferenceSourceAttribution.CaptureAsync(db, log, work.Id, historical: true);
        Assert.Equal(task, log.WorkItemId); Assert.Contains(parent.ToString(), log.AncestorWorkItemIdsJson);
        Assert.Equal("ExecutionBackfill", log.AttributionKind);
    }

    [Theory]
    [InlineData(0, 1, 0, 1, false, "Pending")]
    [InlineData(0, 1, 0, 0, false, "Unknown")]
    [InlineData(0, 0, 0, 0, true, "Unknown — no linked calls")]
    [InlineData(0, 1, 1, 0, false, "0")]
    [InlineData(15, 2, 1, 1, false, "15 reported · incomplete")]
    public void UsageDisplay_DistinguishesUnknownPendingMeasuredZeroAndPartial(long value, long calls, long reported,
        long pending, bool unlinked, string expected) => Assert.Equal(expected, TokenUsageDisplay.Format(value, calls, reported, pending, unlinked));

    [Fact]
    public void Coverage_DistinguishesPendingUnknownPartialAndMeasuredZero()
    {
        var org = Guid.NewGuid(); var zero = WorkEfficiencyTests.Call(org, 0, 0);
        var running = WorkEfficiencyTests.Call(org, null, null); running.Status = "Running"; running.CompletedAt = null;
        var unknown = WorkEfficiencyTests.Call(org, null, null); unknown.Status = "Cancelled";
        var partial = WorkEfficiencyTests.Call(org, 7, null);
        var coverage = InferenceMeasurements.Coverage([zero, running, unknown, partial]);
        Assert.Equal(1, coverage.FullyReportedCalls); Assert.Equal(1, coverage.PendingCalls);
        Assert.Equal(2, coverage.UnknownCalls); Assert.Equal(2, coverage.InputReportedCalls);
    }
}

