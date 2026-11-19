extern alias Producer;

using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Actions = CSweet.Contracts.WorkManagement.WorkOrchestrationActions;
using Production = Producer::CSweet.Agent.Producer.VideoGame;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed partial class FreshTeamSprintJourneyTests
{
    public enum HoldScenario { Schedule, PauseScheduling, InvalidPreflight, ReplacedDuringPreflight }

    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(HoldScenario.Schedule)]
    [InlineData(HoldScenario.PauseScheduling)]
    [InlineData(HoldScenario.InvalidPreflight)]
    [InlineData(HoldScenario.ReplacedDuringPreflight)]
    public async Task Qwen_saved_hold_recovers_only_schedulable_work_through_platform_preflight(HoldScenario scenario)
    {
        var fixture = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "qwen-saved-capacity-hold.json")));
        var model = new FirstSprintModel(true) {
            Pitch = "Build Prism Break, a browser Breakout clone. Verify Chromium feasibility before implementing the game.",
            Outcome = "The first spike records technical findings from a clean checkout.",
            SetupTitle = fixture.GetProperty("ticket").GetProperty("title").GetString()!
        };
        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        var manager = await platform.AddProjectDirectorAsync();
        var brief = await platform.AddProductionBriefAsync(model.Pitch, manager);
        var digest = FreshTeamSprintPlatform.Digest(model.Pitch);
        var source = new Production.ProducerAcceptedHandoff(platform.Project, brief.Document, brief.Revision,
            digest, digest, Guid.NewGuid(), platform.PackageId, 1, DateTimeOffset.UtcNow);
        var team = new PlanningTeam(platform, model, source, manager: manager.OrganizationUserId);
        await team.ReplaySavedHoldAsync(model, source, fixture, scenario);
    }

    internal sealed partial class PlanningTeam
    {
        private sealed record SavedHold(WorkTask Ticket, CSweet.Domain.WorkManagement.WorkSprint Sprint,
            WorkstreamDecisionRecord Decision, Guid EstimationCardId, string ReportKey);

        private async Task<SavedHold> RestoreSavedHoldAsync(FirstSprintModel model, Production.ProducerAcceptedHandoff source,
            JsonElement fixture, HoldScenario scenario)
        {
            var pauseScheduling = scenario == HoldScenario.PauseScheduling;
            var db = platform.Db;
            var runtime = platforms[platform.Gabriel.OrganizationUserId];
            platform.ConnectDecisions(runtime, platform.Gabriel);
            var api = runtime.CreateContext().Platform;
            var record = fixture.GetProperty("ticket");
            var points = record.GetProperty("estimate").GetInt32();
            await SetQwenOwnerEstimateAsync(confirmedBlock: false);
            await StartHostsAsync();
            await PlanAsync(reachedWait: () => db.CoreWorkTasks.Local.Any(t => t.BoardId == platform.Board && t.IsExecutable));
            var ticket = await db.CoreWorkTasks.SingleAsync(t => t.IsExecutable && t.BoardId == platform.Board);
            var sprint = await db.WorkSprints.SingleAsync(s => s.BoardId == platform.Board);
            Assert.EndsWith(model.SetupTitle, ticket.Title);
            ticket.Title = model.SetupTitle;
            ticket.Description = record.GetProperty("description").GetString()!;
            ticket.PlanningRevision = record.GetProperty("planningRevision").GetInt64();
            Assert.Equal(record.GetProperty("typeKey").GetString(), ticket.TypeKey);
            await db.SaveChangesAsync();
            Assert.Equal(platform.Engineer.OrganizationUserId, ticket.AccountableOrganizationUserId);
            Assert.Equal(WorkSprintStatus.Planned, sprint.Status);
            Assert.Equal(1, sprint.Sequence);
            Assert.Null(sprint.CapacityPoints);
            PersonalTodoItem? estimating = null;
            queue.OnDefer = (r, card) => {
                if (r.Reason.StartsWith("Role estimation is pending:")) estimating = card;
                return Task.FromResult(card);
            };
            await PlanAsync(() => Task.FromResult((queue.NextWake(c => c.CorrelationId!.StartsWith("producer-estimation:")), Guid.NewGuid())),
                () => estimating != null);
            var card = Assert.IsType<PersonalTodoItem>(estimating);
            var estimate = sessions.Values.Single(s => s.Target == platform.Engineer &&
                s.Turns[0].Artifact!.Payload.GetProperty("PlanningRevision").GetInt64() == ticket.PlanningRevision);
            var artifact = estimate.Turns.Last().Artifact!;
            Assert.Equal(ticket.PlanningRevision, artifact.Payload.GetProperty("PlanningRevision").GetInt64());
            var estimated = await api.Work.EstimateAsync(new(platform.Board, ticket.Id, points, ticket.Revision, "snapshot-estimate") {
                Provenance = new(platform.Engineer.OrganizationUserId, platform.Engineer.AgentInstallationId,
                    estimate.Id, estimate.Revision, artifact.Digest, 0.9m) { PlanningRevision = ticket.PlanningRevision } });
            var ready = (await api.Work.ReadBoardAsync(platform.Board)).Columns.Single(c => c.Name == "Ready");
            if (estimated.ColumnId != ready.Id)
                estimated = await api.Work.MoveItemAsync(new(platform.Board, ticket.Id, ready.Id, estimated.Revision, "snapshot-ready"));
            if (estimated.SprintId == null)
                await api.Work.SetItemSprintAsync(new(platform.Board, ticket.Id, sprint.Id, estimated.Revision, "snapshot-scope"));
            ticket.Identifier = record.GetProperty("identifier").GetString();
            var decision = new WorkstreamDecisionRecord {
                Id = fixture.GetProperty("decisionId").GetGuid(), WorkstreamId = platform.Project, OrganizationId = platform.Organization,
                Revision = fixture.GetProperty("revision").GetInt64(), Status = DecisionStatuses.Decided,
                TypeKey = "video-game.management-direction.v1", AuthorityRuleKey = "work-planning",
                Summary = fixture.GetProperty("question").GetString()!, OptionsJson = fixture.GetProperty("options").GetRawText(),
                RecommendedOptionId = "choice-0", SelectedOptionId = pauseScheduling ? "choice-1" : fixture.GetProperty("selectedOptionId").GetString(),
                Rationale = pauseScheduling ? "Keep this spike out of the sprint until I confirm the target branch. Return to me before scheduling it."
                    : fixture.GetProperty("rationale").GetString(),
                TypeDataJson = JsonSerializer.Serialize(new { contract = RequirementDecisions.Contract, purpose = "work-capacity",
                    boardId = platform.Board, planningDigest = card.WorkContext!.SourceFingerprint }),
                EvidenceJson = JsonSerializer.Serialize(new[] { new EvidenceReference("artifact", source.ArtifactId,
                    source.AcceptedRevisionId, source.RevisionDigest, "video-game.production-plan.v1", "Accepted") }, JsonSerializerOptions.Web),
                RequestedByOrganizationUserId = platform.Gabriel.OrganizationUserId, RequestedByInstallationId = platform.Gabriel.AgentInstallationId,
                DecidedByOrganizationUserId = manager, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, IdempotencyKey = "snapshot"
            };
            db.WorkstreamDecisions.Add(decision);
            await db.SaveChangesAsync();
            var saved = fixture.GetProperty("followThrough");
            var key = AgentOperatingStateKey.Create("requirement-followthrough:v1", new { decision.Id, decision.Revision });
            await api.WriteOperatingStateAsync(new(key, "work.requirement-followthrough.v1", 1, "Active", new Dictionary<string,string>(),
                [], key, [], Guid.NewGuid(), saved, null, key));
            // An earlier Producer may also have cached its interpretation under the calendar-based policy.
            var oldJudgment = AgentOperatingStateKey.Create("producer-scheduling-direction:v1", new {
                decision = decision.Id, decision.Revision, planningDigest = card.WorkContext.SourceFingerprint,
                candidate = ticket.Id, ticket.PlanningRevision });
            await api.WriteOperatingStateAsync(new(oldJudgment, "producer.scheduling-direction.v1", 1, "Active",
                new Dictionary<string,string>(), [], oldJudgment, [], Guid.NewGuid(),
                JsonSerializer.SerializeToElement(new { HoldScheduling = true, Reason = "No sprint allocation has been established." }),
                null, oldJudgment));
            // Restore both estimation timers and the provisional preflight timer from the incident.
            var savedCard = card with { WorkContext = card.WorkContext! with { DecisionId = decision.Id },
                Wait = new(DateTimeOffset.UtcNow.AddSeconds(-2), saved.GetProperty("Summary").GetString()!, manager) };
            queue.Track(savedCard);
            queue.Track(savedCard with { Id = Guid.NewGuid(), Title = "Reassess staffing and scope",
                CorrelationId = card.CorrelationId + ":3", Wait = savedCard.Wait! with { NextReviewAt = DateTimeOffset.UtcNow.AddSeconds(-1) } });
            var readiness = queue.Add(new("Preflight sprint " + sprint.Name, "Revalidate the selected scope.", "Critical", null, "snapshot-readiness",
                CorrelationId: $"producer-readiness:{sprint.Id:N}:{ticket.PlanningRevision}") {
                WorkContext = card.WorkContext with { DecisionId = null, SprintId = sprint.Id } });
            queue.Track(readiness with { Status = PersonalTodoStatuses.Running,
                Wait = new(DateTimeOffset.UtcNow.AddSeconds(-3), "This sprint is still a provisional draft awaiting estimates and staffing.") });
            Assert.Equal(sprint.Id, ticket.SprintId);
            Assert.Equal(points, ticket.EstimatePoints);
            Assert.Equal(ready.Id, ticket.BoardColumnId);
            Assert.Null(await platform.ClaimAsync());

            return new(ticket, sprint, decision, card.Id, key);
        }

        public async Task ReplaySavedHoldAsync(FirstSprintModel model, Production.ProducerAcceptedHandoff source,
            JsonElement fixture, HoldScenario scenario)
        {
            var (ticket, sprint, decision, estimationCardId, reportKey) = await RestoreSavedHoldAsync(model, source, fixture, scenario);
            var saved = fixture.GetProperty("followThrough");
            var pauseScheduling = scenario == HoldScenario.PauseScheduling;
            var points = fixture.GetProperty("ticket").GetProperty("estimate").GetInt32();
            var db = platform.Db;
            var previousEstimates = sessions.Values.Count(s => s.Target == platform.Engineer);
            var endpoint = Environment.GetEnvironmentVariable("CSWEET_HOLD_CHECKPOINT_URL") is { Length: > 0 }
                ? await CheckpointModelEndpoint.OpenAsync(db, platform.Gabriel, platform.Organization, ProviderId, "CSWEET_HOLD_CHECKPOINT") : null;
            int judgments = 0;
            SetModel(platform.Gabriel, endpoint?.Model ?? "recorded-producer", async (request, ct) => {
                var messages = request.GetProperty("messages");
                var instructions = messages[0].GetProperty("text").GetString()!;
                if (instructions.Contains("preventsScheduling")) return new { text = """{"preventsScheduling":false}""" };
                if (!instructions.Contains("holdScheduling"))
                    return new { text = JsonSerializer.Serialize(model.Answer(platform.Gabriel, request), JsonSerializerOptions.Web) };
                var evidence = messages[1].GetProperty("text").GetString()!;
                var facts = JsonSerializer.Deserialize<JsonElement>(evidence);
                Assert.Equal(ticket.Title, facts.GetProperty("ticket").GetProperty("Title").GetString());
                Assert.Equal(decision.SelectedOptionId, facts.GetProperty("selectedOption").GetProperty("Id").GetString());
                Assert.Equal(decision.Rationale, facts.GetProperty("Rationale").GetString());
                Assert.Equal(saved.GetProperty("Summary").GetString(), facts.GetProperty("historicalFollowThrough").GetString());
                judgments++;
                if (endpoint != null) return new { text = await endpoint.GenerateTextAsync(request, ct) };
                return new { text = JsonSerializer.Serialize(new { holdScheduling = pauseScheduling, reason = pauseScheduling ? "Confirm the branch before scheduling." : "Schedule the spike and verify the branch on the ticket." }) };
            });
            ConfigurePreflightControl(scenario, decision);
            string? wait = null;
            queue.OnDefer = (r, deferred) => {
                if (r.Reason == saved.GetProperty("Summary").GetString() ||
                    (pauseScheduling && r.WaitingOnOrganizationUserId == manager) || r.Reason.StartsWith("Sprint preflight is waiting:") ||
                    r.Reason.Contains("direction changed during preflight"))
                    wait = r.Reason;
                return Task.FromResult(deferred);
            };
            (PersonalTodoAvailableEvent Wake, Guid Event)? recoveredWake = null;
            await StartHostsAsync();
            await PlanAsync(() => {
                var next = (queue.NextWake(c => c.CorrelationId!.StartsWith("producer-estimation:") || c.CorrelationId.StartsWith("producer-readiness:")), Guid.NewGuid());
                if (next.Item1.TriggerItemId == estimationCardId) recoveredWake ??= next;
                return Task.FromResult(next);
            }, () => wait != null);
            Assert.True(wait != saved.GetProperty("Summary").GetString(), "Gabriel repeated the saved hold: " + wait);
            Assert.Equal(1, judgments);
            Assert.NotNull(recoveredWake);
            var report = await platforms[platform.Gabriel.OrganizationUserId].CreateContext().Platform.ReadOperatingStateAsync<JsonElement>(reportKey);
            Assert.NotNull(report);
            Assert.Equal(1, report.Revision);
            Assert.True(JsonElement.DeepEquals(saved, report.Payload));
            Assert.Equal(scenario == HoldScenario.ReplacedDuringPreflight ? 2 : 1, await db.WorkstreamDecisions.CountAsync());
            Assert.Empty(await db.ExecutiveDecisions.ToListAsync());
            Assert.Empty(await db.WorkItemApprovals.ToListAsync());
            var commands = platform.SprintCommands;
            if (scenario != HoldScenario.Schedule)
            {
                Assert.NotNull(wait);
                Assert.Equal(WorkSprintStatus.Planned, sprint.Status);
                Assert.Null(await platform.ClaimAsync());
                if (pauseScheduling) Assert.Empty(commands);
                else {
                    Assert.Equal(Actions.Preflight, Assert.Single(commands).Action);
                    if (scenario == HoldScenario.InvalidPreflight) {
                        Assert.Contains("policy.not_published", commands[0].Result.GetRawText());
                        Assert.Contains("Publish an orchestration policy", wait);
                    }
                    else Assert.Contains("direction changed during preflight", wait);
                }
                return;
            }
            Assert.Null(wait);
            Assert.Equal(WorkSprintStatus.Active, sprint.Status);
            Assert.Equal(points, sprint.CapacityPoints);
            Assert.Equal(new[] { Actions.Preflight, Actions.Start }, commands.Select(c => c.Action));
            var claim = await platform.ClaimAsync();
            Assert.NotNull(claim);
            var assignment = claim.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!;
            Assert.Equal(ticket.Id, assignment.ItemId);
            Assert.Equal(sprint.Id, assignment.SprintId);
            Assert.Equal("specialist-execution", assignment.StageKey);
            Assert.Equal(1, claim.Attempt);
            Assert.Equal(previousEstimates + 1, sessions.Values.Count(s => s.Target == platform.Engineer));
            await StartHostsAsync();
            var duplicate = await hosts[platform.Gabriel.OrganizationUserId].DeliverEventAsync(PersonalTodoEvents.Available,
                recoveredWake.Value.Wake, recoveredWake.Value.Event);
            Assert.True(duplicate.Succeeded, duplicate.Error);
            Assert.Null(await platform.ClaimAsync());
            Assert.Single(await db.WorkSprints.ToListAsync());
            Assert.Single(await db.CoreWorkTasks.Where(t => t.IsExecutable && t.BoardId == platform.Board).ToListAsync());
            Assert.Single(await db.WorkSprintExecutions.ToListAsync());
            Assert.Equal(assignment.AttemptId, (await db.WorkExecutionAttempts.SingleAsync()).Id);
            Assert.Equal(claim.WorkId, (await db.AgentWorkAttempts.SingleAsync()).AgentWorkItemId);
            Assert.Equal(2, commands.Count);
            Assert.Equal(1, judgments);
        }

        private void ConfigurePreflightControl(HoldScenario scenario, WorkstreamDecisionRecord decision)
        {
            var db = platform.Db;
            if (scenario == HoldScenario.ReplacedDuringPreflight)
                platform.BeforePreflight = async () => {
                    var replacement = new WorkstreamDecisionRecord {
                        Id = Guid.NewGuid(), OrganizationId = platform.Organization, WorkstreamId = platform.Project,
                        Status = DecisionStatuses.Pending, SupersedesDecisionId = decision.Id, TypeKey = decision.TypeKey,
                        AuthorityRuleKey = decision.AuthorityRuleKey, OptionsJson = decision.OptionsJson, TypeDataJson = decision.TypeDataJson,
                        Summary = "Reassess scheduling after the target branch changed.", IdempotencyKey = "replacement",
                        RequestedByOrganizationUserId = platform.Gabriel.OrganizationUserId
                    };
                    db.WorkstreamDecisions.Add(replacement);
                    decision.Status = DecisionStatuses.Superseded;
                    decision.SupersededByDecisionId = replacement.Id;
                    decision.Revision++;
                    await db.SaveChangesAsync();
                };
            if (scenario == HoldScenario.InvalidPreflight)
                platform.BeforePreflight = async () => {
                    (await db.WorkOrchestrationPolicies.SingleAsync(p => p.BoardId == platform.Board)).PublishedRevisionId = null;
                    await db.SaveChangesAsync();
                };
        }
    }
}
