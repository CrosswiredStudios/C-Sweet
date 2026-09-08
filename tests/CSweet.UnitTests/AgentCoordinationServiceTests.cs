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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using DomainCoordinationSession = CSweet.Domain.Communications.AgentCoordinationSession;
using DomainCoordinationTurn = CSweet.Domain.Communications.AgentCoordinationTurn;

namespace CSweet.UnitTests;

public sealed class AgentCoordinationServiceTests
{
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
    public async Task DeadLetteredTurn_StoresOperationalFailureWithoutImpersonatingInitiator()
    {
        await using var fixture = await Fixture.CreateAsync();
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
        var lease = await fixture.Inbox.ClaimAsync(runtimeSession, CancellationToken.None);
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
    }

    private sealed class Fixture : IAsyncDisposable
    {
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
