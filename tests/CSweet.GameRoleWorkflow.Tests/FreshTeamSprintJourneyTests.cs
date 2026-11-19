extern alias Developer;
extern alias Engineer;
extern alias Producer;
extern alias Technical;

using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using GameProducer = Producer::CSweet.Agent.Producer.VideoGame;
using SoftwareDeveloper = Developer::CSweet.Agents.SoftwareDeveloper.SoftwareDeveloperAgent;
using GameEngineer = Engineer::CSweet.Agent.Engineer.VideoGame.GameEngineerAgent;
using GameTechnicalDirector = Technical::CSweet.Agent.TechnicalDirector.VideoGame.SpecialistAgent;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed partial class FreshTeamSprintJourneyTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fresh_team_starts_Sprint_1_and_engineer_claims_stack_setup_once_after_restart(bool dependencies) =>
        VerifyFirstClaimAsync(new FirstSprintModel(dependencies));

    [Fact, Trait("Category", "AgentHarness")]
    public Task Breakout_setup_spike_and_provider_recipe_reach_the_first_engineer_claim() =>
        VerifyFirstClaimAsync(new FirstSprintModel(false) { ReplaysBreakout = true });

    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Prism_Breaker_first_epic_assigns_only_the_approved_baseline_to_Adrian(bool reviseConflictingDraft, bool refineWithoutCandidate)
    {
        var stack = JsonSerializer.Deserialize<CheckpointStack>(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "prism-breaker-stack.json")), JsonSerializerOptions.Web)!;
        await VerifyFirstClaimAsync(new FirstSprintModel(true) {
            Pitch = "Build a single-screen brick breaker. Start with a playable baseline and deliver audio and visual effects in later epics.",
            Outcome = "Clearing a single row of bricks scores points and allows a restart.",
            StoryCriteria = [
                "The delivered source and runtime remain a 2D top-down single-playfield baseline and do not introduce 3D presentation, 3D game pipeline, or presentation-changing post-processing such as bloom, chromatic aberration, hit-stop, screen shake, or particle juice in this increment.",
                "Restore the dependencies with npm ci and verify the playable baseline in Chromium."
            ],
            Stack = stack,
            RefineWithoutCandidate = refineWithoutCandidate,
            SupersededCriterion = reviseConflictingDraft
                ? $"From a clean checkout, launch the first runnable slice using {stack.Engine} and {stack.Language}. {stack.Setup} Commit {stack.Lockfile} and verify a clean dependency restore."
                : null
        }, ["screen shake", "hit-stop", "particle bursts", "chromatic aberration", "game-over sting"]);
    }

    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prism_export_timeout_returns_the_failed_command_and_checkpoint_to_the_assigned_engineer(bool automatic)
    {
        const string command = "node scripts/export-setup-record.mjs setup.json";
        const string checkpoint = "e36ffb1ba0a4bbbc634083761688844e7ac61afb";
        var model = new FirstSprintModel(false);
        await using var platform = new FreshTeamSprintPlatform(useSoftwareDeveloper: true);
        await platform.OpenAsync(model.Pitch);
        var team = new PlanningTeam(platform, model);
        await team.StartHostsAsync();
        await team.PlanAsync();
        var first = Assert.IsType<CSweet.Infrastructure.Setup.ClaimedAgentWork>(await platform.ClaimAsync());
        var assignment = first.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!;
        platform.Db.SourceControlWorkspaces.Add(new() { Id = Guid.NewGuid(), OrganizationId = platform.Organization,
            WorkItemId = assignment.TaskItemId, AssignmentRevision = assignment.AssignmentRevision, TeamId = platform.Team,
            RepositoryId = platform.Repository, AgentInstallationId = platform.Engineer.AgentInstallationId,
            CheckpointAt = DateTimeOffset.UnixEpoch, CheckpointDigest = checkpoint,
            Status = CSweet.Domain.Setup.SourceControlWorkspaceStatus.Ready });
        await platform.Db.SaveChangesAsync();
        await platform.ReportTimeoutAsync(first, command);
        var second = Assert.IsType<CSweet.Infrastructure.Setup.ClaimedAgentWork>(await platform.ClaimAsync());
        await platform.ReportTimeoutAsync(second, command);
        var stage = await platform.Db.WorkStageExecutions.SingleAsync(s => s.Id == assignment.StageExecutionId);
        Assert.Equal(WorkStageExecutionStatus.Blocked, stage.Status);
        Assert.Contains("cause=workspace.command.timeout", stage.LastError);
        Assert.Contains(command, stage.LastError);
        var manager = Guid.NewGuid();
        var direction = new CommunicationMessageReceivedEvent(Guid.NewGuid(), Guid.NewGuid().ToString(), manager.ToString(),
            $"Retry ticket {assignment.Identifier}: Daniel, repair the exporter and submit the result for review.", new Dictionary<string, string> {
                [CommunicationMessageContextKeys.SenderEmployeeType] = "Human",
                [CommunicationMessageContextKeys.SenderOrganizationUserId] = manager.ToString(),
                ["senderIsReportingAncestor"] = "true" }, Guid.NewGuid(), 1, Guid.NewGuid());
        var schedule = await platform.Db.AgentSchedules.AsNoTracking().SingleAsync(s => s.AgentInstallationId == platform.Gabriel.AgentInstallationId);
        Assert.Equal("work.stage-stopped", schedule.PendingAttentionTriggerCategory);
        Assert.Equal(stage.Id, schedule.PendingAttentionCorrelationId);
        var now = schedule.NextAttentionReviewAt!.Value;
        var wake = new AgentAttentionReviewDueEvent(Guid.NewGuid(), now, now.AddMinutes(5), schedule.PendingAttentionReason!)
            { TriggerCategory = schedule.PendingAttentionTriggerCategory, CorrelationId = schedule.PendingAttentionCorrelationId };
        if (automatic) await team.DeliverStoppedAsync(wake);
        else await team.DeliverDirectionAsync(direction);
        var repaired = Assert.IsType<CSweet.Infrastructure.Setup.ClaimedAgentWork>(await platform.ClaimAsync());
        var next = repaired.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!;
        Assert.Equal("Daniel Kim", platform.Engineer.DisplayName);
        Assert.Equal(platform.Engineer.AgentInstallationId, (await platform.Db.AgentWorkItems.SingleAsync(w => w.Id == repaired.WorkId)).AgentInstallationId);
        Assert.Equal(assignment.ItemId, next.ItemId);
        Assert.Equal(assignment.StageExecutionId, next.StageExecutionId);
        var evidence = Assert.Single(next.Evidence!, e => e.Kind == "manager-direction");
        Assert.Contains(command, evidence.Value);
        Assert.Contains(checkpoint, evidence.Value);
        await team.StartHostsAsync();
        if (automatic) await team.DeliverStoppedAsync(wake);
        else await team.DeliverDirectionAsync(direction);
        Assert.Null(await platform.ClaimAsync());
        Assert.Single(await platform.Db.WorkOrchestrationEvents.Where(e => e.EventType == "stage.retry.requested").ToListAsync());
    }

    private static async Task VerifyFirstClaimAsync(FirstSprintModel model, string[]? deferredFeatures = null)
    {
        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        Assert.Empty(await platform.Db.WorkSprints.ToListAsync());
        Assert.Empty(await platform.Db.CoreWorkTasks.ToListAsync());
        var team = new PlanningTeam(platform, model);
        await team.StartHostsAsync();
        await team.PlanAsync();
        var sprint = Assert.Single(await platform.Db.WorkSprints.ToListAsync());
        Assert.Equal(1, sprint.Sequence);
        Assert.Equal(WorkSprintStatus.Active, sprint.Status);
        var setup = Assert.Single(await platform.Db.CoreWorkTasks.Where(t => t.SprintId == sprint.Id && t.IsExecutable).ToListAsync());
        Assert.Equal(platform.Engineer.OrganizationUserId, setup.AccountableOrganizationUserId);
        Assert.Equal(team.StoryAnswer.SessionId, setup.ProposalCoordinationSessionId);
        var planning = JsonSerializer.Deserialize<WorkItemPlanningSpecification>(setup.PlanningSpecificationJson!, JsonSerializerOptions.Web)!;
        var stack = model.Stack;
        var decision = Assert.Single(planning.Constraints!, c => c.Contains(stack.Rationale, StringComparison.Ordinal));
        Assert.Contains(stack.Engine, decision);
        Assert.Contains(stack.Language, decision);
        Assert.All(stack.Libraries, library => Assert.Contains(library, decision));
        var delivery = JsonSerializer.Deserialize<WorkItemDeliverySpecification>(setup.DeliverySpecificationJson!, JsonSerializerOptions.Web)!;
        Assert.Equal("Code", delivery.DeliveryKind);
        var criterion = Assert.Single(delivery.AcceptanceCriteria, c => c.Contains("clean checkout", StringComparison.OrdinalIgnoreCase) &&
            c.Contains(stack.Engine) && c.Contains(stack.Language));
        Assert.Contains(model.Outcome, delivery.AcceptanceCriteria);
        Assert.All(model.StoryCriteria, c => Assert.Contains(c, delivery.AcceptanceCriteria));
        foreach (var feature in deferredFeatures ?? [])
            Assert.DoesNotContain(delivery.AcceptanceCriteria.Except(model.StoryCriteria), c => c.Contains(feature, StringComparison.OrdinalIgnoreCase));
        if (stack.Lockfile is not null)
            Assert.Contains(stack.Lockfile, criterion);
        else
            Assert.All(delivery.AcceptanceCriteria, c => Assert.DoesNotMatch(@"(?i)\block[ -]?files?\b|[.-]lock\b", c));
        var lease = await platform.ClaimAsync();
        Assert.True(lease is not null, "Missing engineer lease. Stages: " +
            string.Join(", ", (await platform.Db.WorkItemExecutions.ToListAsync()).Select(s => $"{s.CurrentStageKey}/{s.Status}: {s.BlockedReason}")) +
            ". Inbox: " + string.Join(", ", (await platform.Db.AgentWorkItems.ToListAsync()).Select(w => $"{w.Name}/{w.Status}: {w.LastError}")));
        Assert.Equal(WorkManagementCapabilityNames.ExecutionRun, lease.Name);
        var assignment = lease.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!;
        Assert.Equal(setup.Id, assignment.ItemId);
        Assert.Equal(sprint.Id, assignment.SprintId);
        Assert.Equal("specialist-execution", assignment.StageKey);
        Assert.Equal(1, lease.Attempt);
        Assert.Empty(await platform.Db.WorkItemApprovals.ToListAsync());

        Assert.Equal(model.SupersededCriterion is null ? 0 : 1, model.RevisionRequests);
        if (model.RevisionRequests > 0)
        {
            var review = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "prism-breaker-refine-review.json")));
            var revision = Assert.Single(team.TechnicalPlanningRequests, message => message.Contains(review.GetProperty("draftRevisionReason").GetString()!));
            Assert.Contains(review.GetProperty("actions")[0].GetProperty("rationale").GetString()!, revision);
        }
        await team.RestartAndRedeliverAsync(model.RevisionRequests);
        Assert.Null(await platform.ClaimAsync());
        Assert.Empty(await platform.Db.WorkItemApprovals.ToListAsync());
        Assert.Single(await platform.Db.WorkSprints.ToListAsync());
        Assert.Single(await platform.Db.CoreWorkTasks.Where(t => t.ProposalItemKey == setup.ProposalItemKey).ToListAsync());
        Assert.Single(await platform.Db.WorkSprintExecutions.ToListAsync());
        Assert.Single(await platform.Db.CoreWorkTasks.Where(t => t.IsExecutable && t.ProposalItemKey != null).ToListAsync());
        Assert.Equal(lease.WorkId, (await platform.Db.AgentWorkAttempts.SingleAsync()).AgentWorkItemId);
        Assert.Equal(assignment.AttemptId, (await platform.Db.WorkExecutionAttempts.SingleAsync()).Id);
        Assert.Equal(1, model.StackChoices);
        Assert.Equal(1, model.Admissions);
        Assert.Equal(1, model.Estimates);
        if (model.ReplaysBreakout) Assert.Equal(1, model.RecipeCorrections);
    }

    // Retain each installation's platform state and coordination receipts across fresh SDK dispatchers.
    internal sealed partial class PlanningTeam
    {
        private readonly FreshTeamSprintPlatform platform;
        private readonly Guid? manager;
        private readonly FreshTeamPlanningQueue queue;
        private readonly Dictionary<Guid, AgentTestRuntime> platforms = [];
        private readonly Dictionary<Guid, AgentTestHost> hosts = [];
        private readonly Dictionary<string, AgentCoordinationSession> sessions = [];
        private readonly Dictionary<Guid, (AgentCoordinationTurnRequest Request, Guid Event)> deliveries = [];
        private readonly Dictionary<string, RespondToAgentCoordinationRequest> replies = [];
        private PersonalTodoAvailableEvent firstWake = null!;
        private readonly Guid firstEvent = Guid.NewGuid();
        private readonly Guid provider = Guid.NewGuid();
        private readonly Dictionary<Guid, string> modelNames = [];
        public Guid ProviderId => provider;

        public void SetModel(AgentCoordinationParticipant actor, string name, Func<JsonElement, CancellationToken, Task<object>> answer)
        {
            modelNames[actor.OrganizationUserId] = name;
            platforms[actor.OrganizationUserId].RegisterCapability(PlatformCapabilities.LlmChatStream, answer);
        }

        public Task<AgentOperatingState<CheckpointStack>?> ReadStackAsync()
        {
            var key = AgentOperatingStateKey.Create("game-stack:v1", new { project = platform.Project, revision = platform.RevisionId });
            return platforms[platform.Victor.OrganizationUserId].CreateContext().Platform.ReadOperatingStateAsync<CheckpointStack>(key);
        }

        public RespondToAgentCoordinationRequest StoryAnswer => replies.Values.Last(r =>
            r.Artifact?.Payload.TryGetProperty("progressivePlanning", out var evidence) == true &&
            evidence.GetProperty("phase").GetString() == "Story");

        public PlanningTeam(FreshTeamSprintPlatform platform, FirstSprintModel model, GameProducer.ProducerAcceptedHandoff? planningSource = null,
            PersonalTodoItem? planningCard = null, Guid? manager = null)
        {
            this.platform = platform;
            this.manager = manager;
            queue = new(platform.Gabriel.OrganizationUserId, planningCard?.BoardId);
            var digest = FreshTeamSprintPlatform.Digest(model.Pitch);
            var now = DateTimeOffset.UtcNow;
            var fingerprint = FreshTeamSprintPlatform.Digest(platform.Project.ToString() + digest);
            var handoff = planningSource ?? new GameProducer.ProducerAcceptedHandoff(platform.Project, platform.PitchId, platform.RevisionId, digest,
                digest, Guid.NewGuid(), platform.PackageId, 1, now);
            foreach (var actor in new[] { platform.Gabriel, platform.Victor, platform.Engineer })
            {
                var runtime = platforms[actor.OrganizationUserId] = new();
                var state = new OperatingStateFixture(runtime);
                if (actor == platform.Gabriel)
                    state.Seed("video-game/producer/portfolio", new GameProducer.ProducerOperatingState {
                        AcceptedHandoffs = new Dictionary<Guid, GameProducer.ProducerAcceptedHandoff> { [platform.Project] = handoff },
                        PlanningCycles = new Dictionary<Guid, GameProducer.ProducerPlanningCycleState> {
                            [platform.Project] = new(platform.Project, platform.Board, platform.Team, fingerprint, null, null, null, null, null, null, now) } });
                platform.Connect(runtime, actor);
                queue.Connect(runtime, actor.OrganizationUserId);
                runtime.RegisterCapability<ReadWorkstreamRequest, WorkstreamDetail>(PlatformCapabilities.WorkstreamRead, (_, _) =>
                    Task.FromResult(new WorkstreamDetail(platform.Project, "First playable", model.Pitch, [model.Outcome], "concept", "Approved",
                        platform.Gabriel.OrganizationUserId, null, null, null, "video-game-production.v2", 8, null, platform.ProfileDigest, 1)));
                runtime.RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2, (_, _) => Task.FromResult(new TeamRosterV2Response(platform.Roster, platform.Project)));
                var providerDecision = new DecisionRecord(Guid.NewGuid(), platform.Project, "video-game.toolchain-selection.v1",
                    "Select a certified provider for the 2D web packaging recipe.", "routine-certified-toolchain-selection",
                    [], "provider", "provider", DecisionStatuses.Decided, "Available for the web target.", [], null, null, null, 2, now, now);
                runtime.RegisterCapability<ReadDecisionRequest, IReadOnlyList<DecisionRecord>>(PlatformCapabilities.DecisionRead, (_, _) =>
                    Task.FromResult<IReadOnlyList<DecisionRecord>>(model.ReplaysBreakout ? [providerDecision] : []));
                runtime.RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead, (_, _) => Task.FromResult(new ResourceChangeReadResponse([])));
                runtime.RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read, (_, _) => Task.FromResult<IReadOnlyList<ResourceRequestState>>([]));
                runtime.RegisterCapability<ReadWorkIssuesRequest, WorkIssueDirectory>(WorkIssueCapabilities.Read, (_, _) => Task.FromResult(new WorkIssueDirectory([], [])));
                runtime.RegisterCapability<TeamRepositoryOptionsRequest, IReadOnlyList<TeamRepositoryOption>>(SourceControlCapabilities.TeamRepositoryOptions,
                    (_, _) => Task.FromResult<IReadOnlyList<TeamRepositoryOption>>([new(platform.Repository, "Game", "InternalGit", "game", "main", "PullRequest")]));
                runtime.RegisterCapability<ProvisionSourceControlRepositoryRequest, RepositoryProvisioningResult>(SourceControlCapabilities.ProvisionRepository,
                    (_, _) => Task.FromResult(new RepositoryProvisioningResult(platform.Repository, "Completed", platform.Repository, null, null)));
                runtime.RegisterCapability<ReadAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRead,
                    (r, _) => Task.FromResult(sessions.Values.Single(v => v.Id == r.SessionId)));
                runtime.RegisterCapability<RespondToAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRespond,
                    (r, _) => Task.FromResult(Respond(actor, r)));
                runtime.RegisterCapability<JsonElement, object>(PlatformCapabilities.LlmChatStream, (r, _) =>
                    Task.FromResult<object>(new { text = JsonSerializer.Serialize(model.Answer(actor, r), JsonSerializerOptions.Web) }));
            }
            platforms[platform.Gabriel.OrganizationUserId].RegisterCapability<StartBoardCoordinationRequest, AgentCoordinationSession>(
                CommunicationCapabilities.CoordinationStartBoard, (r, _) => Task.FromResult(StartSession(r)));
            if (planningCard != null) queue.Track(planningCard);
            else queue.Add(new("Plan the accepted game", "Plan the first playable increment", "High", null, "kickoff",
                CorrelationId: $"producer-planning:{platform.Project:N}:{fingerprint}") { WorkContext = new(platform.Project, platform.Team, platform.Board, SourceFingerprint: fingerprint) });
        }

        public async Task StartHostsAsync()
        {
            foreach (var (actor, agent) in new (AgentCoordinationParticipant, ICSweetAgent)[] {
                (platform.Gabriel, new GameProducer.SpecialistAgent()), (platform.Victor, new GameTechnicalDirector()), (platform.Engineer, platform.Engineer.Role == "software-developer" ? new SoftwareDeveloper() : new GameEngineer()) })
            {
                var identity = new AgentIdentity(actor.OrganizationUserId.ToString(), actor.DisplayName, null, actor.Role, null, [], null, actor == platform.Gabriel ? manager?.ToString() : null, null) { TeamContext = platform.Roster };
                var host = hosts[actor.OrganizationUserId] = new(agent, platforms[actor.OrganizationUserId], identity, platform.Organization.ToString(), actor.AgentInstallationId.ToString());
                var settings = new Dictionary<string, JsonElement> { ["llmProviderId"] = JsonSerializer.SerializeToElement(provider), ["llmModel"] = JsonSerializer.SerializeToElement(modelNames.GetValueOrDefault(actor.OrganizationUserId, "scripted")) };
                var configured = await host.ApplyConfigurationAsync(new(actor.AgentInstallationId, "1.0", settings, settings.Keys.ToArray(), 1, "fixture"));
                Assert.True(configured.Succeeded, configured.Error);
            }
        }

        public async Task DeliverStoppedAsync(AgentAttentionReviewDueEvent wake)
        {
            platforms[platform.Gabriel.OrganizationUserId].RegisterCapability<ReadManagementIncidents, ManagementIncidentPage>(
                ProjectHealthCapabilities.Incidents, (_, _) => Task.FromResult(new ManagementIncidentPage([], null)));
            var result = await hosts[platform.Gabriel.OrganizationUserId].DeliverEventAsync(AgentAttentionEvents.ReviewDue, wake, wake.ReviewId);
            Assert.True(result.Succeeded, result.Error);
        }

        public async Task DeliverDirectionAsync(CommunicationMessageReceivedEvent direction)
        {
            // A published installation has the delegation tools. Any accidental detour into
            // generic employee selection fails this journey before the leased retry.
            var runtime = platforms[platform.Gabriel.OrganizationUserId];
            runtime.RegisterCapability<JsonElement, JsonElement>(DelegationCapabilities.Create,
                (_, _) => throw new InvalidOperationException("A board retry must not create a delegation."));
            runtime.RegisterCapability<ReadDelegationRequest, IReadOnlyList<DelegationSnapshot>>(DelegationCapabilities.Read,
                (_, _) => Task.FromResult<IReadOnlyList<DelegationSnapshot>>([]));
            var result = await hosts[platform.Gabriel.OrganizationUserId].DeliverEventAsync(CommunicationEvents.MessageReceived, direction, direction.TurnId);
            Assert.True(result.Succeeded, result.Error);
        }

        public async Task PlanAsync(Func<Task<(PersonalTodoAvailableEvent, Guid)>>? nextWake = null, Func<bool>? reachedWait = null)
        {
            for (var step = 0; step < 16; step++)
            {
                var (wake, deliveryId) = nextWake is null ? (queue.NextWake(), Guid.NewGuid()) : await nextWake();
                if (firstWake is null) { firstWake = wake; deliveryId = firstEvent; }
                var result = await hosts[platform.Gabriel.OrganizationUserId].DeliverEventAsync(PersonalTodoEvents.Available, wake, deliveryId);
                Assert.True(result.Succeeded, result.Error);
                foreach (var session in sessions.Values.Where(s => s.Status == "Active").ToArray())
                {
                    var turn = new AgentCoordinationTurnRequest(session.Id, session.Revision, session.NextTurnOrdinal, session.Subject,
                        session.Objective, session.SuccessCriteria, session.Target, session.Initiator, false, session.Turns)
                        { SourceKind = "Board", BoardSource = session.BoardSource, WorkContext = session.WorkContext };
                    var eventId = Guid.NewGuid();
                    deliveries.Add(session.Id, (turn, eventId));
                    var answer = await hosts[session.Target.OrganizationUserId].DeliverCoordinationTurnAsync(turn, eventId);
                    Assert.True(answer.Succeeded, answer.Error);
                    if (turn.Transcript[0].Artifact!.Payload.TryGetProperty("progressivePlanning", out var request) &&
                        request.GetProperty("phase").GetString() == "Story")
                    {
                        // Crash after the authoritative return, before the Producer can read or publish it.
                        Assert.Empty(await platform.Db.CoreWorkTasks.Where(t => t.BoardId == platform.Board && t.IsExecutable).ToListAsync());
                        await StartHostsAsync();
                        var duplicate = await hosts[session.Target.OrganizationUserId].DeliverCoordinationTurnAsync(turn, eventId);
                        Assert.True(duplicate.Succeeded, duplicate.Error);
                    }
                }
                if (reachedWait?.Invoke() == true) return;
                if (await platform.Db.WorkSprints.AnyAsync(s => s.Status == WorkSprintStatus.Active)) return;
            }
            throw new InvalidOperationException("Sprint 1 did not start: " + queue.Describe());
        }

        public async Task RestartAndRedeliverAsync(int revisionRequests = 0, int expectedEstimates = 1)
        {
            var counts = (sessions.Count, replies.Count);
            Assert.Equal(expectedEstimates, sessions.Values.Count(s => s.Target == platform.Engineer));
            Assert.Equal(2 + revisionRequests + expectedEstimates, sessions.Count); // Outline, Story, revisions and owner estimates.
            Assert.Equal(sessions.Count, replies.Count);
            await StartHostsAsync();
            var (turn, eventId) = deliveries[StoryAnswer.SessionId];
            var answer = await hosts[turn.Self.OrganizationUserId].DeliverCoordinationTurnAsync(turn, eventId);
            Assert.True(answer.Succeeded, answer.Error);
            var wake = await hosts[platform.Gabriel.OrganizationUserId].DeliverEventAsync(PersonalTodoEvents.Available, firstWake, firstEvent);
            Assert.True(wake.Succeeded, wake.Error);
            Assert.Equal(counts, (sessions.Count, replies.Count));
        }

        private AgentCoordinationSession StartSession(StartBoardCoordinationRequest r)
        {
            if (sessions.TryGetValue(r.IdempotencyKey, out var existing)) return existing;
            var target = new[] { platform.Victor, platform.Engineer }.Single(p => p.OrganizationUserId == r.TargetOrganizationUserId);
            Assert.Equal(platform.Board, r.BoardId);
            var now = DateTimeOffset.UtcNow;
            return sessions[r.IdempotencyKey] = new(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, Guid.Empty, Guid.Empty, platform.Gabriel,
                target, r.Subject, r.Objective, r.SuccessCriteria, "Active", 1, 1, target.OrganizationUserId, false, null, now, now,
                [new(Guid.NewGuid(), 0, platform.Gabriel.OrganizationUserId, "Continue", r.InitialMessage, now, Store(r.Artifact!))])
                { SourceKind = "Board", BoardSource = new(platform.Board), WorkContext = new(platform.Organization, platform.Project,
                    platform.Team, platform.Board, null, null, null, Guid.NewGuid(), null, "video-game-production.v2") };
        }

        private AgentCoordinationSession Respond(AgentCoordinationParticipant actor, RespondToAgentCoordinationRequest r)
        {
            var (key, session) = sessions.Single(pair => pair.Value.Id == r.SessionId);
            Assert.Equal(session.Target, actor);
            Assert.True(r.Disposition == "Completed", $"{actor.DisplayName} {session.Subject}: {r.Content}");
            Assert.NotNull(r.Artifact);
            if (replies.TryGetValue(r.IdempotencyKey, out var prior))
            {
                Assert.Equal(prior.Artifact!.Payload.GetRawText(), r.Artifact.Payload.GetRawText());
                return session;
            }
            Assert.Equal(session.Revision, r.ExpectedRevision);
            replies.Add(r.IdempotencyKey, r);
            return sessions[key] = session with { Status = r.Disposition, Revision = session.Revision + 1,
                Turns = [.. session.Turns, new(Guid.NewGuid(), r.ExpectedTurnOrdinal, actor.OrganizationUserId, r.Disposition,
                    r.Content, DateTimeOffset.UtcNow, Store(r.Artifact))] };
        }

        private static AgentCoordinationArtifact Store(AgentCoordinationArtifactSubmission a) =>
            new(a.Type, a.SchemaVersion, a.Key, a.PageOrdinal, a.IsFinalPage, a.Payload, FreshTeamSprintPlatform.Digest(a.Payload.GetRawText()));
    }
}

