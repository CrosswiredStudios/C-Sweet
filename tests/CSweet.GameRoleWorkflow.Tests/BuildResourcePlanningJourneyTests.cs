using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.WorkManagement;
using CSweet.UnitTests;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed partial class FreshTeamSprintJourneyTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Certified_build_wakes_Gabriels_retained_draft_before_the_timer(bool readyBeforeDefer, bool explicitWait)
    {
        var model = new FirstSprintModel(true);
        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        await platform.AddProjectDirectorAsync();
        var db = platform.Db;
        var approver = await db.CoreOrganizationUsers.SingleAsync(u => u.PermissionLevel == OrganizationPermissionLevel.Owner && u.EmployeeType == EmployeeType.Human);
        approver.ApplicationUserId = Guid.NewGuid();
        (await db.CoreOrganizationUsers.SingleAsync(u => u.Id == platform.Victor.OrganizationUserId)).ReportsToOrganizationUserId = approver.Id;
        await db.SaveChangesAsync();

        var clock = new ResourceFulfillmentTests.Clock();
        const string profile = "fixture.web-2d.v1";
        var handler = new ResourceFulfillmentTests.Handler(profile);
        var resources = new ResourceFulfillmentService(db, [handler], clock);
        var request = await resources.RequestAsync(platform.Organization, platform.Victor.AgentInstallationId,
            new(platform.Project, ResourceCapabilities.BuildToolchain, profile, "Build the approved game", "build"), default);
        await resources.ProcessNextAsync(default);
        var pending = await resources.GetAsync(platform.Organization, request.Id, default);
        await resources.DecideAsync(approver, new(request.Id, pending.Revision, pending.PlanHash, "Approve", "Provision build capacity", "approve"), default);
        if (explicitWait)
        {
            clock.Advance();
            Assert.True(await resources.ProcessNextAsync(default));
            clock.Advance();
            Assert.True(await resources.ProcessNextAsync(default));
            Assert.Equal("Certifying", (await resources.GetAsync(platform.Organization, request.Id, default)).Status);
        }

        var todos = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(platform.Gabriel.OrganizationUserId, platform.Gabriel.AgentInstallationId);
        await todos.EnsureBoardAsync(platform.Organization, actor.OrganizationUserId);
        var fingerprint = FreshTeamSprintPlatform.Digest(platform.Project + FreshTeamSprintPlatform.Digest(model.Pitch));
        var card = await todos.AddAsync(platform.Organization, actor, new("Plan the accepted game", "Plan the first playable increment", "High", null, "kickoff",
            CorrelationId: $"producer-planning:{platform.Project:N}:{fingerprint}") { WorkContext = new(platform.Project, platform.Team, platform.Board, SourceFingerprint: fingerprint) });
        var team = new PlanningTeam(platform, model, planningCard: card);
        await team.StartHostsAsync();
        await team.PlanThroughCertificationAsync(model, resources, handler, clock, todos, actor, card, request.Id, readyBeforeDefer, explicitWait);
        var sprint = Assert.Single(await db.WorkSprints.Where(s => s.Status == CSweet.Domain.WorkManagement.WorkSprintStatus.Active).ToListAsync());
        var ticket = Assert.Single(await db.CoreWorkTasks.Where(t => t.SprintId == sprint.Id && t.IsExecutable).ToListAsync());
        Assert.Equal(team.StoryAnswer.SessionId, ticket.ProposalCoordinationSessionId);
        Assert.Equal(platform.Engineer.OrganizationUserId, ticket.AccountableOrganizationUserId);
        var claim = await platform.ClaimAsync();
        Assert.NotNull(claim);
        Assert.Equal(ticket.Id, claim.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!.ItemId);
        await team.RestartAndRedeliverAsync();
        Assert.Null(await platform.ClaimAsync());
        Assert.Empty(await db.WorkItemApprovals.ToListAsync());
    }

    internal sealed partial class PlanningTeam
    {
        public async Task PlanThroughCertificationAsync(FirstSprintModel model, ResourceFulfillmentService resources,
            ResourceFulfillmentTests.Handler handler, ResourceFulfillmentTests.Clock clock, PersonalTodoService todos,
            PersonalTodoActor actor, PersonalTodoItem card, Guid resourceId, bool readyBeforeDefer, bool explicitWait)
        {
            var db = platform.Db;
            var runtime = platforms[actor.OrganizationUserId];
            var readCertifiedBuild = false;
            runtime.RegisterCapability<ResourceRead, IReadOnlyList<ResourceRequestState>>(ResourceCapabilities.Read,
                async (r, ct) => {
                    var states = await resources.ReadAsync(platform.Organization, platform.Gabriel.AgentInstallationId, r, ct);
                    readCertifiedBuild |= states.Any(s => s.Id == resourceId && s.Status == "Ready");
                    return states;
                });
            var changes = 0;
            var expectingDefer = false;
            SetModel(platform.Gabriel, "recorded-planning", async (request, ct) => {
                var messages = request.GetProperty("messages");
                object NormalAnswer() => new { text = JsonSerializer.Serialize(model.Answer(platform.Gabriel, request), JsonSerializerOptions.Web) };
                if (expectingDefer || !messages[0].GetProperty("text").GetString()!.Contains("nextCandidate")) return NormalAnswer();
                using var document = JsonDocument.Parse(messages[1].GetProperty("text").GetString()!);
                var input = document.RootElement;
                if (input.GetProperty("provisional").ValueKind == JsonValueKind.Array)
                {
                    if (explicitWait)
                    {
                        var evidence = input.GetProperty("evidence").EnumerateArray().ToArray();
                        var index = Array.FindIndex(evidence, f => f.GetProperty("Kind").GetString() == "pending-build-resource" && f.GetProperty("Id").GetGuid() == resourceId);
                        Assert.InRange(index, 0, evidence.Length - 1);
                        expectingDefer = true;
                        return new { text = JsonSerializer.Serialize(new { disposition = "wait", nextCandidate = (int?)null, nextGoal = "Retain this draft for certification",
                            draftAhead = false, keepDraft = true, revisitWhen = "When the build is certified", feelSummary = "Build capacity is being verified",
                            actions = new[] { new { kind = "proceed", rationale = "Wait for the approved build to become available", evidenceIndexes = new[] { index } } } }) };
                    }
                    // Two fulfillment transitions during successive judgments about the same draft.
                    clock.Advance();
                    Assert.True(await resources.ProcessNextAsync(ct));
                    changes++;
                    expectingDefer = changes == 2;
                    return new { text = """{"disposition":"select","nextCandidate":0,"nextGoal":"Continue the baseline","draftAhead":false,"keepDraft":true,"revisitWhen":"After build certification","feelSummary":"Build capacity is changing","actions":[{"kind":"resequence","rationale":"Plan around the available build capacity","evidenceIndexes":[0]}]}""" };
                }
                return NormalAnswer();
            });
            async Task Certify()
            {
                handler.Verified = true;
                clock.Advance();
                Assert.True(await resources.ProcessNextAsync(default));
                Assert.Equal("Ready", (await resources.GetAsync(platform.Organization, resourceId, default)).Status);
            }
            DeferPersonalTodoItemRequest? held = null;
            Guid[] earlierWakes = [];
            queue.OnDefer = async (request, simulatedCard) => {
                if (!expectingDefer || held != null) return simulatedCard;
                held = request;
                Assert.Equal(resourceId, request.Dependency?.ResourceId);
                Assert.True(request.NextReviewAt > DateTimeOffset.UtcNow.AddMinutes(5));
                // The journey emulates personal claims on EF InMemory. Transfer that exact claim
                // into the real card so defer, resource mutation and the resulting outbox are real.
                var row = await db.CoreWorkTasks.SingleAsync(t => t.Id == card.Id);
                row.Status = WorkTaskStatus.Running;
                row.ClaimEventId = request.EventId;
                row.ClaimExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
                row.Revision = request.ExpectedRevision;
                await db.SaveChangesAsync();
                var priorWakes = await db.AgentPlatformEventOutbox.Where(e => e.EventType == PersonalTodoEvents.Available).ToListAsync();
                foreach (var prior in priorWakes) prior.Status = AgentPlatformEventOutboxStatus.Published;
                earlierWakes = priorWakes.Select(e => e.Id).ToArray();
                db.AgentWorkItems.Add(new AgentWorkItem {
                    Id = Guid.NewGuid(), OrganizationId = platform.Organization.ToString(), AgentInstallationId = platform.Gabriel.AgentInstallationId,
                    Kind = CSweet.Domain.Setup.AgentWorkKind.Event, Name = PersonalTodoEvents.Available, Status = AgentWorkStatus.Leased,
                    IdempotencyKey = $"personal-todo-available:{card.Id:N}:{request.EventId:N}:{platform.Gabriel.AgentInstallationId:D}",
                    ProtectedPayload = [1], PayloadHash = "fixture", CorrelationId = "planning", CreatedAt = DateTimeOffset.UtcNow,
                    AvailableAt = DateTimeOffset.UtcNow, DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(5) });
                await db.SaveChangesAsync();
                if (readyBeforeDefer) await Certify();
                var result = await todos.DeferAsync(platform.Organization, actor, request);
                if (!readyBeforeDefer) Assert.Equal(request.Dependency, result.Wait?.Dependency);
                return result;
            };
            PersonalTodoAvailableEvent? readyWake = null;
            Guid readyEvent = default;
            await PlanAsync(async () => {
                if (held is null || readyWake != null) return (queue.NextWake(), Guid.NewGuid());
                await StartHostsAsync();
                if (!readyBeforeDefer) await Certify();
                var wake = Assert.Single(await db.AgentPlatformEventOutbox.Where(e => e.EventType == PersonalTodoEvents.Available &&
                    e.TargetInstallationId == platform.Gabriel.AgentInstallationId && !earlierWakes.Contains(e.Id)).ToListAsync());
                readyWake = JsonSerializer.Deserialize<PersonalTodoAvailableEvent>(wake.DataJson, JsonSerializerOptions.Web)!;
                Assert.Equal(card.Id, readyWake.TriggerItemId);
                var ready = (await todos.ListAsync(platform.Organization, actor)).Boards.SelectMany(b => b.Items).Single(i => i.Id == card.Id);
                Assert.Equal(PersonalTodoStatuses.Ready, ready.Status);
                Assert.Null(ready.Wait);
                Assert.True(DateTimeOffset.UtcNow < held.NextReviewAt);
                queue.Track(ready);
                readyEvent = wake.Id;
                return (readyWake, readyEvent);
            });
            Assert.NotNull(readyWake);
            Assert.True(readCertifiedBuild);
            var sessionCount = sessions.Count;
            await StartHostsAsync();
            var replay = await hosts[actor.OrganizationUserId].DeliverEventAsync(PersonalTodoEvents.Available, readyWake, readyEvent);
            Assert.True(replay.Succeeded, replay.Error);
            Assert.Equal(sessionCount, sessions.Count);
        }
    }
}
