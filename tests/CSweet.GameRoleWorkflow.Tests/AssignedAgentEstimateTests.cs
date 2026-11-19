using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Agent.SDK;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.GameRoleWorkflow.Tests;

public sealed partial class FreshTeamSprintJourneyTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assigned_engineer_schedules_the_full_estimate_without_a_calendar_allocation(bool legacyZero)
    {
        var model = new FirstSprintModel(true) {
            Pitch = "Build Prism Break, a browser Breakout clone.", Outcome = "Verify the first playable browser increment.",
            SetupTitle = "Spike Chromium WebGL2/Web Audio Feasibility and Publish Technical Findings"
        };
        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        var team = new PlanningTeam(platform, model);
        var expectedPoints = await team.PlanAssignedEngineerAsync(model, legacyZero);
        var sprint = Assert.Single(await platform.Db.WorkSprints.ToListAsync());
        Assert.Equal(WorkSprintStatus.Active, sprint.Status);
        var ticket = await platform.Db.CoreWorkTasks.SingleAsync(t => t.IsExecutable && t.SprintId == sprint.Id);
        Assert.Equal(expectedPoints, ticket.EstimatePoints);
        Assert.Equal(expectedPoints, sprint.CapacityPoints);
        Assert.Equal(platform.Engineer.OrganizationUserId, ticket.AccountableOrganizationUserId);
        var lease = await platform.ClaimAsync();
        Assert.NotNull(lease);
        Assert.Equal(1, lease.Attempt);
        Assert.Equal(ticket.Id, lease.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!.ItemId);
        await team.RestartAndRedeliverAsync();
        Assert.Null(await platform.ClaimAsync());
        Assert.Empty(await platform.Db.WorkstreamDecisions.ToListAsync());
        Assert.Single(await platform.Db.WorkSprintExecutions.ToListAsync());
    }

    internal sealed partial class PlanningTeam
    {
        public async Task<decimal> PlanAssignedEngineerAsync(FirstSprintModel model, bool legacyZero)
        {
            var recorded = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                "qwen-adrian-21-zero.json")))!.AsObject();
            var checkpoint = Environment.GetEnvironmentVariable("CSWEET_ASSIGNED_CHECKPOINT_URL") is { Length: > 0 }
                ? await CheckpointModelEndpoint.OpenAsync(platform.Db, legacyZero ? platform.Gabriel : platform.Engineer,
                    platform.Organization, ProviderId, "CSWEET_ASSIGNED_CHECKPOINT") : null;
            decimal points = 0;
            SetModel(platform.Engineer, checkpoint?.Model ?? "effort-estimator", async (request, ct) => {
                var instructions = request.GetProperty("messages")[0].GetProperty("text").GetString()!;
                Assert.DoesNotContain("capacityAssessment", instructions);
                if (!legacyZero && checkpoint != null) return new { text = await checkpoint.GenerateTextAsync(request, ct) };
                var answer = legacyZero ? recorded.ToJsonString() : JsonSerializer.Serialize(new {
                    estimates = new[] { new { points = 21, confidence = "medium", rationale = "Integrated rendering and verification." } },
                    assumptions = Array.Empty<string>(), blockers = Array.Empty<string>()
                });
                return new { text = answer };
            });
            var runtime = platforms[platform.Engineer.OrganizationUserId];
            runtime.RegisterCapability<RespondToAgentCoordinationRequest, AgentCoordinationSession>(CommunicationCapabilities.CoordinationRespond, (r, _) => {
                var payload = JsonNode.Parse(r.Artifact!.Payload.GetRawText())!.AsObject();
                points = (decimal)payload["Estimates"]![0]!["EstimatePoints"]!;
                Assert.True(points > 0);
                if (legacyZero || checkpoint == null) Assert.Equal(21, points);
                if (legacyZero) {
                    // Reproduce the installed 4.5.3 wire reply to the same 21-point task.
                    payload["AvailableSprintCapacity"] = recorded["AvailableCapacity"]!.DeepClone();
                    payload.Remove("CapacityAssessment");
                    payload.Remove("AssessmentStateKey");
                    payload.Remove("AssessmentContract");
                }
                else Assert.Equal(points, (decimal)payload["AvailableSprintCapacity"]!);
                return Task.FromResult(Respond(platform.Engineer, r with {
                    Artifact = r.Artifact with { Payload = JsonSerializer.SerializeToElement(payload) } }));
            });
            SetModel(platform.Gabriel, checkpoint?.Model ?? "producer", async (request, ct) => {
                var prompt = request.GetProperty("messages")[0].GetProperty("text").GetString()!;
                if (legacyZero && checkpoint != null && prompt.Contains("preventsScheduling"))
                    return new { text = await checkpoint.GenerateTextAsync(request, ct) };
                object answer = prompt.Contains("preventsScheduling") ? new { preventsScheduling = false } : model.Answer(platform.Gabriel, request);
                return new { text = JsonSerializer.Serialize(answer, JsonSerializerOptions.Web) };
            });
            await StartHostsAsync();
            await PlanAsync();
            var estimate = sessions.Values.Single(s => s.Target == platform.Engineer);
            Assert.DoesNotContain("capacity", estimate.Objective, StringComparison.OrdinalIgnoreCase);
            return points;
        }
    }
}
