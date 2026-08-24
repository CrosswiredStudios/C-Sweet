using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
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
}
