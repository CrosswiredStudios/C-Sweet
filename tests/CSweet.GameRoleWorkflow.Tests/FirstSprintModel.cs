using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.GameRoleWorkflow.Tests;

// Only model judgments are scripted. In particular the task model never supplies an engine,
// setup criterion, lockfile, ticket identity, sprint or assignee.
internal sealed class FirstSprintModel(bool dependencies)
{
    public string SetupTitle { get; init; } = "Implement the opening scene";
    public string? IndivisibleReason { get; init; }
    public bool SplitDelivery { get; init; }
    public bool ReviewedStoryCopy { get; private set; }
    public bool ReplaysBreakout { get; init; }
    public int RecipeCorrections { get; private set; }
    public string Pitch { get; init; } = dependencies
        ? "Make a browser game where the player deflects moving obstacles using a simulated spring."
        : "Make a tiny browser puzzle where a player moves a tile one square at a time.";
    public string Outcome { get; init; } = dependencies ? "The spring deflects the obstacle." : "The selected tile moves one square.";
    public string[] StoryCriteria { get; init; } = [];
    public StartupReviewIncident? Startup { get; init; }
    public string? SupersededCriterion { get; init; }
    public bool RefineWithoutCandidate { get; init; }
    public int RevisionRequests { get; private set; }
    public CheckpointStack Stack { get; set; } = dependencies
        ? new("Matter.js", "TypeScript", ["matter-js"], "A physics solver supports the spring response.", "Install dependencies and launch the spring scene.", "package-lock.json")
        : new("DOM", "JavaScript", [], "Native DOM input covers a single tile with minimal setup.", "Open the puzzle page directly.", null);
    public int StackChoices { get; private set; }
    public int Admissions { get; private set; }
    public int Estimates { get; private set; }
    private int technicalPage = 1;
    private const string Brief = """
        Outcome: Implement one playable interaction and leave scoring outside this first increment.
        Context: Locate the scene entry point and input handler in the project or create them for the first scene.
        Pseudocode: read input, advance the scene by one step, then render the updated position.
        Assumptions: The input represents one player action. Keep the scene resettable for verification.
        Verification: Launch from a clean checkout, exercise the action once and confirm the position changes once.
        """;

    private object SplitTasks(string[] messages)
    {
        Assert.Contains(messages, text => text.Contains("Split recursively until each task has one independently verifiable outcome"));
        return new { setupTaskIndex = 0, tasks = new[] {
            Task("Restore and boot the canvas", [0], []), Task("Resolve a wall collision", [1], []),
            Task("Show one rally on the canvas", [2], [0, 1]) } };
        static object Task(string title, int[] criteria, int[] earlier) => new { title, description = Brief, workItemTypeKey = "video-game.task.v1",
            accountableRoleKey = "game-engineer", requiredSpecializationKeys = Array.Empty<string>(), preferredSpecializationKeys = Array.Empty<string>(),
            storyCriterionIndexes = criteria, additionalCriteria = Array.Empty<string>(), browserVerification = "none",
            interactionCriteria = Array.Empty<int>(), earlierTaskIndexes = earlier, priorTaskIndexes = Array.Empty<int>() };
    }

    public object Answer(AgentCoordinationParticipant actor, JsonElement request)
    {
        var messages = request.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString()!).ToArray();
        if (actor.Role is "game-engineer" or "software-developer")
        {
            Estimates++;
            if (SplitDelivery)
            {
                int count = JsonDocument.Parse(messages[1]).RootElement.GetProperty("work").GetArrayLength();
                return new { estimates = Enumerable.Repeat(new { points = 1, confidence = "high", rationale = "One focused result per ticket." }, count),
                    capacityAssessment = new { offeredPoints = 5, rationale = "Available for these tickets." }, assumptions = Array.Empty<string>(), blockers = Array.Empty<string>() };
            }
            Assert.Contains(messages, text => text.Contains("clean checkout", StringComparison.OrdinalIgnoreCase) && text.Contains(Stack.Engine));
            return new { estimates = new[] { new { points = 2, confidence = "high", rationale = "One small scene with one input." } },
                capacityAssessment = new { offeredPoints = 3, rationale = "There is room for the first scene." }, assumptions = Array.Empty<string>(), blockers = Array.Empty<string>() };
        }

