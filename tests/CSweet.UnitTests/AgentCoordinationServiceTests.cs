using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Application.WorkManagement;
using Wire = CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using DomainCoordinationSession = CSweet.Domain.Communications.AgentCoordinationSession;
using DomainCoordinationTurn = CSweet.Domain.Communications.AgentCoordinationTurn;

namespace CSweet.UnitTests;

public sealed class AgentCoordinationServiceTests
{
    [Fact]
    public async Task Coordination_retains_a_128000_character_planning_payload_with_JSON_escaping()
    {
        await using var fixture = await Fixture.CreateAsync();
        // Default JSON escaping expands these characters; the previous 256 KiB bound
        // rejected valid 128,000-character planning content during handoff.
        var payload = JsonSerializer.SerializeToElement(new string('\u2265', 127998));
        var response = await fixture.Service.RespondAsync(fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId,
            new RespondToAgentCoordinationRequest(fixture.SessionId, 1, 1, AgentCoordinationDispositions.Continue,
                "Planning output is ready for review.", "large-planning-output") {
                Artifact = new("planning.v1", "1", "exact-scope", 0, true, payload) });
        Assert.Equal(payload.GetRawText(), response.Turns.Last().Artifact!.Payload.GetRawText());
        Assert.Equal(payload.GetString(), response.Turns.Last().Artifact!.Payload.GetString());
    }

