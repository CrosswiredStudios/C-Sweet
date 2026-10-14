using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Communications;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthTests
{
    [Fact]
    public async Task EvidenceOrRetryFreshnessChangesDoNotRepeatTheManagerReport()
    {
        await using var f = await Fixture.Create(); var (incident, ticket) = await PrepareOwnerReport(f);
        var message = Assert.Single(f.Db.CoreConversationMessages);
        var receipts = f.Db.ProjectIncidentDeliveries.Count(x => x.DeliveredAt != null);
        ticket.Revision++; ticket.UpdatedAt = f.Clock.Now.AddSeconds(15);
        incident.Revision++;
        f.Db.ProjectIncidentDeliveries.Add(new() { Id = Guid.NewGuid(), IncidentId = incident.Id, RecipientId = f.Ceo.Id,
            SenderId = f.Chief.Id, Revision = incident.Revision, NextAttemptAt = f.Clock.Now });
        await f.Db.SaveChangesAsync(); await f.Service.DeliverAsync(default);
        Assert.Equal(message.Content, Assert.Single(f.Db.CoreConversationMessages).Content);
        Assert.Single(f.Db.UserNotifications);
        Assert.Equal(receipts + 1, f.Db.ProjectIncidentDeliveries.Count(x => x.DeliveredAt != null));
        var attention = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        Assert.Contains("Project records have changed", attention.OwnerReport);
        Assert.Equal("Outdated", attention.ResolutionDecision!.Status);
    }

    [Fact]
    public async Task AuthoritativeRecoveryClosesOnlyItsPendingChoicesAndRemovesAttention()
    {
        await using var f = await Fixture.Create(); var (incident, ticket) = await PrepareOwnerReport(f);
        var choice = Assert.Single(f.Db.ExecutiveDecisions);
        var unrelated = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = f.Org, ConversationId = choice.ConversationId,
            RequestingInstallationId = choice.RequestingInstallationId, Prompt = "Unrelated question", OptionsJson = "[]", IdempotencyKey = "unrelated" };
        f.Db.ExecutiveDecisions.Add(unrelated);
        ticket.Status = WorkTaskStatus.Completed;
        await f.Db.SaveChangesAsync(); await f.Review(); await f.Service.DeliverAsync(default);
        Assert.Equal("Resolved", incident.Status);
        Assert.Equal(ExecutiveDecisionStatus.Cancelled, choice.Status);
        Assert.Equal(ExecutiveDecisionStatus.Pending, unrelated.Status);
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        Assert.Contains(f.Db.ApplicationRealtimeOutbox, x => x.EventType == CSweet.Contracts.Communications.CommunicationEvents.DecisionUpdated);
        Assert.Equal(2, f.Db.CoreConversationMessages.Count());
    }

    [Fact]
    public async Task DiscoveryClosesLegacyResolvedQuestionsOnceWithoutDeletingMessages()
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        var choice = Assert.Single(f.Db.ExecutiveDecisions);
        incident.Status = "Resolved"; await f.Db.SaveChangesAsync();
        await f.Service.ReviewDueAsync(default);
        Assert.Equal(ExecutiveDecisionStatus.Cancelled, choice.Status);
        var events = f.Db.ApplicationRealtimeOutbox.Count();
        await f.Service.ReviewDueAsync(default);
        Assert.Equal(events, f.Db.ApplicationRealtimeOutbox.Count());
        Assert.Single(f.Db.CoreConversationMessages);
    }

    [Fact]
    public async Task InvalidOwnerReportsDoNotLeaveAReceiptOrPartiallyReplaceFindings()
    {
        await using var f = await Fixture.Create(); await f.FailTicket(); var incident = Assert.Single(f.Db.ProjectIncidents);
        var facts = incident.Facts;
        var invalid = new IncidentOwnerReport("Invalid", "Summary", "Recovery", "Next", [], "missing", new string('A', 64));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ReportAsync(f.Org, f.Producer.Id,
            new(incident.Id, incident.Revision, "invalid-owner", "Should not replace facts", "Cause", "Missing", "Action") { OwnerReport = invalid }, default));
        await f.Db.SaveChangesAsync();
        Assert.Empty(f.Db.ProjectIncidentReceipts); Assert.Equal(facts, incident.Facts); Assert.Null(incident.OwnerReportJson);
    }

    private static async Task<(ProjectIncident Incident, WorkTask Ticket)> PrepareOwnerReport(Fixture f)
    {
        var ticket = await f.FailTicket(); ticket.Title = "Reconcile scope, technical backlog and delivery staffing";
        ticket.BlockReason = "Incorporate recorded authority direction in an updated accepted brief before committing scope.";
        await f.Db.SaveChangesAsync();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        foreach (var actor in new[] { f.Producer.Id, f.Director.Id })
            await f.Service.ForwardAsync(f.Org, actor, new(incident.Id, incident.Revision, $"forward:{actor}", "Management review"), default);
        f.Chief.AgentInstallation = new() { Id = f.Chief.AgentInstallationId!.Value, BusinessId = f.Org.ToString(),
            Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = f.Chief.AgentInstallationId.Value,
                RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { ProjectHealthCapabilities.Presentation, ProjectHealthCapabilities.Resolution }),
                EventSubscriptionsJson = JsonSerializer.Serialize(new[] { ProjectHealthEvents.OwnerReportRequested }) } };
        f.Db.Add(f.Chief.AgentInstallation);
        await f.Db.SaveChangesAsync();
        var state = await f.Service.CurrentIncidentStateAsync(incident, default);
        var report = new IncidentOwnerReport("Prism Break: scope reconciliation is blocked",
            "The scope reconciliation task remains blocked. Recorded authority direction must be incorporated into the accepted brief. Remaining logistics facts must be collected while unaffected work continues.",
            "No completed recovery is recorded.", "Coordinate a brief revision with the accountable Producer.",
            [new("reconcile", "Reconcile the accepted brief", "Coordinate the accepted direction with the Producer.", "Coordinate the accepted brief revision with the accountable Producer, preserving approvals."),
             new("evidence", "Collect missing logistics facts", "Identify the remaining contractor and playtest facts.", "Identify and request the missing logistics facts.")], "reconcile", state.Hash);
        await f.Service.ReportAsync(f.Org, f.Chief.Id, new(incident.Id, incident.Revision, "owner-report", incident.Facts,
            "Recorded scope reconciliation blocker", "Remaining logistics facts", report.NextStep) { OwnerReport = report }, default);
        await f.Service.DeliverAsync(default);
        return (incident, ticket);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerReportAndChoicesShareOneAttentionItemAndAnswerStartsOneTurn(bool custom)
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        var attention = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        Assert.Equal(f.Chief.Id, attention.ReporterOrganizationUserId);
        Assert.Contains("Remaining logistics facts", attention.OwnerReport);
        var reportMessage = Assert.Single(f.Db.CoreConversationMessages);
        Assert.Contains(attention.OwnerReport!.Split('\n')[0], reportMessage.Content);
        var service = new ExecutiveDecisionService(f.Db, new ChatTurnService(f.Db));
        Assert.Empty(await service.ListPendingForUserAsync(f.Org, f.Ceo.Id));
        var choice = attention.ResolutionDecision!;
        Assert.Equal(incident.Id, choice.IncidentId);
        var request = new AnswerExecutiveDecisionRequest(custom ? null : "reconcile", custom ? "Ask me for the contractor's identity first" : null, "answer-key");
        var answer = await service.AnswerAsync(f.Org, attention.ReportConversationId!.Value, choice.Id, f.Ceo.Id, request);
        var replay = await service.AnswerAsync(f.Org, attention.ReportConversationId.Value, choice.Id, f.Ceo.Id, request);
        Assert.True(answer.Succeeded, answer.Message); Assert.Equal(answer.Turn!.Id, replay.Turn!.Id); Assert.Single(f.Db.ChatTurns);
        Assert.Equal("Open", incident.Status); Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        var resolved = await f.Service.ReadResolutionAsync(f.Org, f.Chief.Id, new(choice.Id, answer.Turn.Id), default);
        Assert.Equal(custom ? request.SomethingElse : ProjectHealthService.OwnerReport(incident)!.Options[0].IntendedOutcome, resolved.IntendedOutcome);
        Assert.False(resolved.AlreadyHandled);
    }

    [Theory]
    [InlineData("work-changed")]
    [InlineData("resolved")]
    [InlineData("reporter-deactivated")]
    [InlineData("hierarchy-changed")]
    public async Task StaleOrUnauthorizedIncidentChoicesCannotStartRecovery(string change)
    {
        await using var f = await Fixture.Create(); var (incident, ticket) = await PrepareOwnerReport(f);
        var attention = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        switch (change)
        {
            case "work-changed": ticket.Revision++; ticket.BlockReason = "Different blocker"; break;
            case "resolved": incident.Status = "Resolved"; break;
            case "reporter-deactivated": f.Chief.IsActive = false; break;
            case "hierarchy-changed": f.Director.ReportsToOrganizationUserId = f.Ceo.Id; break;
        }
        await f.Db.SaveChangesAsync();
        var service = new ExecutiveDecisionService(f.Db, new ChatTurnService(f.Db));
        var response = await service.AnswerAsync(f.Org, attention.ReportConversationId!.Value, attention.ResolutionDecision!.Id, f.Ceo.Id, new("reconcile", null, "answer"));
        Assert.False(response.Succeeded); Assert.Empty(f.Db.ChatTurns);
        Assert.Equal("Pending", f.Db.ExecutiveDecisions.Single().Status.ToString());
    }

    [Fact]
    public async Task ReportPresentationUpdatesDoNotChangeRoutingOrDeadlineAndReplaySafely()
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        var recipient = incident.CurrentRecipientId; var deadline = incident.EscalateAt;
        var request = new UpdateManagementIncidentPresentation(incident.Id, incident.Revision, "presentation-update",
            ProjectHealthService.OwnerReport(incident)! with { Recovery = "The accepted brief has not yet been updated." });
        await f.Service.UpdatePresentationAsync(f.Org, f.Chief.Id, request, default);
        await f.Service.UpdatePresentationAsync(f.Org, f.Chief.Id, request, default);
        Assert.Equal(recipient, incident.CurrentRecipientId); Assert.Equal(deadline, incident.EscalateAt); Assert.Equal("Open", incident.Status);
        Assert.Equal(2, incident.ReportRevision);
        await f.Service.DeliverAsync(default);
        Assert.Equal(2, f.Db.CoreConversationMessages.Count());
        Assert.Single(f.Db.ExecutiveDecisions.Where(x => x.Status == ExecutiveDecisionStatus.Pending));
        Assert.Single(f.Db.ExecutiveDecisions.Where(x => x.Status == ExecutiveDecisionStatus.Superseded));
    }

    [Fact]
    public async Task RelatedDecisionsAreCurrentFactsRatherThanRepeatedUnresolvedQuestions()
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        foreach (var summary in new[] { "Audio contractor identity is missing; prototype can proceed.", "Playtest budget is missing; content can proceed." })
            f.Db.WorkstreamDecisions.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
                TypeKey = "planning", Summary = summary, Status = "Decided", Rationale = summary, CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now });
        await f.Db.SaveChangesAsync();
        var diagnostics = await f.Service.ReadDiagnosticsAsync(f.Org, f.Chief.Id, new(incident.Id), default);
        Assert.Equal(2, diagnostics.CurrentState!.Facts.Count(x => x.Kind == "CurrentProjectDecision"));
        Assert.All(diagnostics.CurrentState.Facts.Where(x => x.Kind == "CurrentProjectDecision"), x => Assert.Contains("Status: Decided", x.Detail));
        Assert.NotEqual(ProjectHealthService.OwnerReport(incident)!.SourceHash, diagnostics.CurrentState.Hash);
        Assert.Equal("Outdated", Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default)).ResolutionDecision!.Status);
    }

    [Fact]
    public async Task RecoveryOutcomeKeepsFollowUpIncidentBoundAndReplaysItsOwnSavedResult()
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        var attention = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        var service = new ExecutiveDecisionService(f.Db, new ChatTurnService(f.Db));
        var answered = await service.AnswerAsync(f.Org, attention.ReportConversationId!.Value, attention.ResolutionDecision!.Id, f.Ceo.Id,
            new("evidence", null, "answer"));
        Assert.True(answered.Succeeded, answered.Message);
        var request = new UpdateManagementIncidentPresentation(incident.Id, incident.Revision,
            $"resolution-result:{attention.ResolutionDecision.Id:N}", ResolutionDecisionId: attention.ResolutionDecision.Id,
            ResolutionOutcome: "Please provide the contractor identity and playtest recruitment budget.");
        await f.Service.UpdatePresentationAsync(f.Org, f.Chief.Id, request, default);
        await f.Service.DeliverAsync(default);
        var followUp = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        Assert.Equal("Pending", followUp.ResolutionDecision!.Status);
        Assert.NotEqual(attention.ResolutionDecision.Id, followUp.ResolutionDecision.Id);
        var original = await f.Service.ReadResolutionAsync(f.Org, f.Chief.Id, new(attention.ResolutionDecision.Id, answered.Turn!.Id), default);
        Assert.True(original.AlreadyHandled); Assert.Equal(request.ResolutionOutcome, original.RecordedOutcome);
        f.Db.ChatTurns.Single().Status = ChatTurnStatus.Completed;
        await f.Db.SaveChangesAsync();
        var reply = await service.AnswerAsync(f.Org, followUp.ReportConversationId!.Value, followUp.ResolutionDecision.Id, f.Ceo.Id,
            new(null, "The contractor is confirmed; request a playtest budget approval.", "follow-up"));
        Assert.True(reply.Succeeded, reply.Message); Assert.Equal("Open", incident.Status);
        var routed = await f.Service.ReadResolutionAsync(f.Org, f.Chief.Id, new(followUp.ResolutionDecision.Id, reply.Turn!.Id), default);
        Assert.Contains("contractor is confirmed", routed.IntendedOutcome);
    }
}