        if (actor.Role == "game-producer")
        {
            var data = JsonDocument.Parse(messages[1]).RootElement;
            if (Startup is { } incident && data.TryGetProperty("existingObligations", out var registry))
                return new { commitments = incident.Goals.Select((statement, index) => new { statement, sources = new[] { 0 },
                    evidenceIndexes = new[] { 0 }, proposedIndexes = new[] { index },
                    existingObligationIndex = registry.EnumerateArray().Where(e => e.GetProperty("Statement").GetString() == statement)
                        .Select(e => (int?)e.GetProperty("index").GetInt32()).SingleOrDefault() }), gaps = Array.Empty<string>() };
            if (data.TryGetProperty("existingObligations", out var existing))
                return new { commitments = new[] { new { statement = Outcome, sources = new[] { 0 }, evidenceIndexes = new[] { 0 },
                    proposedIndexes = new[] { 0 }, existingObligationIndex = existing.GetArrayLength() == 0 ? (int?)null : 0 } }, gaps = Array.Empty<string>() };
            Assert.True(data.TryGetProperty("candidates", out var candidates),
                "Unscripted Producer judgment: " + messages[0][..Math.Min(320, messages[0].Length)] + " " + string.Join(", ", data.EnumerateObject().Select(p => p.Name)));
            if (data.GetProperty("provisional").ValueKind == JsonValueKind.Array)
            {
                var tasks = data.GetProperty("provisional");
                Assert.Contains(Stack.Engine, tasks.GetRawText());
                if (IndivisibleReason != null) Assert.True(data.GetProperty("copiesWholeStory").GetBoolean());
                if (SplitDelivery && data.GetProperty("copiesWholeStory").GetBoolean())
                {
                    if (messages.Any(m => m.Contains("The only task copies the whole Story.")))
                    {
                        ReviewedStoryCopy = true;
                        RevisionRequests++;
                        return new { disposition = "refine", nextGoal = "Split the Story", nextCandidate = (int?)null, draftAhead = false, keepDraft = false,
                            draftRevisionReason = "Separate independently testable clean restore and collision rules before integrating them.", revisitWhen = "After the split",
                            feelSummary = "The child repeats the Story", actions = new[] { new { kind = "refine", rationale = "Separate independently testable clean restore and collision rules before integrating them.", evidenceIndexes = new[] { 0 } } } };
                    }
                    // Replay the original acceptance of one ticket without examining whether the scope is separable.
                }
                if (SupersededCriterion is not null && tasks.EnumerateArray().Any(task =>
                    task.GetProperty("AcceptanceCriteria").EnumerateArray().Any(c => c.GetString() == SupersededCriterion)))
                {
                    Assert.Equal(0, RevisionRequests);
                    RevisionRequests++;
                    var review = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(
                        AppContext.BaseDirectory, "Fixtures", "prism-breaker-refine-review.json")))!;
                    if (RefineWithoutCandidate)
                    {
                        review["disposition"] = "refine";
                        review["nextCandidate"] = null;
                    }
                    return review;
                }
                if (ReplaysBreakout)
                {
                    var facts = data.GetProperty("evidence").EnumerateArray().ToArray();
                    var provider = Array.FindIndex(facts, f => f.GetProperty("Detail").GetString()!.Contains("2D web packaging recipe"));
                    Assert.InRange(provider, 0, facts.Length - 1);
                    if (RecipeCorrections == 0)
                    {
                        RecipeCorrections++;
                        var recorded = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(
                            AppContext.BaseDirectory, "Fixtures", "prism-break-recipe-review.json")))!;
                        // Rebind the live evidence positions to this board's container and provider selection.
                        var container = Array.FindIndex(facts, f => f.GetProperty("Kind").GetString() == "container");
                        recorded["actions"]![0]!["evidenceIndexes"] = new System.Text.Json.Nodes.JsonArray(container, provider);
                        return recorded;
                    }
                    Assert.Equal("build-environment", facts[provider].GetProperty("Kind").GetString());
                    Assert.Contains(Stack.Rationale, data.GetProperty("technicalConstraints").GetRawText());
                    Assert.Contains(messages, text => text.Contains("provider selection", StringComparison.OrdinalIgnoreCase)
                        && text.Contains("correction", StringComparison.OrdinalIgnoreCase));
                }
                Admissions++;
            }
            if (candidates.GetArrayLength() == 0)
                return new { disposition = "conclude", nextGoal = "Deliver the only planned Story", nextCandidate = (int?)null,
                    draftAhead = false, keepDraft = false, revisitWhen = "When engineering finishes", feelSummary = "The increment awaits execution.",
                    actions = new[] { new { kind = "proceed", rationale = "The current Story covers the brief.", capability = (string?)null, evidenceIndexes = new[] { 0 } } } };
            return new { disposition = "select", nextGoal = "Ship the first playable interaction", nextCandidate = 0, draftAhead = false, keepDraft = true, singleDeliverableReason = IndivisibleReason,
                revisitWhen = "After the scene runs", feelSummary = "The first interaction is planned.", actions = new[] { new { kind = "proceed",
                    rationale = "The staffed team can deliver this slice.", capability = (string?)null, evidenceIndexes = new[] { 0 } } }, additionalDraftCandidates = Array.Empty<int>() };
        }

        Assert.Equal("game-technical-director", actor.Role);
        if (CheckpointStack.Matches(request))
        {
            Assert.Contains(messages, text => text.Contains(Pitch));
            Assert.Contains(messages, text => text.Contains("on merit"));
            StackChoices++;
            return Stack;
        }
        string[] none = [];
        var page = ++technicalPage;
        if (RevisionRequests == 1 && page is 7 or 8)
        {
            Assert.Contains(messages, text => text.Contains(SplitDelivery ? "Separate independently testable" : "Remove the setup criterion"));
            Assert.Contains(messages, text => text.Contains("Apply the Producer's corrections directly to the returned task briefs"));
            page -= 2; // Reuse the frozen Story and answer only the revised task index and brief.
        }
        return page switch
        {
            2 => new { commitments = (Startup?.Goals ?? [Outcome]).Select(statement => new { statement, sourceIndexes = new[] { 0 }, kind = "Outcome" }) },
            3 => new { epics = new[] { new { title = "Playable interaction", outcome = Outcome, acceptanceCriteria = new[] { Outcome },
                sourceIndexes = Enumerable.Range(0, Startup?.Goals.Length ?? 1).ToArray(), prerequisiteIndexes = Array.Empty<int>() } }, feasibilityFindings = none,
                technicalConstraints = Startup is null ? none : [.. Startup.Constraints[..6], Startup.Goals[2]], openFeasibilityDecisions = none },
            4 => new { stories = new[] { new { title = SplitDelivery ? SetupTitle : "Playable opening scene", outcome = Outcome, acceptanceCriteria = StoryCriteria, epicCriterionIndexes = new[] { 0 }, prerequisiteIndexes = Array.Empty<int>() } } },
            5 when SplitDelivery && RevisionRequests > 0 => SplitTasks(messages),
            6 when SplitDelivery && RevisionRequests > 0 => new { items = new[] { 1, 2, 3 }.Select(i => new {
                proposalKey = $"roadmap-epic-1.story-1.task-{i}", description = Brief }) },
            5 => new { setupTaskIndex = 0, tasks = new[] { new { title = SetupTitle, description = Brief, workItemTypeKey = ReplaysBreakout ? "video-game.research-spike.v1" : "video-game.task.v1",
                accountableRoleKey = "game-engineer", requiredSpecializationKeys = none, preferredSpecializationKeys = none, storyCriterionIndexes = Enumerable.Range(0, StoryCriteria.Length + 1).ToArray(),
                additionalCriteria = SupersededCriterion is not null && RevisionRequests == 0 ? new[] { SupersededCriterion } : none,
                technicalConstraintIndexes = Startup is null ? Array.Empty<int>() : Enumerable.Range(0, 6).ToArray(),
                browserVerification = Startup is null ? "none" : "canvas", interactionCriteria = Array.Empty<int>(), earlierTaskIndexes = Array.Empty<int>(), priorTaskIndexes = Array.Empty<int>() } } },
            6 => new { items = new[] { new { proposalKey = "roadmap-epic-1.story-1.task-1", description = Startup?.Requirements.Single() ?? Brief } } },
            _ => throw new InvalidOperationException("Unexpected technical generation or correction: " + messages.Last()[..Math.Min(300, messages.Last().Length)])
        };
    }
}