    [Theory]
    [InlineData("ready", true)]
    [InlineData("offline", false)]
    [InlineData("claimed", false)]
    [InlineData("attempt-record", false)]
    [InlineData("revoked", false)]
    [InlineData("disabled", false)]
    [InlineData("execution-timeout", false)]
    [InlineData("cancelled", false)]
    [InlineData("cooldown", false)]
    public async Task ExpiredUnclaimedCoordinationRecoversOnlyConnectedAuthorizedSpeaker(string scenario, bool recover)
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var stored = await fixture.Db.AgentCoordinationSessions.Include(x => x.Turns).SingleAsync();
        stored.Turns.Single().Content = "Accepted brief and the pending specialist questions.";
        var work = await fixture.Inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.TargetInstallationId,
            CSweet.Domain.Setup.AgentWorkKind.Event, AgentCoordinationEvents.TurnRequested,
            JsonSerializer.SerializeToElement(new { sessionId = fixture.SessionId }), "expired-unclaimed-turn", now.AddHours(1),
            correlationId: fixture.SessionId.ToString("D"), causationId: stored.Turns.Single().Id.ToString("D"),
            sourceType: "agent-coordination", sourceId: Guid.NewGuid().ToString("D"));
        work.DeadlineAt = now.AddHours(-1);
        work.Status = AgentWorkStatus.DeadLetter;
        work.AttemptCount = scenario == "claimed" ? 1 : 0;
        work.LastError = scenario == "execution-timeout" ? "The agent work deadline elapsed." : AgentWorkInbox.DeadlineElapsedBeforeClaimReason;
        stored.Status = scenario == "cancelled" ? AgentCoordinationStatus.Cancelled : AgentCoordinationStatus.Failed;
        stored.FinalSummary = "Collaboration failed because an agent turn could not continue: " + work.LastError;
        stored.UpdatedAt = scenario == "cooldown" ? now : now.AddMinutes(-5);
        stored.CurrentAgentWorkItemId = null;
        stored.CurrentOrganizationUserId = null;
        if (scenario != "revoked")
            foreach (var installationId in new[] { fixture.InitiatorInstallationId, fixture.TargetInstallationId })
                fixture.Db.AgentInstallationGrants.Add(new AgentInstallationGrant
                {
                    Id = Guid.NewGuid(), AgentInstallationId = installationId,
                    RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { CommunicationCapabilities.CoordinationRead, CommunicationCapabilities.CoordinationRespond })
                });
        if (scenario == "disabled")
            (await fixture.Db.AgentInstallations.SingleAsync(x => x.Id == fixture.TargetInstallationId)).IsEnabled = false;
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = fixture.TargetInstallationId,
            QueuedAt = now.AddMinutes(-2)
        };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, now.AddMinutes(-2));
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now.AddMinutes(-1));
        if (scenario != "offline") runtime.TransitionTo(AgentRuntimeStatus.Running, now.AddMinutes(-1));
        fixture.Db.AgentRuntimeInstances.Add(runtime);
        if (scenario == "attempt-record")
            fixture.Db.AgentWorkAttempts.Add(new AgentWorkAttempt
            {
                Id = Guid.NewGuid(), AgentWorkItemId = work.Id, RuntimeInstanceId = runtime.Id,
                Attempt = 1, ClaimedAt = now.AddHours(-2), LeaseExpiresAt = now.AddHours(-1), FinishedAt = now.AddHours(-1)
            });
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(recover ? 1 : 0, await fixture.Service.RecoverTransientFailuresAsync(now));
        Assert.Equal(0, await fixture.Service.RecoverTransientFailuresAsync(now));
        Assert.Equal(AgentWorkStatus.DeadLetter, work.Status);
        Assert.Equal("Accepted brief and the pending specialist questions.", stored.Turns.Single().Content);
        var pending = await fixture.Db.AgentWorkItems.Where(x => x.Status == AgentWorkStatus.Pending).ToListAsync();
        if (recover)
        {
            var fresh = Assert.Single(pending);
            Assert.NotEqual(work.Id, fresh.Id);
            Assert.Equal(fixture.TargetInstallationId, fresh.AgentInstallationId);
            Assert.Equal(work.CausationId, fresh.CausationId);
            Assert.True(fresh.DeadlineAt > now);
            Assert.Equal(fixture.TargetId, stored.CurrentOrganizationUserId);
            Assert.Equal(AgentCoordinationStatus.Active, stored.Status);
        }
        else Assert.Empty(pending);
    }

    [Theory]
    [InlineData("Board", false, true)]
    [InlineData("Board", true, false)]
    [InlineData("Chat", false, false)]
    public async Task BlockedRetryRequestsFreshSpecialistOnlyForMissingBoardResponse(string source, bool artifact, bool specialist)
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = await SeedBlockedBoardAsync(fixture);
        session.SourceKind = source;
        if (artifact) session.Turns.Last().ArtifactType = "decision.v1";
        await fixture.Db.SaveChangesAsync();
        var request = new ResumeAgentCoordinationRequest(session.Id, session.Revision, "Explain the issue and proposed repair.", "fresh-retry");
        var first = await fixture.Service.ResumeAsync(fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId, request);
        var replay = await fixture.Service.ResumeAsync(fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId, request);
        Assert.Equal(first.Revision, replay.Revision);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ResumeAsync(fixture.OrganizationId,
            fixture.TargetId, fixture.TargetInstallationId, request));
        Assert.Equal(specialist ? fixture.TargetId : fixture.InitiatorId, first.CurrentOrganizationUserId);
        Assert.Equal(2, first.Turns.Count);
        var pending = Assert.Single(await fixture.Db.AgentWorkItems.Where(x => x.Status == AgentWorkStatus.Pending).ToListAsync());
        var lease = await fixture.ClaimAsync(new McpAgentSession { Id = Guid.NewGuid(), RuntimeInstanceId = Guid.NewGuid(),
            TickId = Guid.NewGuid(), AgentInstallationId = pending.AgentInstallationId, OrganizationId = fixture.OrganizationId.ToString("D") }, CancellationToken.None);
        var payload = lease!.Payload.Deserialize<AgentCoordinationTurnRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains(request.Reason, payload!.Objective);
        Assert.Equal(source == "Board" ? session.SourceBoardId : null, payload.BoardSource?.BoardId);
    }

    [Fact]
    public async Task ManagerTicketRequeueResumesSpecialistAndPreservesVisibleDiagnosis()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = await SeedBlockedBoardAsync(fixture);
        var resolutions = 0;
        var engine = new WorkItemMutationEngine(fixture.Db, TimeProvider.System, coordinationFactory: () =>
        {
            resolutions++;
            return fixture.Service;
        });
        Assert.Equal(0, resolutions);
        var item = await SeedCommitmentAsync(fixture, engine, session);
        await engine.RequeueAsync(fixture.OrganizationId, new(fixture.ManagerId, null), new(item.Id, item.Revision, "human-retry"));
        Assert.Equal(WorkTaskStatus.Ready, item.Status);
        Assert.Null(item.BlockReason);
        Assert.Equal(AgentCoordinationStatus.Active, session.Status);
        Assert.True(resolutions > 0);
        Assert.Equal(fixture.TargetId, session.CurrentOrganizationUserId);
        var comments = await fixture.Db.WorkItemComments.Where(x => x.WorkItemId == item.Id).ToListAsync();
        Assert.Contains(comments, x => x.Body.Contains("Precise planning diagnostic") && x.CoordinationSessionId == session.Id);
        Assert.Contains(comments, x => x.Kind == "coordination.recovery");
        Assert.Single(await fixture.Db.AgentWorkItems.Where(x => x.Status == AgentWorkStatus.Pending).ToListAsync());
        await fixture.Service.RespondAsync(fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId,
            new(session.Id, session.Revision, session.NextTurnOrdinal, "Blocked", "Repair needs an approved dependency decision.", "new-diagnostic"));
        Assert.Contains(await fixture.Db.WorkItemComments.Where(x => x.WorkItemId == item.Id).ToListAsync(),
            x => x.Body.Contains("Repair needs an approved dependency decision.") && x.CoordinationSessionId == session.Id);
        await fixture.Service.RespondAsync(fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId,
            new(session.Id, session.Revision, session.NextTurnOrdinal, "Blocked", "Awaiting the dependency decision.", "owner-block"));
        item.Status = WorkTaskStatus.Blocked; item.BlockReason = "Awaiting the dependency decision.";
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();
        var revision = session.Revision;
        await engine.ReconcileAsync();
        Assert.Equal(WorkTaskStatus.Blocked, item.Status);
        Assert.Equal(revision, session.Revision);
    }

    [Theory]
    [InlineData("unchanged", false)]
    [InlineData("owner-update", false)]
    [InlineData("specialist-update", true)]
    [InlineData("specialist-disabled", false)]
    [InlineData("specialist-revoked", false)]
    [InlineData("other-session", false)]
    [InlineData("completed", true)]
    public async Task CommitmentAttentionRecoversOnlyRelevantCurrentChanges(string scenario, bool recover)
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = await SeedBlockedBoardAsync(fixture);
        var engine = new WorkItemMutationEngine(fixture.Db, TimeProvider.System, fixture.Service);
        var item = await SeedCommitmentAsync(fixture, engine, session);
        var target = await fixture.Db.AgentInstallations.SingleAsync(x => x.Id == fixture.TargetInstallationId);
        if (scenario.StartsWith("specialist")) target.UpdatedAt = DateTimeOffset.UtcNow;
        if (scenario == "specialist-disabled") target.IsEnabled = false;
        if (scenario == "specialist-revoked")
            (await fixture.Db.AgentInstallationGrants.SingleAsync(x => x.AgentInstallationId == target.Id)).RequiredCapabilitiesJson = "[]";
        if (scenario == "owner-update")
            (await fixture.Db.AgentInstallations.SingleAsync(x => x.Id == fixture.InitiatorInstallationId)).UpdatedAt = DateTimeOffset.UtcNow;
        if (scenario == "other-session") item.PersonalWorkContextJson = JsonSerializer.Serialize(new Wire.PersonalTodoWorkContext(
            BoardId: session.SourceBoardId, CoordinationSessionId: Guid.NewGuid()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (scenario == "completed") { session.Status = AgentCoordinationStatus.Completed; session.UpdatedAt = DateTimeOffset.UtcNow; }
        await fixture.Db.SaveChangesAsync();
        await engine.ReconcileAsync();
        await engine.ReconcileAsync();
        Assert.Equal(recover ? WorkTaskStatus.Ready : WorkTaskStatus.Blocked, item.Status);
        Assert.Equal(scenario == "specialist-update" ? 1 : 0,
            await fixture.Db.AgentWorkItems.CountAsync(x => x.Status == AgentWorkStatus.Pending));
        var comments = await fixture.Db.WorkItemComments.Where(x => x.WorkItemId == item.Id).ToListAsync();
        Assert.Equal(comments.Count, comments.Select(x => x.IdempotencyKey).Distinct().Count());
        if (scenario != "other-session") Assert.Contains(comments, x => x.Body.Contains("Precise planning diagnostic"));
    }

    private static async Task<DomainCoordinationSession> SeedBlockedBoardAsync(Fixture fixture)
    {
        var session = await fixture.Db.AgentCoordinationSessions.Include(x => x.Turns).SingleAsync();
        session.SourceKind = "Board"; session.SourceBoardId = Guid.NewGuid();
        session.Status = AgentCoordinationStatus.Blocked; session.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        session.UpdatedAt = session.CompletedAt.Value; session.CurrentOrganizationUserId = null;
        session.Revision = 2; session.NextTurnOrdinal = 2; session.IsFinalization = true;
        var blockedTurn = new DomainCoordinationTurn { Id = Guid.NewGuid(), SessionId = session.Id, EventId = Guid.NewGuid(),
            SpeakerOrganizationUserId = fixture.TargetId, Ordinal = 1, Disposition = "Blocked",
            Content = "Precise planning diagnostic: dependency cycle. Needs a corrected proposal.", IdempotencyKey = "blocked", CreatedAt = session.UpdatedAt };
        session.Turns.Add(blockedTurn);
        fixture.Db.AgentCoordinationTurns.Add(blockedTurn);
        fixture.Db.WorkBoards.Add(new() { Id = session.SourceBoardId.Value, OrganizationId = fixture.OrganizationId, Kind = WorkBoardKind.Standard });
        foreach (var installationId in new[] { fixture.InitiatorInstallationId, fixture.TargetInstallationId })
            fixture.Db.AgentInstallationGrants.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = installationId,
                RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { CommunicationCapabilities.CoordinationRead, CommunicationCapabilities.CoordinationRespond }),
                ProvidedCapabilitiesJson = "[]", EventSubscriptionsJson = "[]", NetworkAccessJson = "[]", ResourceLimitsJson = "{}", ApprovedAt = session.UpdatedAt });
        foreach (var installation in await fixture.Db.AgentInstallations.ToListAsync()) installation.UpdatedAt = session.UpdatedAt.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();
        return session;
    }

    private static async Task<WorkTask> SeedCommitmentAsync(Fixture fixture, WorkItemMutationEngine engine, DomainCoordinationSession session)
    {
        var added = await engine.AddAsync(fixture.OrganizationId, new(fixture.InitiatorId, fixture.InitiatorInstallationId),
            new("Coordinate delivery", "Reconcile specialist response", "Normal", null, "commitment")
            { WorkContext = new(BoardId: session.SourceBoardId, CoordinationSessionId: session.Id) });
        var item = await fixture.Db.CoreWorkTasks.SingleAsync(x => x.Id == added.Id);
        item.Status = WorkTaskStatus.Blocked; item.BlockReason = "Specialist proposal is invalid";
        item.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        item.BoardColumnId = await fixture.Db.WorkBoardColumns.Where(x => x.BoardId == item.BoardId &&
            x.Category == WorkBoardColumnCategory.Blocked).Select(x => x.Id).SingleAsync();
        await fixture.Db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task BoardPlanningDeliversAuthoritativeProjectContextToTarget()
    {
        await using var fixture = await Fixture.CreateAsync();
        var boardId = Guid.NewGuid(); var teamId = Guid.NewGuid(); var workstreamId = Guid.NewGuid();
        fixture.Db.WorkBoards.Add(new() { Id = boardId, OrganizationId = fixture.OrganizationId,
            TeamId = teamId, WorkstreamId = workstreamId, ManagerOrganizationUserId = fixture.InitiatorId });
        foreach (var userId in new[] { fixture.InitiatorId, fixture.TargetId })
            fixture.Db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                TeamId = teamId, OrganizationUserId = userId });
        await fixture.Db.SaveChangesAsync();
        var chat = new CommunicationChatResponse(fixture.SourceConversationId, "Planning", null,
            true, true, false, true, DateTimeOffset.UtcNow, [], null, null, 0);
        var service = new AgentCoordinationService(fixture.Db, new StubCommunicationHubService(chat), fixture.Inbox);
        var session = await service.StartBoardAsync(fixture.OrganizationId, fixture.InitiatorId,
            fixture.InitiatorInstallationId, new(fixture.TargetId, boardId, "Planning", "Plan delivery",
                ["Grounded backlog"], "Decompose accepted scope", "board-context"));
        fixture.Db.ChangeTracker.Clear();
        var lease = await fixture.ClaimAsync(new McpAgentSession
        {
            Id = Guid.NewGuid(), RuntimeInstanceId = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = fixture.TargetInstallationId, OrganizationId = fixture.OrganizationId.ToString("D")
        }, CancellationToken.None);
        Assert.NotNull(lease);
        var turn = lease.Payload.Deserialize<AgentCoordinationTurnRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(turn?.WorkContext);
        Assert.Equal(workstreamId, turn.WorkContext.WorkstreamId);
        Assert.Equal(teamId, turn.WorkContext.TeamId);
        Assert.Equal(boardId, turn.WorkContext.BoardId);
        Assert.Equal(session.Id, turn.SessionId);
    }

    [Fact]
    public async Task OutboundAgentKickoff_StartsCoordinationAndLinksSourceMessage()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var initiatorId = Guid.NewGuid();
        var initiatorInstallationId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targetInstallationId = Guid.NewGuid();
        var sourceConversationId = Guid.NewGuid();
        var coordinationConversationId = Guid.NewGuid();
        var sourceMessageId = Guid.NewGuid();
        var sourceTurnId = Guid.NewGuid();
        db.Add(new Organization
        {
            Id = organizationId, Name = "Example", Status = OrganizationStatus.Active,
            CreatedAt = now, UpdatedAt = now
        });
        db.AddRange(
            AgentUser(initiatorId, organizationId, initiatorInstallationId, "Product Manager"),
            AgentUser(targetId, organizationId, targetInstallationId, "Architect"),
            Installation(initiatorInstallationId, organizationId, now),
            Installation(targetInstallationId, organizationId, now),
            Conversation(sourceConversationId, organizationId, initiatorId, "Planning kickoff", now),
            Conversation(coordinationConversationId, organizationId, initiatorId, "Planning", now),
            new ConversationMessage
            {
                Id = sourceMessageId, Sequence = 1, ConversationId = sourceConversationId,
                Role = ConversationRole.User, Content = "Start governed architecture planning.",
                SenderOrganizationUserId = initiatorId, ChatTurnId = sourceTurnId,
                CorrelationId = Guid.NewGuid(), CreatedAt = now
            },
            new ChatTurn
            {
                Id = sourceTurnId, OrganizationId = organizationId,
                ConversationId = sourceConversationId, UserMessageId = sourceMessageId,
                TargetAgentOrganizationUserId = targetId, Status = ChatTurnStatus.Queued,
                CreatedAt = now, UpdatedAt = now
            });
        await db.SaveChangesAsync();
        var chat = new CommunicationChatResponse(
            coordinationConversationId, "Planning", null, true, true, false, true, now,
            [
                new CommunicationParticipantResponse(initiatorId, "Product Manager", "Agent", "Software Product Manager"),
                new CommunicationParticipantResponse(targetId, "Architect", "Agent", "Software Architect")
            ], null, null, 0);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var service = new AgentCoordinationService(db, new StubCommunicationHubService(chat), inbox);

        var session = await service.StartAsync(
            organizationId, initiatorId, initiatorInstallationId,
            new StartAgentCoordinationRequest(
                targetId, "Release planning", "Produce an approved design.", ["Design is traceable."],
                "Begin with the technical design.", sourceConversationId, sourceTurnId,
                sourceMessageId, "pm-architect-planning"));

        Assert.Equal(targetId, session.CurrentOrganizationUserId);
        Assert.Equal(session.Id, (await db.CoreConversationMessages.SingleAsync(x =>
            x.Id == sourceMessageId)).CoordinationSessionId);
        Assert.Equal(ChatTurnStatus.Completed, (await db.ChatTurns.SingleAsync(x =>
            x.Id == sourceTurnId)).Status);
        Assert.Single(await db.AgentWorkItems.Where(x =>
            x.AgentInstallationId == targetInstallationId &&
            x.CorrelationId == session.Id.ToString("D")).ToListAsync());
    }

    [Fact]
    public async Task StructuredArtifact_IsPersistedWithPlatformDigestAndReturnedOnReplay()
    {
        await using var fixture = await Fixture.CreateAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            planKey = "team-1",
            epicKey = "EPIC-01",
            stories = new[] { new { key = "EPIC-01-STORY-01" } }
        });
        var request = new RespondToAgentCoordinationRequest(
            fixture.SessionId, 1, 1, AgentCoordinationDispositions.Continue,
            "Story proposal attached.", "artifact-turn-1",
            new AgentCoordinationArtifactSubmission(
                "software-architecture.story-proposal.v1", "1.0",
                "team-1:EPIC-01:stories", 0, true, payload));

        var first = await fixture.Service.RespondAsync(
            fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId, request);
        var replay = await fixture.Service.RespondAsync(
            fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId, request);

        var artifact = first.Turns.Single(x => x.Ordinal == 1).Artifact;
        Assert.NotNull(artifact);
        Assert.Equal("software-architecture.story-proposal.v1", artifact.Type);
        Assert.Equal("team-1:EPIC-01:stories", artifact.Key);
        Assert.Equal(64, artifact.Digest.Length);
        Assert.Equal(artifact.Digest, replay.Turns.Single(x => x.Ordinal == 1).Artifact?.Digest);
        var stored = await fixture.Db.AgentCoordinationTurns.SingleAsync(x =>
            x.SessionId == fixture.SessionId && x.Ordinal == 1);
        Assert.Equal(artifact.Digest, stored.ArtifactDigest);
    }

    [Fact]
    public async Task MultiTurnSequence_AlternatesAndFinalizesOnceInTheSourceChat()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service;

        var targetContinue = await service.RespondAsync(
            fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 1, 1, AgentCoordinationDispositions.Continue,
                "Please provide dependency order and quality constraints.", "target-continue"));
        Assert.Equal(2, targetContinue.Revision);
        Assert.Equal(fixture.InitiatorId, targetContinue.CurrentOrganizationUserId);

        var replay = await service.RespondAsync(
            fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 1, 1, AgentCoordinationDispositions.Continue,
                "Please provide dependency order and quality constraints.", "target-continue"));
        Assert.Equal(2, replay.Revision);
        Assert.Equal(2, replay.Turns.Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RespondAsync(
            fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 1, 1, AgentCoordinationDispositions.Continue,
                "Stale response", "stale-response")));

        var architectContinue = await service.RespondAsync(
            fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 2, 2, AgentCoordinationDispositions.Continue,
                "Implement the API contract first, then persistence, with rollback and fault tests.",
                "architect-continue"));
        Assert.Equal(fixture.TargetId, architectContinue.CurrentOrganizationUserId);

        var productComplete = await service.RespondAsync(
            fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 3, 3, AgentCoordinationDispositions.Completed,
                "The board is reconciled with decision-ready tickets.", "target-completed"));
        Assert.Equal(AgentCoordinationStatuses.Summarizing, productComplete.Status);
        Assert.True(productComplete.IsFinalization);
        Assert.Equal(fixture.InitiatorId, productComplete.CurrentOrganizationUserId);

        var finalized = await service.RespondAsync(
            fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 4, 4, AgentCoordinationDispositions.Completed,
                "Completed: the board plan is decision-ready and all existing gates remain in place.",
                "architect-summary"));
        Assert.Equal(AgentCoordinationStatuses.Completed, finalized.Status);
        Assert.Null(finalized.CurrentOrganizationUserId);
        Assert.Equal(5, finalized.Turns.Count);
        var sourceSummaries = await fixture.Db.CoreConversationMessages
            .Where(x => x.ConversationId == fixture.SourceConversationId &&
                        x.CoordinationSessionId == fixture.SessionId)
            .ToListAsync();
        Assert.Single(sourceSummaries);
        Assert.Contains("decision-ready", sourceSummaries[0].Content,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Finalization_DoesNotDuplicateSummaryWhenSourceAndSessionUseSameChat()
    {
        await using var fixture = await Fixture.CreateAsync(useSourceConversationForSession: true);
        var completed = await fixture.Service.RespondAsync(
            fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 1, 1, AgentCoordinationDispositions.Completed,
                "The backlog is published.", "target-complete"));
        var finalized = await fixture.Service.RespondAsync(
            fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 2, 2, AgentCoordinationDispositions.Completed,
                "Planning completed.", "initiator-final"));

        Assert.Equal(AgentCoordinationStatuses.Completed, finalized.Status);
        Assert.Equal(2, await fixture.Db.CoreConversationMessages.CountAsync(x =>
            x.ConversationId == fixture.SourceConversationId &&
            x.CoordinationSessionId == fixture.SessionId));
    }

    [Fact]
    public async Task Cancellation_CancelsPendingWorkRejectsLateRepliesAndPreservesTranscript()
    {
        await using var fixture = await Fixture.CreateAsync();
        var cancelled = await fixture.Service.CancelAsync(
            fixture.OrganizationId, fixture.ManagerId, true,
            new CancelAgentCoordinationRequest(
                fixture.SessionId, 1, "The owner stopped this task.", "cancel-1"));

        Assert.Equal(AgentCoordinationStatuses.Cancelled, cancelled.Status);
        var replay = await fixture.Service.CancelAsync(
            fixture.OrganizationId, fixture.ManagerId, true,
            new CancelAgentCoordinationRequest(
                fixture.SessionId, 1, "The owner stopped this task.", "cancel-1"));
        Assert.Equal(cancelled.Revision, replay.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RespondAsync(
            fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 2, 1, AgentCoordinationDispositions.Continue,
                "Late response", "late-response")));
        Assert.Single(cancelled.Turns);
        Assert.Single(await fixture.Db.CoreConversationMessages.Where(x =>
            x.ConversationId == fixture.SourceConversationId &&
            x.CoordinationSessionId == fixture.SessionId).ToListAsync());
    }

    [Fact]
    public async Task DeadLetteredTurn_NotifiesSourceChatManagerWithoutImpersonatingInitiator()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.ConversationParticipants.Add(new ConversationParticipant
        {
            Id = Guid.NewGuid(), ConversationId = fixture.SourceConversationId,
            OrganizationUserId = fixture.ManagerId, Role = ConversationParticipantRole.Member,
            JoinedAt = DateTimeOffset.UtcNow
        });
        await fixture.Db.SaveChangesAsync();
        var work = await fixture.Inbox.EnqueueAsync(
            fixture.OrganizationId.ToString("D"),
            fixture.TargetInstallationId,
            CSweet.Domain.Setup.AgentWorkKind.Event,
            AgentCoordinationEvents.TurnRequested,
            JsonSerializer.SerializeToElement(new { sessionId = fixture.SessionId }),
            "failed-coordination-turn",
            DateTimeOffset.UtcNow.AddMinutes(5),
            correlationId: fixture.SessionId.ToString("D"),
            sourceType: "agent-coordination",
            sourceId: Guid.NewGuid().ToString("D"),
            maximumAttempts: 1);
        var runtimeSession = new McpAgentSession
        {
            Id = Guid.NewGuid(), RuntimeInstanceId = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = fixture.TargetInstallationId,
            OrganizationId = fixture.OrganizationId.ToString("D")
        };
        var lease = await fixture.ClaimAsync(runtimeSession, CancellationToken.None);
        Assert.NotNull(lease);
        await fixture.Inbox.FailAsync(runtimeSession, work.Id, lease!.Attempt, lease.LeaseToken,
            "runtime failure", CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var session = await fixture.Db.AgentCoordinationSessions.SingleAsync(x =>
            x.Id == fixture.SessionId);
        Assert.Equal(AgentCoordinationStatus.Failed, session.Status);
        Assert.Contains("runtime failure", session.FinalSummary,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Db.CoreConversationMessages.Where(x =>
            x.ConversationId == fixture.SourceConversationId &&
            x.CoordinationSessionId == fixture.SessionId).ToListAsync());
        var notification = Assert.Single(await fixture.Db.UserNotifications.ToListAsync());
        Assert.Equal(fixture.ManagerId, notification.RecipientOrganizationUserId);
        Assert.Equal("CoordinationFailure", notification.Category);
        Assert.Equal($"/organizations/{fixture.OrganizationId:D}/communications/{fixture.SourceConversationId:D}",
            notification.ActionUri);
        Assert.DoesNotContain("runtime failure", notification.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitiatorCanListAndIdempotentlyResumeItsFailedSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        var failedDelivery = await fixture.Inbox.EnqueueAsync(
            fixture.OrganizationId.ToString("D"),
            fixture.InitiatorInstallationId,
            CSweet.Domain.Setup.AgentWorkKind.Event,
            AgentCoordinationEvents.TurnRequested,
            JsonSerializer.SerializeToElement(new { sessionId = fixture.SessionId, revision = 2 }),
            $"coordination:{fixture.SessionId:N}:turn:1",
            DateTimeOffset.UtcNow.AddMinutes(5),
            correlationId: fixture.SessionId.ToString("D"),
            sourceType: "agent-coordination",
            sourceId: Guid.NewGuid().ToString("D"));
        failedDelivery.Status = AgentWorkStatus.DeadLetter;
        var stored = await fixture.Db.AgentCoordinationSessions.SingleAsync(x =>
            x.Id == fixture.SessionId);
        stored.Status = AgentCoordinationStatus.Failed;
        stored.CurrentOrganizationUserId = null;
        stored.CompletedAt = stored.UpdatedAt = DateTimeOffset.UtcNow;
        stored.FinalSummary = "A runtime transport failed.";
        stored.Revision = 2;
        await fixture.Db.SaveChangesAsync();

        var visible = await fixture.Service.ListAsync(
            fixture.OrganizationId, fixture.InitiatorId, null, activeOnly: false);
        Assert.Single(visible);
        Assert.Empty(await fixture.Service.ListAsync(
            fixture.OrganizationId, fixture.ManagerId, null, activeOnly: false));

        var request = new ResumeAgentCoordinationRequest(
            fixture.SessionId, 2, "Retry the failed runtime turn.", "resume-session-1");
        var resumed = await fixture.Service.ResumeAsync(
            fixture.OrganizationId, fixture.InitiatorId,
            fixture.InitiatorInstallationId, request);
        var replay = await fixture.Service.ResumeAsync(
            fixture.OrganizationId, fixture.InitiatorId,
            fixture.InitiatorInstallationId, request);

        Assert.Equal(AgentCoordinationStatuses.Active, resumed.Status);
        Assert.Equal(fixture.InitiatorId, resumed.CurrentOrganizationUserId);
        Assert.Equal(resumed.Revision, replay.Revision);
        Assert.Equal(3, resumed.Revision);
        Assert.Single(await fixture.Db.AgentWorkItems.Where(x =>
            x.CorrelationId == fixture.SessionId.ToString("D") &&
            x.Status == AgentWorkStatus.Pending).ToListAsync());
    }

    [Fact]
    public async Task TechnicalSupportSession_RejectsContinuationAtItsTurnLimit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var stored = await fixture.Db.AgentCoordinationSessions.SingleAsync(x =>
            x.Id == fixture.SessionId);
        stored.SourceKind = "WorkItem";
        stored.MaximumTurns = 2;
        await fixture.Db.SaveChangesAsync();

        var first = await fixture.Service.RespondAsync(
            fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 1, 1, AgentCoordinationDispositions.Continue,
                "Inspect the failed invariant before retrying.", "support-turn-1"));

        Assert.Equal(2, first.NextTurnOrdinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RespondAsync(
            fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId,
            new RespondToAgentCoordinationRequest(
                fixture.SessionId, 2, 2, AgentCoordinationDispositions.Continue,
                "Continue investigating without a terminal outcome.", "support-turn-2")));
    }

    [Fact]
    public async Task ProfileRegisteredArtifact_RejectsInvalidPayloadBeforePersistingTurn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var workstreamId = Guid.NewGuid();
        fixture.Db.Workstreams.Add(new Workstream
        {
            Id = workstreamId, OrganizationId = fixture.OrganizationId,
            ProfileKey = "campaign.v1", ProfileVersion = 1, ProfileDefinitionDigest = "pinned"
        });
        fixture.Db.WorkstreamProfileDefinitions.Add(new WorkstreamProfileDefinitionRecord
        {
            Id = Guid.NewGuid(), Key = "campaign.v1", Version = 1, DefinitionDigest = "pinned",
            DefinitionJson = """
                {"artifactTypes":[{"key":"publisher.brief.v1","displayName":"Brief","schemaVersion":"1.0",
                "payloadSchema":{"type":"object","required":["Audience"],"properties":{"Audience":{"type":"string"}}}}]}
                """
        });
        var session = await fixture.Db.AgentCoordinationSessions.SingleAsync(x => x.Id == fixture.SessionId);
        session.WorkstreamId = workstreamId;
        await fixture.Db.SaveChangesAsync();
        var request = new RespondToAgentCoordinationRequest(
            fixture.SessionId, 1, 1, AgentCoordinationDispositions.Continue, "Brief attached.", "invalid-brief",
            new AgentCoordinationArtifactSubmission("publisher.brief.v1", "1.0", "brief", 0, true,
                JsonSerializer.SerializeToElement(new { Audience = 42 })));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RespondAsync(
            fixture.OrganizationId, fixture.TargetId, fixture.TargetInstallationId, request));
        Assert.False(await fixture.Db.AgentCoordinationTurns.AnyAsync(x => x.SessionId == fixture.SessionId && x.Ordinal == 1));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedDocumentSharingAtBoardAndWorkStartsGrantsOnlyRead(bool workSource)
    {
        await using var fixture = await Fixture.CreateAsync();
        var db = fixture.Db;
        var boardId = Guid.NewGuid(); var teamId = Guid.NewGuid();
        var documentId = Guid.NewGuid(); var revisionId = Guid.NewGuid();
        var itemId = Guid.NewGuid(); var sprintId = Guid.NewGuid(); var stageId = Guid.NewGuid();
        db.WorkBoards.Add(new() { Id = boardId, OrganizationId = fixture.OrganizationId,
            TeamId = teamId, ManagerOrganizationUserId = fixture.InitiatorId });
        foreach (var userId in new[] { fixture.InitiatorId, fixture.TargetId })
            db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                TeamId = teamId, OrganizationUserId = userId });
        db.CoreArtifacts.Add(new() { Id = documentId, OrganizationId = fixture.OrganizationId,
            CreatedByOrganizationUserId = fixture.InitiatorId });
        db.ArtifactRevisions.Add(new() { Id = revisionId, ArtifactId = documentId,
            OrganizationId = fixture.OrganizationId, ContentSha256 = "exact" });
        db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = fixture.InitiatorInstallationId,
            ScopeKind = GrantScopeKind.Artifact, ScopeId = documentId, Action = CSweet.Contracts.Core.ArtifactActions.Read });
        if (workSource)
        {
            foreach (var installationId in new[] { fixture.InitiatorInstallationId, fixture.TargetInstallationId })
                db.AgentInstallationGrants.Add(new() { AgentInstallationId = installationId,
                    RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] {
                        CommunicationCapabilities.CoordinationRead, CommunicationCapabilities.CoordinationRespond }) });
            var initiator = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.InitiatorId);
            var target = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.TargetId);
            initiator.Role = new() { Id = Guid.NewGuid(), Name = "Developer" };
            target.Role = new() { Id = Guid.NewGuid(), Name = "Architect" };
            db.CoreRoles.AddRange(initiator.Role, target.Role);
            var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = itemId,
                SprintExecutionId = sprintId,
                WorkItem = new() { Id = itemId, OrganizationId = fixture.OrganizationId, AssignmentRevision = 1 },
                SprintExecution = new() { Id = sprintId, OrganizationId = fixture.OrganizationId, BoardId = boardId } };
            db.WorkStageExecutions.Add(new() { Id = stageId, ItemExecutionId = execution.Id, ItemExecution = execution,
                Status = WorkStageExecutionStatus.Running, AgentInstallationId = fixture.InitiatorInstallationId });
        }
        await db.SaveChangesAsync();
        var chat = new CommunicationChatResponse(fixture.SourceConversationId, "Documentation", null,
            true, true, false, true, DateTimeOffset.UtcNow, [], null, null, 0);
        var service = new AgentCoordinationService(db, new StubCommunicationHubService(chat), fixture.Inbox);
        var artifact = CollaborationActions.ShareDocuments("sources", [new(documentId, revisionId, "exact")]);
        var session = workSource
            ? await service.StartWorkAsync(fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId,
                new(fixture.TargetId, boardId, itemId, sprintId, stageId, 1, "Documentation", "Review source", ["Source read"], "Read this source", "work-source", artifact))
            : await service.StartBoardAsync(fixture.OrganizationId, fixture.InitiatorId, fixture.InitiatorInstallationId,
                new(fixture.TargetId, boardId, "Documentation", "Review source", ["Source read"], "Read this source", "board-source", artifact));
        Assert.Equal(CollaborationActions.DocumentShareType, session.Turns[0].Artifact!.Type);
        var grant = Assert.Single(await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.TargetInstallationId).ToArrayAsync());
        Assert.Equal(CSweet.Contracts.Core.ArtifactActions.Read, grant.Action);
        Assert.Equal(documentId, grant.ScopeId);
        var queuedId = await db.AgentCoordinationSessions.Where(s => s.Id == session.Id).Select(s => s.CurrentAgentWorkItemId).SingleAsync();
        var queuedWork = await db.AgentWorkItems.SingleAsync(x => x.Id == queuedId);
        if (workSource)
        {
            using var inputs = JsonDocument.Parse(Assert.IsType<string>(queuedWork.NativeWorkInputReceiptJson));
            Assert.Equal("input-origins-only-v1", inputs.RootElement.GetProperty("coverage").GetString());
            Assert.Equal(itemId, inputs.RootElement.GetProperty("origins").GetProperty("caseId").GetGuid());
            Assert.Equal(session.Id, inputs.RootElement.GetProperty("origins").GetProperty("sessionId").GetGuid());
        }
        else Assert.Null(queuedWork.NativeWorkInputReceiptJson);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        // A real inbox claim always belongs to a persisted runtime. Resource evidence
        // now reads that identity even when this fixture does not configure memory policy.
        public async Task<ClaimedAgentWork?> ClaimAsync(McpAgentSession session, CancellationToken token)
        {
            Db.Add(new AgentRuntimeInstance { Id = session.RuntimeInstanceId, AgentInstallationId = session.AgentInstallationId,
                QueuedAt = DateTimeOffset.UtcNow });
            await Db.SaveChangesAsync(token);
            return await Inbox.ClaimAsync(session, token);
        }
        public required CSweetDbContext Db { get; init; }
        public required AgentCoordinationService Service { get; init; }
        public required AgentWorkInbox Inbox { get; init; }
        public Guid OrganizationId { get; init; }
        public Guid InitiatorId { get; init; }
        public Guid TargetId { get; init; }
        public Guid ManagerId { get; init; }
        public Guid InitiatorInstallationId { get; init; }
        public Guid TargetInstallationId { get; init; }
        public Guid SessionId { get; init; }
        public Guid SourceConversationId { get; init; }

        public static async Task<Fixture> CreateAsync(bool useSourceConversationForSession = false)
        {
            var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            var now = DateTimeOffset.UtcNow;
            var organizationId = Guid.NewGuid();
            var initiatorId = Guid.NewGuid();
            var targetId = Guid.NewGuid();
            var managerId = Guid.NewGuid();
            var initiatorInstallationId = Guid.NewGuid();
            var targetInstallationId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var sourceConversationId = Guid.NewGuid();
            var collaborationConversationId = useSourceConversationForSession
                ? sourceConversationId
                : Guid.NewGuid();
            var sourceTurnId = Guid.NewGuid();
            var sourceMessageId = Guid.NewGuid();

            db.Add(new Organization
            {
                Id = organizationId, Name = "Example", Status = OrganizationStatus.Active,
                CreatedAt = now, UpdatedAt = now
            });
            db.AddRange(
                AgentUser(initiatorId, organizationId, initiatorInstallationId, "Architect"),
                AgentUser(targetId, organizationId, targetInstallationId, "Product Manager"),
                new OrganizationUser
                {
                    Id = managerId, OrganizationId = organizationId, DisplayName = "Owner",
                    EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner,
                    IsActive = true, CreatedAt = now
                },
                Installation(initiatorInstallationId, organizationId, now),
                Installation(targetInstallationId, organizationId, now),
                new Conversation
                {
                    Id = collaborationConversationId, OrganizationId = organizationId,
                    Title = "Agent collaboration", Kind = ConversationKind.AgentChannel,
                    InitiatedByOrganizationUserId = initiatorId, IsPrivate = true,
                    CreatedAt = now, UpdatedAt = now
                });
            if (sourceConversationId != collaborationConversationId)
                db.Add(
                new Conversation
                {
                    Id = sourceConversationId, OrganizationId = organizationId,
                    Title = "CEO and Architect", Kind = ConversationKind.DirectHumanAgent,
                    InitiatedByOrganizationUserId = managerId, IsPrivate = true,
                    CreatedAt = now, UpdatedAt = now
                });
            db.AgentCoordinationSessions.Add(new DomainCoordinationSession
            {
                Id = sessionId, OrganizationId = organizationId,
                ConversationId = collaborationConversationId,
                SourceConversationId = sourceConversationId,
                SourceChatTurnId = sourceTurnId, SourceMessageId = sourceMessageId,
                InitiatorOrganizationUserId = initiatorId,
                InitiatorInstallationId = initiatorInstallationId,
                TargetOrganizationUserId = targetId,
                TargetInstallationId = targetInstallationId,
                CurrentOrganizationUserId = targetId,
                Subject = "Populate the kanban board",
                Objective = "Collaborate on a decision-ready delivery plan.",
                SuccessCriteriaJson = JsonSerializer.Serialize(new[] { "Board plan is decision-ready." }),
                Status = AgentCoordinationStatus.Active,
                Revision = 1, NextTurnOrdinal = 1,
                IdempotencyKey = "session-1", CreatedAt = now, UpdatedAt = now,
                Turns =
                [
                    new DomainCoordinationTurn
                    {
                        Id = Guid.NewGuid(), SessionId = sessionId, EventId = Guid.NewGuid(),
                        SpeakerOrganizationUserId = initiatorId, Ordinal = 0,
                        Disposition = AgentCoordinationDispositions.Continue,
                        Content = "Please collaborate on populating the kanban board.",
                        IdempotencyKey = "initial", CreatedAt = now
                    }
                ]
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
            return new Fixture
            {
                Db = db,
                Service = new AgentCoordinationService(db, null!, inbox),
                Inbox = inbox,
                OrganizationId = organizationId,
                InitiatorId = initiatorId,
                TargetId = targetId,
                ManagerId = managerId,
                InitiatorInstallationId = initiatorInstallationId,
                TargetInstallationId = targetInstallationId,
                SessionId = sessionId,
                SourceConversationId = sourceConversationId
            };
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static OrganizationUser AgentUser(
            Guid id, Guid organizationId, Guid installationId, string name) => new()
        {
            Id = id, OrganizationId = organizationId, DisplayName = name,
            EmployeeType = EmployeeType.Agent, PermissionLevel = OrganizationPermissionLevel.Contributor,
            AgentInstallationId = installationId, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };

        private static AgentInstallation Installation(
            Guid id, Guid organizationId, DateTimeOffset now) => new()
        {
            Id = id, InstallationKey = Guid.NewGuid(), PackageVersionId = Guid.NewGuid(),
            BusinessId = organizationId.ToString("D"), IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active, CreatedAt = now, UpdatedAt = now
        };
    }

    [Theory]
    [InlineData("ready", true)]
    [InlineData("stopping", false)]
    [InlineData("no-replacement", true)]
    [InlineData("replacement-reset", false)]
    [InlineData("replacement-legacy", false)]
    [InlineData("erased", false)]
    [InlineData("exhausted", false)]
    [InlineData("no-attempt", false)]
    [InlineData("revoked", false)]
    public async Task MemoryRecoveryRequiresTerminatedContaminatedRuntimeAndPreservesQuestions(string scenario, bool recover)
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var stored = await fixture.Db.AgentCoordinationSessions.Include(x => x.Turns).SingleAsync();
        stored.Turns.Single().Content = "Gabriel's five scope questions and exact production brief.";
        var work = await fixture.Inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.InitiatorInstallationId,
            CSweet.Domain.Setup.AgentWorkKind.Event, AgentCoordinationEvents.TurnRequested,
            JsonSerializer.SerializeToElement(new { sessionId = fixture.SessionId }), "memory-failed-director", now.AddHours(1),
            correlationId: fixture.SessionId.ToString("D"), causationId: stored.Turns.Single().Id.ToString("D"),
            sourceType: "agent-coordination", sourceId: Guid.NewGuid().ToString("D"));
        work.Status = AgentWorkStatus.DeadLetter;
        work.AttemptCount = scenario == "exhausted" ? 3 : 1;
        work.LastError = CSweet.Infrastructure.Core.MemoryRuntimeResetRequiredException.SafeMessage;
        stored.Status = AgentCoordinationStatus.Failed;
        stored.FinalSummary = work.LastError;
        stored.UpdatedAt = now.AddMinutes(-10);
        stored.CurrentAgentWorkItemId = null;
        stored.CurrentOrganizationUserId = null;
        if (scenario != "revoked")
            foreach (var installation in new[] { fixture.InitiatorInstallationId, fixture.TargetInstallationId })
                fixture.Db.AgentInstallationGrants.Add(new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = installation,
                    RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { CommunicationCapabilities.CoordinationRead, CommunicationCapabilities.CoordinationRespond }),
                    GrantRevision = 1 });
        var old = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = fixture.InitiatorInstallationId, QueuedAt = now.AddMinutes(-15),
            MemoryResetRequestedAt = now.AddMinutes(-12),
            MemoryResetReasonCode = scenario == "erased" ? "memory.source_erased" : "memory.retained_evidence_invalid" };
        old.TransitionTo(AgentRuntimeStatus.Starting, now.AddMinutes(-15));
        old.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now.AddMinutes(-14));
        old.TransitionTo(AgentRuntimeStatus.Running, now.AddMinutes(-14));
        if (scenario != "stopping")
        {
            old.TransitionTo(AgentRuntimeStatus.Cancelled, now.AddMinutes(-11));
            old.MemoryResetCompletedAt = now.AddMinutes(-11);
        }
        fixture.Db.AgentRuntimeInstances.Add(old);
        if (scenario != "no-attempt")
            fixture.Db.AgentWorkAttempts.Add(new() { Id = Guid.NewGuid(), AgentWorkItemId = work.Id,
                RuntimeInstanceId = old.Id, Attempt = 1, Error = "memory.runtime_reset", FinishedAt = now.AddMinutes(-12) });
        if (scenario != "no-replacement")
        {
            var replacement = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
                AgentInstallationId = fixture.InitiatorInstallationId, QueuedAt = now.AddMinutes(-9),
                MemoryResetRequestedAt = scenario == "replacement-reset" ? now : null,
                MemoryReadEvidenceVersion = scenario == "replacement-legacy" ? 2 : AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion };
            replacement.TransitionTo(AgentRuntimeStatus.Starting, now.AddMinutes(-9));
            replacement.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now.AddMinutes(-8));
            replacement.TransitionTo(AgentRuntimeStatus.Running, now.AddMinutes(-8));
            fixture.Db.AgentRuntimeInstances.Add(replacement);
        }
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(recover ? 1 : 0, await fixture.Service.RecoverTransientFailuresAsync(now));
        Assert.Equal(0, await fixture.Service.RecoverTransientFailuresAsync(now));
        Assert.Equal("Gabriel's five scope questions and exact production brief.", Assert.Single(stored.Turns).Content);
        var pending = await fixture.Db.AgentWorkItems.Where(x => x.Status == AgentWorkStatus.Pending).ToListAsync();
        if (recover)
        {
            Assert.Equal(fixture.InitiatorInstallationId, Assert.Single(pending).AgentInstallationId);
            Assert.Equal(fixture.InitiatorId, stored.CurrentOrganizationUserId);
            Assert.Equal(AgentCoordinationStatus.Active, stored.Status);
            Assert.Equal(work.CausationId, pending[0].CausationId);
        }
        else Assert.Empty(pending);
    }

    [Theory]
    [InlineData("ready", true)]
    [InlineData("disabled", false)]
    [InlineData("no-evidence", false)]
    [InlineData("other-capability", false)]
    [InlineData("backoff", false)]
    [InlineData("exhausted", false)]
    [InlineData("provider-interrupted", true)]
    [InlineData("provider-exhausted", false)]
    [InlineData("provider-permanent", false)]
    [InlineData("unrelated-model-run", false)]
    public async Task RecoveryReassessesUnavailableInferenceWithoutOverridingOtherBlockers(string scenario, bool recover)
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var stored = await fixture.Db.AgentCoordinationSessions.Include(x => x.Turns).SingleAsync();
        var capability = scenario == "other-capability" ? "platform.artifact-package.read.v1" : "platform.llm.chat-stream.v1";
        var providerFailure = scenario.StartsWith("provider-", StringComparison.Ordinal);
        var code = providerFailure ? "llm.provider_unavailable" : "platform.capability.unavailable";
        var retryable = providerFailure && scenario != "provider-permanent" ? "true" : "false";
        var error = $"agent-failure:v1;code={code};retryable={retryable};capability={capability};diagnosticId=test";
        var work = await fixture.Inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.TargetInstallationId,
            CSweet.Domain.Setup.AgentWorkKind.Event, AgentCoordinationEvents.TurnRequested, JsonSerializer.SerializeToElement(new {}),
            "unavailable-inference", now.AddHours(1), correlationId: fixture.SessionId.ToString("D"),
            causationId: stored.Turns.Single().Id.ToString("D"), sourceType: "agent-coordination",
            sourceId: Guid.NewGuid().ToString("D"));
        work.Status = AgentWorkStatus.DeadLetter;
        work.AttemptCount = scenario == "exhausted" ? 12 : scenario == "backoff" ? 4 : scenario == "provider-exhausted" ? 3 : 1;
        work.LastError = error;
        stored.Status = AgentCoordinationStatus.Failed;
        stored.FinalSummary = error;
        stored.UpdatedAt = now.AddMinutes(-2);
        stored.CurrentAgentWorkItemId = null;
        stored.CurrentOrganizationUserId = null;
        var provider = Guid.NewGuid();
        fixture.Db.LlmProviderProfiles.Add(new() { Id = provider, IsEnabled = scenario != "disabled" });
        if (scenario != "no-evidence")
            fixture.Db.AgentRunLogs.Add(new() { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
                AgentInstallationId = fixture.TargetInstallationId, ProviderProfileId = provider,
                AgentWorkItemId = scenario == "unrelated-model-run" ? Guid.NewGuid() : work.Id,
                StartedAt = work.CreatedAt, Status = "Failed" });
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(recover ? 1 : 0, await fixture.Service.RecoverTransientFailuresAsync(now));
        Assert.Equal(0, await fixture.Service.RecoverTransientFailuresAsync(now));
        Assert.Single(stored.Turns);
        var pending = await fixture.Db.AgentWorkItems.Where(x => x.Status == AgentWorkStatus.Pending).ToListAsync();
        if (recover)
        {
            Assert.Equal(fixture.TargetInstallationId, Assert.Single(pending).AgentInstallationId);
            Assert.Equal(AgentCoordinationStatus.Active, stored.Status);
        }
        else Assert.Empty(pending);
    }

    [Theory]
    [InlineData("runtime.rate_limited", true, 3, 120, true)]
    [InlineData("runtime.transport", true, 3, 120, true)]
    [InlineData("runtime.transport", false, 3, 120, false)]
    [InlineData("runtime.transport", true, 12, 120, false)]
    [InlineData("runtime.transport", true, 3, 10, false)]
    [InlineData("capability.denied", true, 3, 120, false)]
    public async Task TransientRecovery_RetriesThePendingProducerWithCooldownAndBoundedAttempts(
        string code, bool retryable, int attempts, int ageSeconds, bool recover)
    {
        await using var fixture = await Fixture.CreateAsync();
        var stored = await fixture.Db.AgentCoordinationSessions.Include(x => x.Turns).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var error = $"agent-failure:v1;code={code};retryable={retryable.ToString().ToLowerInvariant()};diagnosticId=test";
        var work = await fixture.Inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.TargetInstallationId,
            CSweet.Domain.Setup.AgentWorkKind.Event, AgentCoordinationEvents.TurnRequested,
            JsonSerializer.SerializeToElement(new { sessionId = fixture.SessionId }), "failed-producer",
            now.AddHours(1), correlationId: fixture.SessionId.ToString("D"),
            causationId: stored.Turns.Single().Id.ToString("D"), sourceType: "agent-coordination", sourceId: Guid.NewGuid().ToString("D"));
        work.Status = AgentWorkStatus.DeadLetter;
        work.AttemptCount = attempts;
        work.LastError = error;
        stored.Status = AgentCoordinationStatus.Failed;
        stored.CurrentOrganizationUserId = null;
        stored.CurrentAgentWorkItemId = null;
        stored.Revision = 2;
        stored.FinalSummary = error;
        stored.UpdatedAt = now.AddSeconds(-ageSeconds);
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(recover ? 1 : 0, await fixture.Service.RecoverTransientFailuresAsync(now));
        Assert.Equal(0, await fixture.Service.RecoverTransientFailuresAsync(now));
        Assert.Single(stored.Turns);
        var pending = await fixture.Db.AgentWorkItems.Where(x => x.Status == AgentWorkStatus.Pending).ToListAsync();
        if (recover)
        {
            Assert.Equal(fixture.TargetId, stored.CurrentOrganizationUserId);
            Assert.Equal(fixture.TargetInstallationId, Assert.Single(pending).AgentInstallationId);
            Assert.Equal(3, stored.Revision);
            Assert.Equal(AgentWorkStatus.DeadLetter, work.Status);
        }
        else Assert.Empty(pending);
    }
    [Fact]
    public async Task HumanManagerRetryPreservesSessionAndRetriesFailedSpeakerIdempotently()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var installation in new[] { fixture.InitiatorInstallationId, fixture.TargetInstallationId })
            fixture.Db.AgentInstallationGrants.Add(new AgentInstallationGrant { Id = Guid.NewGuid(), AgentInstallationId = installation,
                RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { CommunicationCapabilities.CoordinationRead, CommunicationCapabilities.CoordinationRespond }),
                ProvidedCapabilitiesJson = "[]", EventSubscriptionsJson = "[]", NetworkAccessJson = "[]", ResourceLimitsJson = "{}", ApprovedAt = DateTimeOffset.UtcNow });
        var work = await fixture.Inbox.EnqueueAsync(fixture.OrganizationId.ToString("D"), fixture.TargetInstallationId,
            CSweet.Domain.Setup.AgentWorkKind.Event, AgentCoordinationEvents.TurnRequested, JsonSerializer.SerializeToElement(new { }),
            "failed-model", DateTimeOffset.UtcNow.AddHours(1), correlationId: fixture.SessionId.ToString("D"),
            sourceType: "agent-coordination", sourceId: Guid.NewGuid().ToString("D"));
        work.Status = AgentWorkStatus.DeadLetter;
        var session = await fixture.Db.AgentCoordinationSessions.SingleAsync();
        session.Status = AgentCoordinationStatus.Failed; session.Revision = 2;
        session.CurrentOrganizationUserId = null; session.CurrentAgentWorkItemId = null;
        await fixture.Db.SaveChangesAsync();
        var retry = new ResumeAgentCoordinationRequest(session.Id, 2, "Configured model is available again.", "manager-retry");
        var first = await fixture.Service.ResumeForManagerAsync(fixture.OrganizationId, fixture.ManagerId, retry);
        var replay = await fixture.Service.ResumeForManagerAsync(fixture.OrganizationId, fixture.ManagerId, retry);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(fixture.SessionId, first.Id);
        Assert.Equal(fixture.TargetId, first.CurrentOrganizationUserId);
        Assert.Single(first.Turns);
        Assert.Single(await fixture.Db.AgentWorkItems.Where(x => x.Status == AgentWorkStatus.Pending).ToListAsync());
        Assert.Equal(AgentWorkStatus.DeadLetter, work.Status);
    }

    [Fact]
    public async Task HumanRetryRejectsAgentAndUnprivilegedUserIdentities()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = new ResumeAgentCoordinationRequest(fixture.SessionId, 1, "Retry", "unauthorized-retry");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ResumeForManagerAsync(
            fixture.OrganizationId, fixture.TargetId, request));
        var manager = await fixture.Db.CoreOrganizationUsers.SingleAsync(x => x.Id == fixture.ManagerId);
        manager.PermissionLevel = OrganizationPermissionLevel.Contributor; await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ResumeForManagerAsync(
            fixture.OrganizationId, fixture.ManagerId, request));
        Assert.Empty(await fixture.Db.AgentWorkItems.ToListAsync());
    }
    private sealed class StubCommunicationHubService(CommunicationChatResponse chat)
        : ICommunicationHubService
    {
        public Task<CommunicationHubActionResponse> CreateAsync(
            Guid organizationId, Guid actorOrganizationUserId, CreateCommunicationChatRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommunicationHubActionResponse(true, null, "Created", chat));

        public Task<Guid?> ResolveOrganizationUserIdAsync(Guid organizationId, Guid applicationUserId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CommunicationHubResponse?> GetAsync(Guid organizationId, Guid actorOrganizationUserId,
            Guid? perspectiveOrganizationUserId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> CanAccessChatAsync(Guid organizationId, Guid chatId, Guid actorOrganizationUserId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CommunicationHubMessageResponse>?> ListMessagesAsync(
            Guid organizationId, Guid chatId, Guid actorOrganizationUserId,
            Guid? perspectiveOrganizationUserId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CommunicationUnreadSummaryResponse?> GetUnreadSummaryAsync(
            Guid organizationId, Guid actorOrganizationUserId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CommunicationUnreadSummaryResponse?> MarkReadAsync(
            Guid organizationId, Guid chatId, Guid actorOrganizationUserId, long throughMessageSequence,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CommunicationHubActionResponse> UpdateAsync(
            Guid organizationId, Guid chatId, Guid actorOrganizationUserId,
            UpdateCommunicationChatRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CommunicationHubActionResponse> ArchiveAsync(
            Guid organizationId, Guid chatId, Guid actorOrganizationUserId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CommunicationMessageSendResponse?> SendAsync(
            Guid organizationId, Guid chatId, Guid actorOrganizationUserId,
            SendCommunicationMessageRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static OrganizationUser AgentUser(
        Guid id, Guid organizationId, Guid installationId, string name) => new()
    {
        Id = id, OrganizationId = organizationId, DisplayName = name,
        EmployeeType = EmployeeType.Agent, PermissionLevel = OrganizationPermissionLevel.Contributor,
        AgentInstallationId = installationId, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };

    private static AgentInstallation Installation(
        Guid id, Guid organizationId, DateTimeOffset now)
    {
        var installation = new AgentInstallation
        {
            Id = id, InstallationKey = Guid.NewGuid(), PackageVersionId = Guid.NewGuid(),
            BusinessId = organizationId.ToString("D"), IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active, CreatedAt = now, UpdatedAt = now
        };
        installation.Grant = new AgentInstallationGrant
        {
            Id = Guid.NewGuid(), AgentInstallationId = id,
            EventSubscriptionsJson = "[]", ProvidedCapabilitiesJson = "[]",
            RequiredCapabilitiesJson = JsonSerializer.Serialize(new[]
            {
                CommunicationCapabilities.CoordinationRead,
                CommunicationCapabilities.CoordinationRespond
            }),
            NetworkAccessJson = "[]", ResourceLimitsJson = "{}", ApprovedAt = now
        };
        return installation;
    }

    private static Conversation Conversation(
        Guid id, Guid organizationId, Guid initiatorId, string title, DateTimeOffset now) => new()
    {
        Id = id, OrganizationId = organizationId, Title = title,
        Kind = ConversationKind.AgentChannel, InitiatedByOrganizationUserId = initiatorId,
        IsPrivate = true, CreatedAt = now, UpdatedAt = now
    };
}
