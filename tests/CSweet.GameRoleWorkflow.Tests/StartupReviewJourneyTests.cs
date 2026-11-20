using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.GameRoleWorkflow.Tests;

internal sealed record StartupReviewIncident(string Title, string[] Requirements, string[] Criteria, string[] Constraints,
    string[] Goals, JsonElement Reply);

public sealed partial class FreshTeamSprintJourneyTests
{
    [Theory, Trait("Category", "AgentHarness")]
    [InlineData("publication")]
    [InlineData("review")]
    [InlineData("missing-receipt")]
    [InlineData("legacy")]
    [InlineData("broken")]
    public async Task Adrian_startup_PR_is_reviewed_against_its_six_published_criteria(string scenario)
    {
        var incident = JsonSerializer.Deserialize<StartupReviewIncident>(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "prism-breaker-startup-review.json")), JsonSerializerOptions.Web)!;
        var model = new FirstSprintModel(true) {
            Startup = incident, Pitch = string.Join(" ", incident.Goals), SetupTitle = incident.Title,
            Outcome = incident.Criteria[4], StoryCriteria = incident.Criteria[..4],
            Stack = new("PixiJS v7 WebGL renderer with a custom TypeScript game loop; collision and layout logic implemented directly (no full arcade engine)",
                "TypeScript", ["pixi.js"], "The WebGL renderer supports the desktop canvas.", "Restore and render a frame.", "package-lock.json")
        };
        await using var platform = new FreshTeamSprintPlatform();
        await platform.OpenAsync(model.Pitch);
        var team = new PlanningTeam(platform, model);
        await team.StartHostsAsync();
        await team.PlanAsync();
        var lease = Assert.IsType<CSweet.Infrastructure.Setup.ClaimedAgentWork>(await platform.ClaimAsync());
        var execution = lease.Payload.Deserialize<WorkExecutionAssignment>(JsonSerializerOptions.Web)!;
        var board = await team.ReadPublishedBoardAsync();
        var item = Assert.Single(board.Items, i => i.Id == execution.TaskItemId);
        Assert.Contains(incident.Title, item.Title);
        Assert.Equal(incident.Criteria, item.Planning!.AcceptanceCriteria);
        Assert.Equal(incident.Requirements, item.Planning.Requirements);
        var constraints = item.Planning.Constraints!;
        if (scenario == "publication")
        {
            var epic = Assert.Single(board.Items, i => i.ProposalProvenance?.ProposalItemKey == "roadmap-epic-1");
            Assert.All(incident.Goals, goal => Assert.Contains(goal, epic.Planning!.Constraints!));
            Assert.Empty(constraints.Intersect(incident.Goals));
            Assert.All(incident.Constraints[..6], rule => Assert.Contains(rule, constraints));
            Assert.Equal(constraints, item.Delivery!.Constraints);
            return;
        }
        if (scenario == "legacy")
            item = item with { Planning = item.Planning with { Constraints = constraints.Union(incident.Goals).ToArray() } };
        var result = await team.ReviewStartupAsync(execution, item, incident, scenario);
        if (scenario == "missing-receipt")
        {
            Assert.Equal("rejected", result.OutcomeCode);
            Assert.Contains(result.Diagnostics, reason => reason.Contains(WorkItemVerificationKinds.BrowserSmokeReceiptCommand));
            return;
        }
        Assert.True(result.OutcomeCode == (scenario == "broken" ? "rejected" : "approved"), result.Summary);
        var grades = result.Output.GetProperty("criteria").EnumerateArray().ToArray();
        Assert.Equal(incident.Criteria, grades.Select(c => c.GetProperty("criterionText").GetString()));
        Assert.Equal(scenario == "broken" ? 1 : 0, grades.Count(c => !c.GetProperty("satisfied").GetBoolean()));
        Assert.Contains(result.Evidence, e => e.Kind == "target-commit");
    }

    internal sealed partial class PlanningTeam
    {
        public Task<WorkBoardDetail> ReadPublishedBoardAsync() =>
            platforms[platform.Gabriel.OrganizationUserId].CreateContext().Platform.Work.ReadBoardAsync(platform.Board);

        public async Task<WorkExecutionOutcomeV1> ReviewStartupAsync(WorkExecutionAssignment execution, WorkItem item,
            StartupReviewIncident incident, string scenario)
        {
            var victor = platform.Victor;
            var runtime = platforms[victor.OrganizationUserId];
            var web = JsonSerializerOptions.Web;
            var broken = scenario == "broken";
            var input = execution.Input.Deserialize<WorkExecutionInputV1>(web)!;
            var stage = item.StageAssignments.Single(a => a.StageKey == "technical-review");
            Assert.Equal(victor.AgentInstallationId, stage.AgentInstallationId);
            var assignment = execution with { StageKey = stage.StageKey, StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
                Input = JsonSerializer.SerializeToElement(input with { Planning = item.Planning, AssignmentRequirements = stage.Requirements,
                    AssignmentSelection = stage.SelectionEvidence }, web) };
            runtime.RegisterCapability<WorkItemReference, WorkItem>(WorkManagementCapabilityNames.ItemRead, (_, _) => Task.FromResult(item));
            var patch = "diff --git a/src/main.ts b/src/main.ts\nnew file mode 100644\n--- /dev/null\n+++ b/src/main.ts\n@@ -0,0 +1,3 @@\n" +
                "+import { Application } from 'pixi.js';\n+const app = new Application<HTMLCanvasElement>();\n+" +
                (broken ? "throw new Error('startup failed');" : "document.body.appendChild(app.view);") + "\n";
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(patch)));
            var candidate = new GitMergeReview(Guid.NewGuid(), platform.Repository, item.Id, item.Title, hash[..40], null, "", [], [], "AwaitingValidation") {
                ChangedFiles = ["src/main.ts"], TargetBranch = item.Delivery!.BaseBranch, TargetCommitSha = new string('b', 40),
                PatchDigest = hash, ImplementationEvidence = [new("npm ci", true, 0, "Clean restore"), new("npm run build", true, 0, "Built"),
                    new(WorkItemVerificationKinds.BrowserSmokeReceiptCommand + " dist --canvas", true, 0, "Visible canvas")]
            };
            if (scenario == "missing-receipt") candidate = candidate with { ImplementationEvidence = candidate.ImplementationEvidence.Take(2).ToArray() };
            runtime.RegisterCapability<ReviewGitMergeRequest, GitMergeReview>(GitMergeCapabilities.Review, (r, _) =>
                Task.FromResult(r.DiffRead is null ? candidate : candidate with { DiffPage = new(hash, 0, patch.Length, patch, null) }));
            var decisions = 0;
            runtime.RegisterCapability<JsonElement, object>(PlatformCapabilities.LlmChatStream, (request, ct) => {
                var messages = request.GetProperty("messages");
                var data = JsonDocument.Parse(messages[1].GetProperty("text").GetString()!).RootElement;
                object answer;
                if (data.TryGetProperty("page", out _))
                    answer = new { summary = broken ? "Startup throws." : "Startup mounts a canvas.", blockingFindings = Array.Empty<object>(),
                        observations = Array.Empty<string>(), questions = Array.Empty<string>() };
                else
                {
                    decisions++;
                    var planning = data.GetProperty("reference").GetProperty("planning");
                    Assert.Equal(incident.Criteria, planning.GetProperty("acceptanceCriteria").EnumerateArray().Select(c => c.GetString()));
                    var schema = request.GetProperty("responseSchema").GetProperty("properties").GetProperty("criteria");
                    // A legacy ticket replays the actual invalid reply before following the corrected schema.
                    if (scenario == "legacy" && decisions == 1 || !schema.TryGetProperty("maxItems", out var count) || count.GetInt32() != incident.Criteria.Length)
                        answer = incident.Reply;
                    else
                    {
                        Assert.Contains("reference.planning.acceptanceCriteria", messages[0].GetProperty("text").GetString());
                        Assert.Equal(incident.Criteria.Length, schema.GetProperty("minItems").GetInt32());
                        answer = new { summary = broken ? "Startup fails." : "The startup slice passes.", additionalFindings = Array.Empty<object>(),
                            resolutions = Array.Empty<object>(), criteria = incident.Criteria.Select((_, i) => new {
                                satisfied = !broken || i != 2, evidence = broken && i == 2 ? "main.ts throws before mount." : "Candidate source and supplied restore, build and canvas receipts support this check.",
                                evidenceOffsets = new[] { 0 } }) };
                    }
                }
                return Task.FromResult<object>(new { text = JsonSerializer.Serialize(answer, web) });
            });
            var result = await hosts[victor.OrganizationUserId].ExecuteCapabilityAsync(WorkManagementCapabilityNames.ExecutionRun, assignment);
            Assert.True(result.Succeeded, result.Error);
            var outcome = result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(web)!;
            var expectedDecisions = scenario switch { "missing-receipt" => 0, "legacy" => 2, _ => 1 };
            Assert.True(decisions == expectedDecisions, $"Expected {expectedDecisions} synthesis calls, got {decisions}. {outcome.Summary}");
            return outcome;
        }
    }
}
