extern alias Director;
extern alias Producer;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Setup;
using CSweet.UnitTests;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Collaboration = CSweet.Domain.Communications;
using Marcus = Director::CSweet.Agent.Director.VideoGame.VideoGameDirectorAgent;
using Gabriel = Producer::CSweet.Agent.Producer.VideoGame.SpecialistAgent;
using Events = CSweet.Agent.SDK.CommunicationEvents;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed class DecisionInquiryJourneyTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Marcus_checks_the_stopped_session_then_asks_Gabriel_and_returns_to_the_pending_decision(bool fromProjectReview)
    {
        const string cause = "The brick collision budget needs a measurement on the target device.";
        const string question = "What’s the conflict?";
        const string schedule = "What would that mean for the schedule?";
        const string ask = "When is the target-device measurement scheduled?";
        const string clarification = "The target-device measurement is booked for Thursday. The delivery date is still unconfirmed.";
        await using var studio = new FreshTeamSprintPlatform();
        await studio.OpenAsync("Neon Brick Breaker");
        var marcus = await studio.AddProjectDirectorAsync();
        var gabriel = studio.Gabriel;
        var db = studio.Db;
        var org = studio.Organization;
        var human = await db.CoreOrganizationUsers.SingleAsync(p => p.DisplayName == "Business owner");
        var grant = await db.AgentInstallationGrants.SingleAsync(g => g.AgentInstallationId == marcus.AgentInstallationId);
        grant.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { ProjectHealthCapabilities.Diagnostics, ProjectHealthCapabilities.Resolution,
            ProjectHealthCapabilities.Presentation, PlatformCapabilities.DecisionRead, PlatformCapabilities.ArtifactRead, CommunicationCapabilities.CoordinationStart,
            CommunicationCapabilities.CoordinationRead, CommunicationCapabilities.CoordinationRespond });
        const string detail = "The lab requires an independent repeat measurement.";
        await studio.AddProductionBriefAsync("The shared measurement plan requires two device readings.".PadRight(4100, ' ') + detail, marcus);
        var restricted = await studio.AddProductionBriefAsync("Restricted unshared draft with a speculative release date.", marcus);
        var restrictedGrant = await db.ScopedActionGrants.SingleAsync(g => g.SubjectId == marcus.AgentInstallationId &&
            g.ScopeId == restricted.Document);
        restrictedGrant.RevokedAt = DateTimeOffset.UtcNow;
        var turns = new ChatTurnService(db);
        var decisions = new ExecutiveDecisionService(db, turns);
        var hub = new CommunicationHubService(db, new TestAuditEventWriter(), turns, decisions);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var coordination = new AgentCoordinationService(db, hub, inbox);
        var health = new ProjectHealthService(db, new(db, TimeProvider.System), TimeProvider.System);
        var chat = Guid.NewGuid();
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat, SenderOrganizationUserId = marcus.OrganizationUserId,
            Role = ConversationRole.Assistant, Content = "How should we handle the planning stop?", CreatedAt = DateTimeOffset.UtcNow };
        db.CoreConversations.Add(new() { Id = chat, OrganizationId = org, Kind = ConversationKind.DirectHumanAgent,
            AgentOrganizationUserId = marcus.OrganizationUserId, InitiatedByOrganizationUserId = human.Id,
            Participants = new[] { human.Id, marcus.OrganizationUserId }.Select(id => new ConversationParticipant {
                Id = Guid.NewGuid(), OrganizationUserId = id, ConversationId = chat }).ToList() });
        db.Add(message);
        var stopped = new Collaboration.AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = studio.Project,
            SourceKind = "Board", SourceBoardId = studio.Board,
            InitiatorOrganizationUserId = gabriel.OrganizationUserId, TargetOrganizationUserId = studio.Victor.OrganizationUserId,
            ConversationId = Guid.NewGuid(), Subject = "Collision planning", Status = Collaboration.AgentCoordinationStatus.Blocked,
            Turns = [new() { Id = Guid.NewGuid(), Ordinal = 0, Disposition = "Continue", Content = "Scope review needs a collision measurement.", SpeakerOrganizationUserId = gabriel.OrganizationUserId },
                new() { Id = Guid.NewGuid(), Ordinal = 1, Disposition = "Blocked", Content = cause, SpeakerOrganizationUserId = studio.Victor.OrganizationUserId }] };
        db.Add(stopped);
        var ticket = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = studio.Board, AssignedEmployeeId = gabriel.OrganizationUserId,
            Title = "Reconcile scope and staffing", BlockReason = "Planning stopped", Status = WorkTaskStatus.Blocked,
            PersonalWorkContextJson = JsonSerializer.Serialize(new { workstreamId = studio.Project, coordinationSessionId = stopped.Id }) };
        db.Add(ticket);
        var affected = ticket;
        if (fromProjectReview)
        {
            affected = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = studio.Board,
                AssignedEmployeeId = marcus.OrganizationUserId, Title = "Review blocked work",
                CorrelationId = $"{ManagerWorkRecovery.Prefix}{ticket.Id:N}:19",
                PersonalWorkContextJson = JsonSerializer.Serialize(new { workstreamId = studio.Project, workItemId = ticket.Id }) };
            db.Add(affected);
        }
        var incident = new ProjectIncident { Id = Guid.NewGuid(), OrganizationId = org, WorkstreamId = studio.Project,
            AffectedWorkItemId = affected.Id, ProducerEmployeeId = gabriel.OrganizationUserId, ReporterOrganizationUserId = marcus.OrganizationUserId,
            CurrentRecipientId = human.Id, Fingerprint = $"ticket:{affected.Id:N}", ReportRevision = 1, DetectedAt = DateTimeOffset.UtcNow };
        db.Add(incident);
        await db.SaveChangesAsync();
        var state = await health.CurrentIncidentStateAsync(incident, default);
        var report = new IncidentOwnerReport("Planning stop", "Planning stopped.", "Measurement pending.", "Review the budget.",
            [new("wait", "Wait", "Keep the current budget.", "Wait for the measurement."), new("review", "Review", "Review the budget.", "Request a budget review.")],
            "wait", state.Hash) { RequiresOwnerInput = true, OwnerInputReason = "Only the owner can approve a budget change." };
        incident.OwnerReportJson = JsonSerializer.Serialize(report, JsonSerializerOptions.Web);
        await db.SaveChangesAsync();
        var projectDecision = new WorkstreamDecisionRecord { Id = Guid.NewGuid(), WorkstreamId = studio.Project,
            OrganizationId = org, RequestedByInstallationId = marcus.AgentInstallationId,
            RequestedByOrganizationUserId = marcus.OrganizationUserId, Summary = message.Content, RecommendedOptionId = "wait",
            OptionsJson = JsonSerializer.Serialize(report.Options.Select(o => new { id = o.Id, label = o.Label, description = o.Description })),
            EvidenceJson = JsonSerializer.Serialize(new[] { new { kind = "incident", resourceId = incident.Id } }) };
        if (fromProjectReview) { db.Add(projectDecision); await db.SaveChangesAsync(); }
        var command = new CSweet.Application.Communications.CreateExecutiveDecisionCommand(org, chat, null, message.Id, marcus.AgentInstallationId, message.Content,
            report.Options.Select(o => new CSweet.Application.Communications.CreateExecutiveDecisionOption(o.Id, o.Label, o.Description)).ToArray(),
            "wait", "inkling-decision");
        var card = await decisions.CreateAsync(fromProjectReview ? command with { WorkstreamDecisionId = projectDecision.Id } :
            command with { Incident = new(incident.Id, incident.ReportRevision) });
        var inquiries = new DecisionFollowUpService(db, turns, decisions);
        var runtime = new AgentTestRuntime();
        var peer = new AgentTestRuntime();
        studio.Connect(runtime, marcus);
        studio.Connect(peer, gabriel);
        var provider = Guid.NewGuid();
        AgentTestHost Host(CSweetAgentBase agent, AgentTestRuntime rt, AgentCoordinationParticipant actor) => new(agent, rt,
            new(actor.OrganizationUserId.ToString(), actor.DisplayName, null, actor.Role, null, [], null, null, null),
            org.ToString(), actor.AgentInstallationId.ToString());
        runtime.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (input, _) =>
        {
            Assert.Contains("target device", input.GetRawText());
            Assert.Contains("Scope review needs a collision measurement", input.GetRawText());
            Assert.Contains("requires two device readings", input.GetRawText());
            Assert.DoesNotContain("speculative release date", input.GetRawText());
            var follow = input.GetRawText().Contains(schedule);
            if (!follow && !input.GetRawText().Contains(detail))
            {
                using var evidence = JsonDocument.Parse(input.GetProperty("messages").EnumerateArray().Last().GetProperty("contents")[0]
                    .GetProperty("text").GetString()!);
                var document = evidence.RootElement.GetProperty("records").GetProperty("documents").EnumerateArray()
                    .Single(d => d.GetProperty("title").GetString() == "Production plan");
                return Task.FromResult(JsonSerializer.SerializeToElement(new { contents = new[] { new {
                    kind = "function_call", callId = "read-lab-method", name = "read_document_section",
                    arguments = new { index = document.GetProperty("index").GetInt32(), offset = document.GetProperty("nextOffset").GetInt32() } } } }));
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { text = JsonSerializer.Serialize(new {
                answer = follow ? "I have no confirmed delivery date." : cause, ask = follow ? ask : null,
                recipient = follow ? gabriel.DisplayName : null }) }));
        });
        runtime.RegisterCapability<StartAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationStart,
            (r, ct) => coordination.StartAsync(org, marcus.OrganizationUserId, marcus.AgentInstallationId, r, ct));
        void ConnectResponder(AgentTestRuntime rt, AgentCoordinationParticipant actor) =>
            rt.RegisterCapability<RespondToAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRespond,
                (r, ct) => coordination.RespondAsync(org, actor.OrganizationUserId, actor.AgentInstallationId, r, ct));
        ConnectResponder(runtime, marcus);
        ConnectResponder(peer, gabriel);
        runtime.RegisterCapability<ReadProjectDiagnostics, ProjectDiagnosticsPage>(ProjectHealthCapabilities.Diagnostics,
            (r, ct) => health.ReadDiagnosticsAsync(org, marcus.OrganizationUserId, r, ct));
        peer.RegisterCapability<ReadProjectDiagnostics, ProjectDiagnosticsPage>(ProjectHealthCapabilities.Diagnostics,
            (r, ct) => health.ReadDiagnosticsAsync(org, gabriel.OrganizationUserId, r, ct));
        peer.RegisterCapability<JsonElement, JsonElement>(PlatformCapabilities.LlmChatStream, (input, _) => {
            Assert.Contains(ask, input.GetRawText());
            Assert.Contains(cause, input.GetRawText());
            Assert.Contains("booked for Thursday", input.GetRawText());
            Assert.Contains("requires two device readings", input.GetRawText());
            Assert.DoesNotContain("speculative release date", input.GetRawText());
            return Task.FromResult(JsonSerializer.SerializeToElement(new { text = clarification }));
        });

        async Task<CommunicationMessageReceivedEvent> DeliverQuestion(string text)
        {
            var req = new DecisionQuestionRequest(text, "wait") { RequestId = Guid.NewGuid() };
            Guid turnId;
            if (text == schedule)
            {
                var followUp = new SendCommunicationMessageRequest(text, req.RequestId!.Value.ToString("N"))
                    { FollowUpDecisionId = card.Id, FollowUpOptionId = req.OptionId };
                turnId = (await hub.SendAsync(org, chat, human.Id, followUp))!.Turn!.Id;
                Assert.Equal(turnId, (await hub.SendAsync(org, chat, human.Id, followUp))!.Turn!.Id);
            }
            else
            {
                turnId = (await inquiries.AskAsync(org, chat, card.Id, human.Id, req, default))!.Value;
                Assert.Equal(turnId, await inquiries.AskAsync(org, chat, card.Id, human.Id, req, default));
            }
            var turn = await db.ChatTurns.SingleAsync(t => t.Id == turnId);
            var frozen = await new MemoryRecallDispatchEvidence(db).CapturePromptAsync(new(turn.Id, 1, org, chat,
                turn.UserMessageId, marcus.OrganizationUserId, human.Id, marcus.AgentInstallationId, new string('A', 64)), null, default);
            if (text == schedule) Assert.Contains(cause, frozen.Conversation);
            var callback = new CommunicationMessageReceivedEvent(provider, chat.ToString(), human.Id.ToString(),
                frozen.Agent, frozen.Metadata.Context, turn.Id, 1, turn.UserMessageId);
            await turns.SetStatusAsync(turnId, "Running");
            var result = await Host(new Marcus(), runtime, marcus).DeliverEventAsync(Events.MessageReceived, callback);
            Assert.True(result.Succeeded, result.Error);
            var answer = runtime.Progress.Last().GetProperty("delta").GetString()!;
            var reply = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = chat, ChatTurnId = turnId,
                SenderOrganizationUserId = marcus.OrganizationUserId, Role = ConversationRole.Assistant, Content = answer, CreatedAt = DateTimeOffset.UtcNow };
            db.Add(reply);
            await turns.CompleteAsync(turnId, reply.Id, false);
            return callback;
        }
        await DeliverQuestion(question);
        var callback = await DeliverQuestion(schedule);
        Assert.True((await Host(new Marcus(), runtime, marcus).DeliverEventAsync(Events.MessageReceived, callback)).Succeeded);
        var handoff = Assert.Single(await db.AgentCoordinationSessions.Where(s => s.SourceMessageId == callback.MessageId).ToListAsync());
        Assert.Equal(gabriel.OrganizationUserId, handoff.CurrentOrganizationUserId);
        // New information becomes available after Marcus sent the question.
        incident.EvidenceJson = JsonSerializer.Serialize(new[] { new ProjectDiagnosticEvidence("Measurement booking", ticket.Id,
            DateTimeOffset.UtcNow, clarification) }, JsonSerializerOptions.Web);
        await db.SaveChangesAsync();
        async Task Reply(AgentCoordinationParticipant actor, CSweetAgentBase agent, AgentTestRuntime rt)
        {
            var process = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = actor.AgentInstallationId, QueuedAt = DateTimeOffset.UtcNow };
            db.Add(process); await db.SaveChangesAsync();
            var lease = await inbox.ClaimAsync(new McpAgentSession { Id = Guid.NewGuid(), RuntimeInstanceId = process.Id, TickId = Guid.NewGuid(),
                AgentInstallationId = actor.AgentInstallationId, OrganizationId = org.ToString() }, default);
            Assert.Equal(handoff.CurrentAgentWorkItemId, lease!.WorkId);
            var host = Host(agent, rt, actor);
            var configured = await host.ExecuteCapabilityAsync(AgentConfigurationCapabilities.Update, new UpdateAgentConfigurationRequest(
                new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(provider),
                    ["llmModel"] = JsonSerializer.SerializeToElement("scripted") }));
            Assert.True(configured.Succeeded, configured.Error);
            var reply = await host.DeliverEventAsync(lease.Name, lease.Payload, lease.EventId);
            Assert.True(reply.Succeeded, reply.Error);
            // A lost acknowledgement after the durable response must not start a second turn.
            var restarted = Host(actor == gabriel ? new Gabriel() : new Marcus(), rt, actor);
            Assert.True((await restarted.ExecuteCapabilityAsync(AgentConfigurationCapabilities.Update, new UpdateAgentConfigurationRequest(
                new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(provider),
                    ["llmModel"] = JsonSerializer.SerializeToElement("scripted") }))).Succeeded);
            var replay = await restarted.DeliverEventAsync(lease.Name, lease.Payload, lease.EventId);
            Assert.True(replay.Succeeded, replay.Error);
        }
        await Reply(gabriel, new Gabriel(), peer);
        await Reply(marcus, new Marcus(), runtime);
        db.ChangeTracker.Clear();
        var history = await new DecisionFollowUpService(db, turns, decisions).HistoryAsync(org, chat, card.Id, human.Id, default);
        Assert.Equal(2, history!.Count);
        Assert.Equal(2, await db.ApplicationRealtimeOutbox.CountAsync(e => e.EventType ==
            CSweet.Contracts.Communications.CommunicationEvents.DecisionFollowUpUpdated));
        Assert.Contains(clarification, Assert.Single(history, h => h.Question == schedule).Clarification);
        Assert.Equal(cause, Assert.Single(history, h => h.Question == question).Answer);
        Assert.Single(await db.CoreConversationMessages.Where(m => m.ConversationId == chat && m.CoordinationSessionId == handoff.Id &&
            m.Role == ConversationRole.Assistant).ToListAsync());
        Assert.Equal(ExecutiveDecisionStatus.Pending, (await db.ExecutiveDecisions.SingleAsync(d => d.Id == card.Id)).Status);
    }
}
