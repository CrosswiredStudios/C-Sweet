extern alias Producer;

using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.WorkManagement;
using CSweet.UnitTests;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Production = Producer::CSweet.Agent.Producer.VideoGame;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed partial class FreshTeamSprintJourneyTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task Qwen_estimate_resumes_on_QA_hire_and_preserves_the_owner_offer(bool lateQa, bool hireBeforeDefer, bool confirmedBlock)
    {
        var model = new FirstSprintModel(true) {
            Pitch = "Build Prism Break, a browser Breakout clone. Verify the first playable increment from a clean checkout.",
            Outcome = "The player moves the paddle to return the ball."
        };
        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        var manager = await platform.AddProjectDirectorAsync();
        var brief = await platform.AddProductionBriefAsync(model.Pitch, manager);
        var digest = FreshTeamSprintPlatform.Digest(model.Pitch);
        var handoff = new Production.ProducerAcceptedHandoff(platform.Project, brief.Document, brief.Revision,
            digest, digest, Guid.NewGuid(), platform.PackageId, 1, DateTimeOffset.UtcNow);
        var team = new PlanningTeam(platform, model, handoff, manager: manager.OrganizationUserId);
        await team.StartHostsAsync();
        await team.ReplayQwenEstimateAsync(model, manager, lateQa, hireBeforeDefer, confirmedBlock);
        if (confirmedBlock)
        {
            Assert.Single(await platform.Db.WorkstreamDecisions.ToListAsync());
            Assert.False(await platform.Db.WorkSprints.AnyAsync(s => s.Status == WorkSprintStatus.Active));
            Assert.Null(await platform.ClaimAsync());
            return;
        }

        var sprint = Assert.Single(await platform.Db.WorkSprints.ToListAsync());
        Assert.Equal(1, sprint.Sequence);
        Assert.Equal(WorkSprintStatus.Active, sprint.Status);
        Assert.Equal(13, sprint.CapacityPoints);
        var ticket = Assert.Single(await platform.Db.CoreWorkTasks.Where(t => t.SprintId == sprint.Id && t.IsExecutable).ToListAsync());
        Assert.Equal(13, ticket.EstimatePoints);
        Assert.Equal(platform.Engineer.OrganizationUserId, ticket.AccountableOrganizationUserId);
        var claim = await platform.ClaimAsync();
        Assert.NotNull(claim);
        Assert.Equal(1, claim.Attempt);
        Assert.Equal(ticket.Id, claim.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!.ItemId);
        await team.RestartAndRedeliverAsync(expectedEstimates: lateQa ? 2 : 1);
        Assert.Null(await platform.ClaimAsync());
        Assert.Single(await platform.Db.WorkSprintExecutions.ToListAsync());
        Assert.Empty(await platform.Db.WorkstreamDecisions.ToListAsync());
        Assert.Empty(await platform.Db.WorkItemApprovals.ToListAsync());
        Assert.Empty(await platform.Db.ExecutiveDecisions.ToListAsync());
    }

    internal sealed partial class PlanningTeam
    {
        private async Task<string[]> SetQwenOwnerEstimateAsync(bool confirmedBlock)
        {
            var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "qwen-adrian-estimate.json")))!;
            string[] notes = confirmedBlock ? ["The required environment has been checked and is unavailable. No executable work on this ticket can begin."]
                : fixture["blockers"]!.Deserialize<string[]>()!;
            SetModel(platform.Engineer, "recorded-owner", (_, _) => Task.FromResult<object>(new {
                text = JsonSerializer.Serialize(new { estimates = new[] { new { points = (int)fixture["points"]!, confidence = "high", rationale = "Size of the setup spike" } },
                    capacityAssessment = new { offeredPoints = (int)fixture["availableSprintCapacity"]!, rationale = "Available for the setup spike." },
                    assumptions = Array.Empty<string>(), blockers = notes }) }));
            // Adrian's installed SDK in the incident sent the legacy numeric projection only.
            platforms[platform.Engineer.OrganizationUserId].RegisterCapability<RespondToAgentCoordinationRequest, AgentCoordinationSession>(
                CommunicationCapabilities.CoordinationRespond, (r, _) => {
                    var payload = JsonNode.Parse(r.Artifact!.Payload.GetRawText())!.AsObject();
                    Assert.Equal(13, (int)payload["AvailableSprintCapacity"]!);
                    payload.Remove("CapacityAssessment");
                    payload.Remove("AssessmentStateKey");
                    return Task.FromResult(Respond(platform.Engineer, r with { Artifact = r.Artifact with { Payload = JsonSerializer.SerializeToElement(payload) } }));
                });
            return notes;
        }

        public async Task ReplayQwenEstimateAsync(FirstSprintModel model, AgentCoordinationParticipant manager,
            bool lateQa, bool hireBeforeDefer, bool confirmedBlock)
        {
            var db = platform.Db;
            var actor = new PersonalTodoActor(platform.Gabriel.OrganizationUserId, platform.Gabriel.AgentInstallationId);
            var runtime = platforms[actor.OrganizationUserId];
            platform.ConnectDecisions(runtime, platform.Gabriel);
            var todos = new PersonalTodoService(db, TimeProvider.System);
            await todos.EnsureBoardAsync(platform.Organization, actor.OrganizationUserId);
            runtime.RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, async (r, ct) => {
                if (r.CorrelationId?.StartsWith("producer-estimation:") != true) return queue.Add(r);
                var card = await todos.AddAsync(platform.Organization, actor, r, ct);
                queue.Track(card);
                return card;
            });
            var qa = platform.Roster.Members.Single(m => m.DeclaredRoleKeys.Contains("game-quality-assurance"));
            var qaId = Guid.Parse(qa.EmployeeId);
            if (lateQa)
            {
                (await db.TeamMemberships.SingleAsync(m => m.OrganizationUserId == qaId && m.TeamId == platform.Team)).EndedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
                runtime.RegisterCapability<TeamRosterV2Request, TeamRosterV2Response>(PlatformCapabilities.TeamRosterReadV2, async (r, ct) => {
                    var revision = await db.OrganizationTeams.Where(t => t.Id == r.TeamId).Select(t => t.Revision).SingleAsync(ct);
                    var present = await db.TeamMemberships.AnyAsync(m => m.TeamId == r.TeamId && m.OrganizationUserId == qaId && m.EndedAt == null, ct);
                    var members = platform.Roster.Members.Where(m => present || m.EmployeeId != qa.EmployeeId).ToArray();
                    return new(platform.Roster with { Revision = revision, Members = members }, platform.Project);
                });
                var approved = JsonSerializer.Deserialize<ResourceChangeRequestResponse>("{}")! with {
                    Id = Guid.NewGuid(), TeamId = platform.Team, WorkstreamId = platform.Project, Status = "Approved",
                    Roles = [new("game-quality-assurance", "game", "QA", "Verify this candidate", 1, 1, "Sprint 1",
                        [], false, actor.OrganizationUserId, null)] };
                runtime.RegisterCapability<ResourceChangeReadRequest, ResourceChangeReadResponse>(PlatformCapabilities.ResourceChangeRead,
                    (_, _) => Task.FromResult(new ResourceChangeReadResponse([approved])));
            }

            var notes = await SetQwenOwnerEstimateAsync(confirmedBlock);
            var endpoint = Environment.GetEnvironmentVariable("CSWEET_RISK_CHECKPOINT_URL") is { Length: > 0 }
                ? await CheckpointModelEndpoint.OpenAsync(db, platform.Gabriel, platform.Organization, ProviderId, "CSWEET_RISK_CHECKPOINT")
                : null;
            var riskJudgments = 0;
            SetModel(platform.Gabriel, endpoint?.Model ?? "recorded-producer", async (request, ct) => {
                var messages = request.GetProperty("messages");
                var instructions = messages[0].GetProperty("text").GetString()!;
                object answer;
                if (instructions.Contains("preventsScheduling"))
                {
                    var facts = messages[1].GetProperty("text").GetString()!;
                    Assert.All(notes, note => Assert.Contains(note, facts));
                    Assert.Equal(1, ++riskJudgments);
                    if (endpoint != null) return new { text = await endpoint.GenerateTextAsync(request, ct) };
                    answer = new { preventsScheduling = confirmedBlock };
                }
                else if (instructions.Contains("blockingImpact,choices"))
                {
                    Assert.True(confirmedBlock, "A 13-point owner offer with conditional risks was treated as zero capacity.");
                    answer = new RequirementDecisionDraft("Which commitment should move until the environment is available?", "The setup spike waits.",
                        [new("Investigate the outage", "Delay the spike", "Verify the next available environment") { Impact = "The team investigates before scheduling the spike." },
                         new("Reorder the backlog", "Bring other work forward", "Estimate the next eligible task") { Impact = "The team schedules independent work first." }], 0);
                }
                else answer = model.Answer(platform.Gabriel, request);
                return new { text = JsonSerializer.Serialize(answer, JsonSerializerOptions.Web) };
            });
            async Task HireQa() => await new TeamService(db, new TestAuditEventWriter(), TimeProvider.System)
                .AssignFromWorkflowAsync(platform.Organization, platform.Team, qaId, null, "HiringRecommendation", Guid.NewGuid());
            Guid? waitingCard = null;
            var decisionHeld = false;
            var hireWakeCount = 0;
            queue.OnDefer = async (request, simulated) => {
                var staffing = request.Reason.StartsWith("Sprint staffing:");
                var capacity = request.Reason.Contains("source-bound planning decision");
                if (!staffing && !capacity) return simulated;
                Assert.True(request.NextReviewAt > DateTimeOffset.UtcNow.AddMinutes(5));
                var row = await db.CoreWorkTasks.SingleAsync(t => t.Id == request.ItemId);
                row.Revision = request.ExpectedRevision;
                row.Status = WorkTaskStatus.Running;
                row.ClaimEventId = request.EventId;
                row.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
                foreach (var e in await db.AgentPlatformEventOutbox.Where(e => e.EventType == PersonalTodoEvents.Available).ToArrayAsync())
                    e.Status = AgentPlatformEventOutboxStatus.Published;
                await db.SaveChangesAsync();
                if (staffing)
                {
                    Assert.Equal(platform.Team, request.Dependency?.ResourceId);
                    Assert.Null(waitingCard);
                    waitingCard = row.Id;
                    if (hireBeforeDefer) await HireQa();
                }
                var result = await todos.DeferAsync(platform.Organization, actor, request);
                if (capacity)
                {
                    var decision = await db.WorkstreamDecisions.SingleAsync();
                    Assert.Equal(decision.Id, result.WorkContext?.DecisionId);
                    Assert.Null(row.ClaimEventId);
                    Assert.NotNull(result.Wait);
                    decisionHeld = true;
                }
                return result;
            };
            (PersonalTodoAvailableEvent Data, Guid Id)? hireEvent = null;
            await PlanAsync(async () => {
                if (waitingCard == null || hireWakeCount > 0) return (queue.NextWake(), Guid.NewGuid());
                await StartHostsAsync();
                if (!hireBeforeDefer) await HireQa();
                var wake = Assert.Single(await db.AgentPlatformEventOutbox.Where(e => e.EventType == PersonalTodoEvents.Available &&
                    e.TargetInstallationId == platform.Gabriel.AgentInstallationId && e.Status == AgentPlatformEventOutboxStatus.Pending).ToListAsync());
                var data = JsonSerializer.Deserialize<PersonalTodoAvailableEvent>(wake.DataJson, JsonSerializerOptions.Web)!;
                Assert.Equal(waitingCard, data.TriggerItemId);
                var ready = (await todos.ListAsync(platform.Organization, actor)).Boards.SelectMany(b => b.Items).Single(i => i.Id == waitingCard);
                Assert.Equal(PersonalTodoStatuses.Ready, ready.Status);
                Assert.Null(ready.Wait);
                queue.Track(ready);
                hireWakeCount++;
                hireEvent = (data, wake.Id);
                return hireEvent.Value;
            }, () => decisionHeld);
            Assert.Equal(1, riskJudgments);
            Assert.Equal(lateQa ? 1 : 0, hireWakeCount);
            Assert.Equal(confirmedBlock, decisionHeld);
            if (hireEvent is { } delivered)
            {
                var estimates = sessions.Values.Where(s => s.Target == platform.Engineer)
                    .Select(s => s.Turns[0].Artifact!.Payload.GetProperty("PlanningRevision").GetInt64()).ToArray();
                Assert.Equal(2, estimates.Distinct().Count());
                await StartHostsAsync();
                var duplicate = await hosts[actor.OrganizationUserId].DeliverEventAsync(PersonalTodoEvents.Available, delivered.Data, delivered.Id);
                Assert.True(duplicate.Succeeded, duplicate.Error);
            }
            if (decisionHeld)
            {
                var managerRuntime = new AgentTestRuntime();
                platform.ConnectDecisions(managerRuntime, manager);
                var decision = await db.WorkstreamDecisions.SingleAsync();
                var api = managerRuntime.CreateContext().Platform;
                await api.DecideDecisionAsync(new(decision.Id, decision.Revision, decision.RecommendedOptionId,
                    "Verify the required environment before planning this work.", "manager-direction"));
                var ready = (await todos.ListAsync(platform.Organization, actor)).Boards.SelectMany(b => b.Items)
                    .Single(c => c.WorkContext?.DecisionId == decision.Id);
                Assert.Equal(PersonalTodoStatuses.Ready, ready.Status);
                Assert.Null(ready.Wait);
                Assert.Single(await db.AgentPlatformEventOutbox.Where(e => e.EventType == PersonalTodoEvents.Available &&
                    e.IdempotencyKey.StartsWith("personal-todo-available:" + ready.Id.ToString("N") + ":dependency-")).ToListAsync());
            }
        }
    }
}
