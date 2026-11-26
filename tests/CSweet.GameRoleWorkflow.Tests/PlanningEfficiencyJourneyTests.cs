using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed partial class FreshTeamSprintJourneyTests
{
    [Fact, Trait("Category", "AgentHarness")]
    public async Task Prism_Breaker_retained_planning_wait_avoids_repeat_advice_and_still_leases_ready_work()
    {
        var repeated = await RunForecastReviewAsync(reuseWait: false);
        var retained = await RunForecastReviewAsync(reuseWait: true);

        Assert.Equal((3, 3, 1), repeated);
        Assert.Equal((1, 1, 1), retained);
    }

    private static async Task<(int AdviceSessions, int RoutingCalls, int EngineerLeases)> RunForecastReviewAsync(bool reuseWait)
    {
        var model = new FirstSprintModel(false)
        {
            Pitch = "Deliver the Prism Breaker Canvas 2D playable slice and forecast from observed delivery.",
            SetupTitle = "Pin the toolchain and boot the Canvas 2D frame"
        };
        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        var db = platform.Db;
        var column = await db.WorkBoardColumns.Where(c => c.BoardId == platform.Board).Select(c => c.Id).FirstAsync();
        var playable = new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = platform.Organization, BoardId = platform.Board, BoardColumnId = column,
            Title = "Observe the first playable prototype and its QA result", Status = WorkTaskStatus.Running,
            AssignedEmployeeId = platform.Engineer.OrganizationUserId, AssignedAgentInstallationId = platform.Engineer.AgentInstallationId
        };
        var notes = new WorkTask
        {
            Id = Guid.NewGuid(), OrganizationId = platform.Organization, BoardId = platform.Board, BoardColumnId = column,
            Title = "Update internal release notes", Status = WorkTaskStatus.Ready
        };
        db.CoreWorkTasks.AddRange(playable, notes);
        await db.SaveChangesAsync();

        var team = new PlanningTeam(platform, model);
        var reviews = 0;
        var routingCalls = 0;
        var readCompletedEvidence = false;
        team.SetModel(platform.Gabriel, "scripted-forecast-review", (request, _) =>
        {
            var system = request.GetProperty("messages")[0].GetProperty("text").GetString()!;
            if (system.Contains("Route one bounded consultation", StringComparison.Ordinal))
            {
                routingCalls++;
                using var routing = JsonDocument.Parse(request.GetProperty("messages")[1].GetProperty("text").GetString()!);
                var contributor = Assert.Single(routing.RootElement.GetProperty("candidates").EnumerateArray(), person =>
                    person.GetProperty("DisplayName").GetString() == platform.Victor.DisplayName);
                return Answer(new { candidate = contributor.GetProperty("index").GetInt32(), reason = "Victor owns technical advice about the playable build." });
            }
            if (system.Contains("nextCandidate", StringComparison.Ordinal))
            {
                using var data = JsonDocument.Parse(request.GetProperty("messages")[1].GetProperty("text").GetString()!);
                var facts = data.RootElement.GetProperty("evidence").EnumerateArray().ToArray();
                var index = Array.FindIndex(facts, fact => fact.GetProperty("Id").GetGuid() == playable.Id);
                Assert.InRange(index, 0, facts.Length - 1);
                var proof = facts[index];
                if (proof.GetProperty("AwaitingResult").GetBoolean())
                {
                    reviews++;
                    var consult = !reuseWait || team.ForecastAdviceIds.Length == 0;
                    return Answer(new { disposition = "wait", nextCandidate = (int?)null, nextGoal = "Forecast from observed delivery.",
                        draftAhead = false, keepDraft = true, revisitWhen = "When the playable prototype has a QA result",
                        feelSummary = "The playable result is pending.", actions = new[] { new {
                            kind = consult ? "consult" : "proceed", capability = consult ? "TechnicalDirector" : null,
                            rationale = consult ? "Check whether the first playable build and QA result are ready for the forecast."
                                : "Retain the existing consultation and wait for the recorded playable result.",
                            evidenceIndexes = new[] { index }
                        } } });
                }
                readCompletedEvidence |= proof.GetProperty("Detail").GetString()!.Contains("Completed", StringComparison.Ordinal);
            }
            return Answer(model.Answer(platform.Gabriel, request));
        });

        await team.StartHostsAsync();
        // Keep specialist replies in flight in both arms. The Producer must react to current board evidence.
        await team.PlanAsync(reachedWait: () => reviews > 0, deliverSession: IsRequiredPlanningSession);
        var firstAdvice = Assert.Single(team.ForecastAdviceIds);
        for (var revision = 1; revision <= 2; revision++)
        {
            notes.Description = $"Release-note edit {revision}. The playable result is unchanged.";
            await db.SaveChangesAsync();
            await team.StartHostsAsync();
            await team.ReviewPlanningCardAsync();
        }
        Assert.Equal(3, reviews);
        Assert.Contains(firstAdvice, team.ForecastAdviceIds);
        Assert.Null(await platform.ClaimAsync());
        Assert.Empty(await db.WorkSprints.Where(sprint => sprint.Status == WorkSprintStatus.Active).ToListAsync());

        var advice = team.ForecastAdviceIds;
        playable.Status = WorkTaskStatus.Completed;
        await db.SaveChangesAsync();
        await team.PlanAsync(nextWake: team.NextProductionWakeAsync, deliverSession: IsRequiredPlanningSession);
        Assert.True(readCompletedEvidence);
        Assert.Equal(advice, team.ForecastAdviceIds);
        var sprint = Assert.Single(await db.WorkSprints.Where(s => s.Status == WorkSprintStatus.Active).ToListAsync());
        var ticket = Assert.Single(await db.CoreWorkTasks.Where(t => t.SprintId == sprint.Id && t.IsExecutable).ToListAsync());
        var claim = Assert.IsType<CSweet.Infrastructure.Setup.ClaimedAgentWork>(await platform.ClaimAsync());
        var assignment = claim.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!;
        Assert.Equal(ticket.Id, assignment.ItemId);
        Assert.Equal(team.StoryAnswer.SessionId, ticket.ProposalCoordinationSessionId);
        Assert.Equal(platform.Engineer.OrganizationUserId, ticket.AccountableOrganizationUserId);
        Assert.Null(await platform.ClaimAsync());
        return (advice.Length, routingCalls, await db.AgentWorkAttempts.CountAsync());
    }

    private static bool IsRequiredPlanningSession(AgentCoordinationSession session) =>
        session.Turns[0].Artifact?.Type != WorkContributions.RequestType;

    private static Task<object> Answer(object result) => Task.FromResult<object>(new { text = JsonSerializer.Serialize(result, JsonSerializerOptions.Web) });

    internal sealed partial class PlanningTeam
    {
        public Guid[] ForecastAdviceIds => sessions.Values.Where(session => !IsRequiredPlanningSession(session)).Select(session => session.Id).ToArray();

        public Task<(PersonalTodoAvailableEvent, Guid)> NextProductionWakeAsync() =>
            Task.FromResult((queue.NextWake(card => card.Title != "Receive scoped planning advice"), Guid.NewGuid()));
    }
}
