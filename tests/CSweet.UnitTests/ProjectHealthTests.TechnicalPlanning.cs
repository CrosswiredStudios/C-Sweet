using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Core;
using AgentCoordinationSession = CSweet.Domain.Communications.AgentCoordinationSession;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Planning_cards_group_only_an_unambiguous_authorized_cycle(bool ambiguous)
    {
        await using var f = await Fixture.Create(); var (incident, ticket) = await PrepareOwnerReport(f);
        const string diagnostic = "[csweet.planning.generator] scope coverage page 1 exceeds 128000 characters.";
        ticket.Status = CSweet.Domain.Core.WorkTaskStatus.Blocked; ticket.BlockReason = diagnostic;
        ticket.AssignedEmployeeId = f.Producer.Id;
        ticket.PersonalWorkContextJson = JsonSerializer.Serialize(new { boardId = f.Board.Id, sourceFingerprint = "exact-cycle" });
        AgentCoordinationSession Session() => new() { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            ConversationId = Guid.NewGuid(), SourceKind = "Board", SourceBoardId = f.Board.Id,
            InitiatorOrganizationUserId = f.Producer.Id, InitiatorInstallationId = f.Producer.AgentInstallationId!.Value,
            Status = AgentCoordinationStatus.Blocked, FinalSummary = diagnostic, CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now,
            Turns = [new() { Id = Guid.NewGuid(), SpeakerOrganizationUserId = f.Producer.Id,
                ArtifactKey = "exact-cycle", ArtifactType = "video-game.production.planning-cycle.v1" }] };
        var session = Session(); f.Db.Add(session);
        if (ambiguous) f.Db.Add(Session());
        var collaborationIncident = (CSweet.Domain.Core.ProjectIncident)f.Db.Entry(incident).CurrentValues.ToObject();
        collaborationIncident.Id = Guid.NewGuid(); collaborationIncident.AffectedWorkItemId = null;
        collaborationIncident.Fingerprint = $"coordination:{session.Id:N}";
        f.Db.Add(collaborationIncident);
        var delivery = (CSweet.Domain.Core.ProjectIncidentDelivery)f.Db.Entry(f.Db.ProjectIncidentDeliveries.Single(x => x.DeliveredAt != null)).CurrentValues.ToObject();
        delivery.Id = Guid.NewGuid(); delivery.IncidentId = collaborationIncident.Id; f.Db.Add(delivery);
        await f.Db.SaveChangesAsync();
        var cards = await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default);
        Assert.Equal(ambiguous ? 2 : 1, cards.Count);
        Assert.All(cards, card => { Assert.True(card.IsStatusReport); Assert.Null(card.ResolutionDecision); });
        if (!ambiguous)
        {
            Assert.Equal(2, cards[0].RelatedIncidentCount); Assert.Equal(session.Id, cards[0].Collaboration!.SessionId);
            Assert.False(cards[0].Collaboration!.CanRetry);
        }
        Assert.Empty(await f.Service.ReadAttentionAsync(Guid.NewGuid(), f.Ceo.Id, default));
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Director.Id, default));
        session.Status = AgentCoordinationStatus.Completed; ticket.Status = CSweet.Domain.Core.WorkTaskStatus.Completed;
        if (!ambiguous) { await f.Db.SaveChangesAsync(); Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default)); }
    }

    [Fact]
    public async Task Repeated_legacy_digest_reports_cannot_request_CEO_input_or_repeat_the_same_status_report()
    {
        await using var f = await Fixture.Create(); var (incident,ticket)=await PrepareOwnerReport(f);
        ticket.Status=CSweet.Domain.Core.WorkTaskStatus.Blocked;
        ticket.BlockReason="Repeated issue: I couldn't finish this ticket. Progressive planning retained the existing scope but could not advance: The delivered outline does not match its retained scope digest.";
        await f.Db.SaveChangesAsync();
        var original=Assert.Single(f.Db.CoreConversationMessages).Content;
        for (var pass=0;pass<8;pass++)
        {
            ticket.Revision++; ticket.UpdatedAt=f.Clock.Now; await f.Db.SaveChangesAsync();
            var state=await f.Service.CurrentIncidentStateAsync(incident,default);
            Assert.Contains(state.Facts,x=>x.Kind=="CurrentPlatformTechnicalIssue");
            var report=ProjectHealthService.OwnerReport(incident)! with { SourceHash=state.Hash,RequiresOwnerInput=true,
                OwnerInputReason="Choose how to repair the planning system.", Options=[new("retry","Retry","Retry the failed planning output.","Retry."),
                    new("defer","Defer","Leave planning paused.","Defer.")],RecommendedOptionId="retry" };
            await f.Service.UpdatePresentationAsync(f.Org,f.Chief.Id,new(incident.Id,incident.Revision,"digest-report-"+pass,report),default);
            await f.Service.DeliverAsync(default);
            Assert.False(ProjectHealthService.OwnerReport(incident)!.RequiresOwnerInput);
            ManagerIncidentCoordinator.ValidateReport(ProjectHealthService.OwnerReport(incident)!);
            Assert.Null(incident.EscalateAt); Assert.Equal(f.Ceo.Id,incident.CurrentRecipientId);
            var card = Assert.Single(await f.Service.ReadAttentionAsync(f.Org,f.Ceo.Id,default));
            Assert.True(card.IsStatusReport); Assert.Null(card.ResolutionDecision);
            f.Clock.Now+=TimeSpan.FromMinutes(5);
        }
        Assert.Equal(2,f.Db.CoreConversationMessages.Count());
        Assert.Contains(f.Db.CoreConversationMessages,x=>x.Content==original);
        Assert.DoesNotContain(f.Db.ExecutiveDecisions,x=>x.Status==CSweet.Domain.Core.ExecutiveDecisionStatus.Pending);
    }

    [Theory]
    [InlineData("Progressive planning is blocked; approved scope and validated pages are retained. Page failed after three attempts: full-scope outline (finish=stop, characters=55644): The page exceeds 40000 characters; omit repetition, never scope.")]
    [InlineData("[csweet.planning.generator] C-Sweet technical issue: outline validation failed.")]
    public async Task Planning_generator_failure_retains_diagnosis_through_managers_and_reaches_CEO_without_a_creative_decision(string diagnostic)
    {
        await using var f = await Fixture.Create(); await f.FailTicket(); var incident = Assert.Single(f.Db.ProjectIncidents);
        var session = new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            Status = AgentCoordinationStatus.Blocked, FinalSummary = diagnostic, Subject = "Technical delivery and decomposition proposal",
            InitiatorInstallationId = f.Producer.AgentInstallationId!.Value, CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        f.Db.Add(session); incident.Fingerprint = $"coordination:{session.Id:N}"; incident.AffectedWorkItemId = null;
        await f.Db.SaveChangesAsync();
        foreach (var manager in new[] { f.Producer, f.Director, f.Chief })
        {
            var state = await f.Service.CurrentIncidentStateAsync(incident, default);
            Assert.Contains(state.Facts, x => x.Kind == "CurrentPlatformTechnicalIssue" && x.Detail.Contains(diagnostic));
            var report = new IncidentOwnerReport("Design disagreement", "The designers disagree.", "A retry is running.",
                "I'll settle the design disagreement myself.", [], "", state.Hash) { RequiresOwnerInput = false };
            var request = new ReportManagementIncident(incident.Id, incident.Revision, "technical-" + manager.Id,
                "Planning is blocked", "Creative conflict", "Unknown disagreement", "Resolve the design disagreement") {
                Disposition = IncidentDispositions.Investigating, OwnerReport = report };
            var before = incident.CurrentRecipientId;
            await f.Service.ReportAsync(f.Org, manager.Id, request, default);
            var count = f.Db.AgentPlatformEventOutbox.Count();
            await f.Service.ReportAsync(f.Org, manager.Id, request, default);
            Assert.Equal(count, f.Db.AgentPlatformEventOutbox.Count()); // replay cannot forward another hop
            Assert.NotEqual(before, incident.CurrentRecipientId);
            Assert.Contains(diagnostic, incident.LikelyCause);
            Assert.Contains("CEO", incident.RecommendedAction);
            Assert.Contains("C-Sweet technical issue", ProjectHealthService.OwnerReport(incident)!.Title);
            var presentation = await f.Service.PresentAsync(incident, default);
            Assert.Contains("C-Sweet technical issue", presentation.Title);
            Assert.DoesNotContain(diagnostic, presentation.Summary);
            Assert.Contains("saved planning pages", presentation.Summary);
            Assert.DoesNotContain("designers disagree", presentation.Summary);
            Assert.Contains("No correlated retry", presentation.Recovery);
        }
        Assert.Equal(f.Ceo.Id, incident.CurrentRecipientId);
        await f.Service.DeliverAsync(default);
        Assert.Contains(f.Db.CoreConversationMessages, x => x.Content.Contains("C-Sweet technical issue"));
        Assert.Empty(f.Db.ExecutiveDecisions);
        var card = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        Assert.True(card.IsStatusReport); Assert.Null(card.ResolutionDecision);
        Assert.Equal(diagnostic, card.DiagnosticDetails); Assert.False(card.Collaboration!.CanRetry);
    }

    [Fact]
    public async Task Previously_misclassified_CEO_report_gets_one_factual_correction_without_rewriting_history()
    {
        await using var f = await Fixture.Create(); var (incident, _) = await PrepareOwnerReport(f);
        var original = Assert.Single(f.Db.CoreConversationMessages).Content;
        const string diagnostic = "Progressive planning is blocked; Page failed after three attempts: full-scope outline exceeds 40000 characters.";
        var session = new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            Status = AgentCoordinationStatus.Blocked, FinalSummary = diagnostic, CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        f.Db.Add(session); incident.Fingerprint = $"coordination:{session.Id:N}"; incident.AffectedWorkItemId = null;
        await f.Db.SaveChangesAsync();
        var report = new IncidentOwnerReport("Design disagreement", "The designers disagree.", "A fix is not confirmed.", "Settle the design disagreement.",
            [], "", (await f.Service.CurrentIncidentStateAsync(incident, default)).Hash) { RequiresOwnerInput = false };
        var request = new UpdateManagementIncidentPresentation(incident.Id, incident.Revision, "technical-correction", report);
        await f.Service.UpdatePresentationAsync(f.Org, f.Chief.Id, request, default);
        await f.Service.DeliverAsync(default);
        await f.Service.UpdatePresentationAsync(f.Org, f.Chief.Id, request, default);
        await f.Service.DeliverAsync(default);
        Assert.Equal(2, f.Db.CoreConversationMessages.Count());
        Assert.Contains(f.Db.CoreConversationMessages, x => x.Content == original);
        Assert.Single(f.Db.CoreConversationMessages, x => x.Content.Contains("C-Sweet technical issue"));
        Assert.Equal(f.Ceo.Id, incident.CurrentRecipientId);
        Assert.Contains(diagnostic, incident.LikelyCause);
        Assert.True(Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default)).IsStatusReport);
    }

    [Fact]
    public async Task Creative_blockers_and_completed_collaborations_are_not_technical_generator_failures()
    {
        await using var f = await Fixture.Create(); await f.FailTicket(); var incident = Assert.Single(f.Db.ProjectIncidents);
        var session = new AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            Status = AgentCoordinationStatus.Blocked, FinalSummary = "Need an engine decision", CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        f.Db.Add(session); incident.Fingerprint = $"coordination:{session.Id:N}"; incident.AffectedWorkItemId = null;
        await f.Db.SaveChangesAsync();
        Assert.DoesNotContain((await f.Service.CurrentIncidentStateAsync(incident, default)).Facts, x => x.Kind == "CurrentPlatformTechnicalIssue");
        session.Status = AgentCoordinationStatus.Completed; session.FinalSummary = "[csweet.planning.generator] old failure";
        await f.Db.SaveChangesAsync();
        Assert.DoesNotContain((await f.Service.CurrentIncidentStateAsync(incident, default)).Facts, x => x.Kind == "CurrentPlatformTechnicalIssue");
    }
}
