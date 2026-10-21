using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Core;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthTests
{
    // Delivery tests model an actual owner requirement explicitly. Routing a failure to the
    // owner, or merely having resolution choices, no longer establishes this precondition.
    private static async Task RecordOwnerRequirement(Fixture f, ProjectIncident incident)
    {
        var reporterId = JsonSerializer.Deserialize<ManagementIncidentHop[]>(incident.HistoryJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.LastOrDefault()?.FromEmployeeId ?? incident.ProducerEmployeeId;
        var reporter = f.Db.CoreOrganizationUsers.Single(x => x.Id == reporterId);
        reporter.AgentInstallation ??= new() { Id = reporter.AgentInstallationId!.Value, BusinessId = f.Org.ToString() };
        reporter.AgentInstallation.Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = reporter.AgentInstallation.Id,
            RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { ProjectHealthCapabilities.Presentation, ProjectHealthCapabilities.Resolution }) };
        if (!f.Db.AgentInstallations.Any(x => x.Id == reporter.AgentInstallation.Id)) f.Db.Add(reporter.AgentInstallation);
        else f.Db.Add(reporter.AgentInstallation.Grant);
        var presentation = await f.Service.PresentAsync(incident, default);
        incident.OwnerReportJson = JsonSerializer.Serialize(new IncidentOwnerReport(presentation.Title, presentation.Summary,
            presentation.Recovery, "Open the affected item and authorize the required repair.", [new("approve", "Authorize repair", "Authorize the required external repair.", "Authorize the repair."),
                new("defer", "Defer repair", "Leave the affected work paused.", "Defer the repair.")], "approve",
            (await f.Service.CurrentIncidentStateAsync(incident, default)).Hash) {
                RequiresOwnerInput = true, OwnerInputReason = presentation.NextStep }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        incident.ReporterOrganizationUserId = reporter.Id; incident.ReportRevision++;
        await f.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task RoutineRecoveryWithdrawsOwnerChoiceWithoutRewindingEscalationOrResolvingFailure()
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        var message = Assert.Single(f.Db.CoreConversationMessages).Content;
        var routine = ProjectHealthService.OwnerReport(incident)! with {
            RequiresOwnerInput = false, OwnerInputReason = null, Options = [], RecommendedOptionId = "",
            NextStep = "I will check saved outputs and coordinate remaining work." };
        var request = new UpdateManagementIncidentPresentation(incident.Id, incident.Revision, "routine-recovery", routine);
        await f.Service.UpdatePresentationAsync(f.Org, f.Chief.Id, request, default);
        await f.Service.UpdatePresentationAsync(f.Org, f.Chief.Id, request, default);
        var card = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        Assert.True(card.IsStatusReport); Assert.Null(card.ResolutionDecision);
        Assert.Equal(f.Ceo.Id, incident.CurrentRecipientId);
        Assert.Equal("Open", incident.Status);
        Assert.Equal(IncidentDispositions.Escalate, incident.Disposition);
        Assert.Null(incident.ReviewAt);
        Assert.Null(incident.EscalateAt);
        Assert.Equal(ExecutiveDecisionStatus.Cancelled, Assert.Single(f.Db.ExecutiveDecisions).Status);
        Assert.All(f.Db.UserNotifications, x => Assert.NotNull(x.DismissedAt));
        Assert.Equal(message, Assert.Single(f.Db.CoreConversationMessages).Content);
        f.Clock.Now += TimeSpan.FromMinutes(6);
        await f.Service.ReviewDueAsync(default);
        Assert.Null(incident.ReviewAt);
        Assert.Contains(f.Db.ProjectIncidentDeliveries, x => x.RecipientId == f.Ceo.Id && x.DeliveredAt == null);
    }

    [Fact]
    public async Task Repeated_routine_reports_cannot_extend_deadlines_or_stop_the_reporting_chain()
    {
        await using var f = await Fixture.Create();
        await f.FailTicket();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        var deadline = incident.EscalateAt;
        for (var pass = 0; pass < 4; pass++)
        {
            var state = await f.Service.CurrentIncidentStateAsync(incident, default);
            var report = new IncidentOwnerReport("Planning remains blocked", "The build plan is incomplete.",
                "A corrected retry is running.", "Wait for the retry.", [], "", state.Hash) { RequiresOwnerInput = false };
            await f.Service.ReportAsync(f.Org, f.Producer.Id,
                new(incident.Id, incident.Revision, "routine-" + pass, "Planning failed", "Invalid output", "No completed proposal", "Retry is running") {
                    OwnerReport = report, Disposition = IncidentDispositions.Investigating }, default);
            Assert.Equal(deadline, incident.EscalateAt);
            var presentation = await f.Service.PresentAsync(incident, default);
            Assert.Contains("No correlated retry", presentation.Recovery);
            Assert.DoesNotContain("Wait for the retry", presentation.NextStep);
            Assert.DoesNotContain("retry is running", presentation.Recovery);
            f.Clock.Now += TimeSpan.FromMinutes(4);
        }
        await f.Service.ReviewDueAsync(default);
        Assert.Equal(f.Director.Id, incident.CurrentRecipientId);
        Assert.Equal("Open", incident.Status);
        // Once a hop has advanced, an older reporter cannot use presentation to take it back.
        Assert.NotEqual(f.Producer.Id, incident.CurrentRecipientId);
    }

    [Fact]
    public async Task LegacyChoicesAreNotProofOfOwnerInputEvenWithCurrentEvidence()
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        incident.OwnerReportJson = JsonSerializer.Serialize(ProjectHealthService.OwnerReport(incident)! with {
            RequiresOwnerInput = null, OwnerInputReason = null }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        await f.Review();
        Assert.Equal("Open", incident.Status);
        Assert.Equal(ExecutiveDecisionStatus.Cancelled, Assert.Single(f.Db.ExecutiveDecisions).Status);
    }

    [Theory]
    [InlineData(AgentWorkStatus.DeadLetter, false, "No correlated retry")]
    [InlineData(AgentWorkStatus.Pending, false, "retry is queued")]
    [InlineData(AgentWorkStatus.Leased, true, "active execution lease")]
    [InlineData(AgentWorkStatus.Leased, false, "No correlated retry")]
    public async Task InterruptedReplyUsesCorrelatedExecutionEvidenceInsteadOfManagerRetryClaims(AgentWorkStatus status, bool liveLease, string expected)
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Org, ConversationId = Guid.NewGuid(),
            TargetAgentOrganizationUserId = f.Producer.Id, Status = ChatTurnStatus.CompletedWithWarnings,
            ErrorCode = "memory.runtime_reset", ErrorMessage = "Reply interrupted. Saved actions may exist.",
            CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        var retry = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Org, ConversationId = turn.ConversationId,
            TargetAgentOrganizationUserId = turn.TargetAgentOrganizationUserId, RetryOfTurnId = turn.Id,
            CreatedAt = f.Clock.Now.AddSeconds(1), UpdatedAt = f.Clock.Now.AddSeconds(1) };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Producer.AgentInstallationId!.Value, SourceType = "chat-turn", SourceId = retry.Id.ToString(),
            Status = status, AttemptCount = 1, CreatedAt = f.Clock.Now, DeadlineAt = f.Clock.Now.AddMinutes(10) };
        f.Db.AddRange(turn, retry, work, new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id,
            Attempt = 1, LeaseExpiresAt = liveLease ? f.Clock.Now.AddMinutes(5) : f.Clock.Now.AddMinutes(-1) });
        incident.Fingerprint = $"chat:{turn.Id:N}"; incident.AffectedWorkItemId = null;
        await f.Db.SaveChangesAsync();
        var current = await f.Service.CurrentIncidentStateAsync(incident, default);
        Assert.Contains(current.Facts, x => x.Kind == "CurrentInterruptedReply" && x.Detail.Contains(expected));
        incident.OwnerReportJson = JsonSerializer.Serialize(ProjectHealthService.OwnerReport(incident)! with {
            Title = "Automatic retry running", Summary = "The system automatically retried this work.",
            Recovery = "The retry is running.", NextStep = "Wait for the automatic retry.", SourceHash = current.Hash
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await f.Db.SaveChangesAsync();
        var presentation = await f.Service.PresentAsync(incident, default);
        Assert.Contains(expected, presentation.Recovery);
        Assert.DoesNotContain("Automatic retry running", presentation.Title);
        Assert.DoesNotContain("automatically retried", presentation.Summary);
        Assert.DoesNotContain("Wait for the automatic retry", presentation.NextStep);
        retry.ConversationId = Guid.NewGuid(); // Unrelated work cannot certify this incident's recovery.
        await f.Db.SaveChangesAsync();
        Assert.Contains("No correlated retry", (await f.Service.PresentAsync(incident, default)).Recovery);
        Assert.NotEqual(current.Hash, (await f.Service.CurrentIncidentStateAsync(incident, default)).Hash);
    }

    [Theory]
    [InlineData(AgentWorkStatus.Pending, false, AgentCoordinationStatus.Active, "retry is queued")]
    [InlineData(AgentWorkStatus.Leased, true, AgentCoordinationStatus.Active, "active execution lease")]
    [InlineData(AgentWorkStatus.Leased, false, AgentCoordinationStatus.Active, "No correlated retry")]
    [InlineData(AgentWorkStatus.Leased, true, AgentCoordinationStatus.Blocked, "No correlated retry")]
    [InlineData(AgentWorkStatus.Completed, false, AgentCoordinationStatus.Blocked, "No correlated retry")]
    public async Task Coordination_reports_require_the_current_correlated_attempt_and_live_lease(
        AgentWorkStatus status, bool liveLease, AgentCoordinationStatus sessionStatus, string expected)
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        var session = new CSweet.Domain.Communications.AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            InitiatorInstallationId = f.Producer.AgentInstallationId!.Value, TargetInstallationId = Guid.NewGuid(),
            Status = sessionStatus, Subject = "Technical planning", CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = session.TargetInstallationId,
            SourceType = "agent-coordination", CorrelationId = session.Id.ToString("D"), Status = status,
            CreatedAt = f.Clock.Now, DeadlineAt = f.Clock.Now.AddMinutes(10), AttemptCount = 1 };
        session.CurrentAgentWorkItemId = work.Id;
        incident.Fingerprint = $"coordination:{session.Id:N}"; incident.AffectedWorkItemId = null;
        f.Db.AddRange(session, work, new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1,
            LeaseExpiresAt = liveLease ? f.Clock.Now.AddMinutes(5) : f.Clock.Now.AddMinutes(-1) });
        await f.Db.SaveChangesAsync();
        var state = await f.Service.CurrentIncidentStateAsync(incident, default);
        incident.OwnerReportJson = JsonSerializer.Serialize(ProjectHealthService.OwnerReport(incident)! with {
            SourceHash = state.Hash, Title = "Corrected retry running", Summary = "The retry is in progress.",
            Recovery = "The retry is running.", RequiresOwnerInput = false, Options = [], RecommendedOptionId = "", OwnerInputReason = null,
            NextStep = "Wait while the retry runs." }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await f.Db.SaveChangesAsync();
        var presentation = await f.Service.PresentAsync(incident, default);
        Assert.Contains(expected, presentation.Recovery);
        Assert.DoesNotContain("Corrected retry running", presentation.Title);
        Assert.DoesNotContain("in progress", presentation.Summary);
        var before = (await f.Service.CurrentIncidentStateAsync(incident, default)).Hash;
        work.CorrelationId = Guid.NewGuid().ToString("D"); // a current pointer alone cannot bind unrelated work
        await f.Db.SaveChangesAsync();
        Assert.Contains("No correlated retry", (await f.Service.PresentAsync(incident, default)).Recovery);
        if (sessionStatus == AgentCoordinationStatus.Active && status is AgentWorkStatus.Pending or AgentWorkStatus.Leased)
            Assert.NotEqual(before, (await f.Service.CurrentIncidentStateAsync(incident, default)).Hash);
    }
}
