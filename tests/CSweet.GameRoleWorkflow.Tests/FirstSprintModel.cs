using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.GameRoleWorkflow.Tests;

// Only model judgments are scripted. In particular the task model never supplies an engine,
// setup criterion, lockfile, ticket identity, sprint or assignee.
internal sealed class FirstSprintModel(bool dependencies)
{
    public string Pitch => dependencies
        ? "Make a browser game where the player deflects moving obstacles using a simulated spring."
        : "Make a tiny browser puzzle where a player moves a tile one square at a time.";
    public string Outcome => dependencies ? "The spring deflects the obstacle." : "The selected tile moves one square.";
    public string Engine => dependencies ? "Matter.js" : "DOM";
    public string Language => dependencies ? "TypeScript" : "JavaScript";
    public string Setup => dependencies ? "Install dependencies and launch the spring scene." : "Open the puzzle page directly.";
    public string Rationale => dependencies ? "A physics solver supports the spring response." : "Native DOM input covers a single tile with minimal setup.";
    public string[] Libraries => dependencies ? ["matter-js"] : [];
    public string? Lockfile => dependencies ? "package-lock.json" : null;
    public int StackChoices { get; private set; }
    public int Admissions { get; private set; }
    public int Estimates { get; private set; }
    private int technicalPage;
    private const string Brief = """
        Outcome: Implement one playable interaction and leave scoring outside this first increment.
        Context: Locate the scene entry point and input handler in the project or create them for the first scene.
        Pseudocode: read input, advance the scene by one step, then render the updated position.
        Assumptions: The input represents one player action. Keep the scene resettable for verification.
        Verification: Launch from a clean checkout, exercise the action once and confirm the position changes once.
        """;

    public object Answer(AgentCoordinationParticipant actor, JsonElement request)
    {
        var messages = request.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString()!).ToArray();
        if (actor.Role == "game-engineer")
        {
            Estimates++;
            Assert.Contains(messages, text => text.Contains(Setup));
            return new { estimates = new[] { new { points = 2, confidence = "high", rationale = "One small scene with one input." } },
                capacityAssessment = new { offeredPoints = 3, rationale = "There is room for the first scene." }, assumptions = Array.Empty<string>(), blockers = Array.Empty<string>() };
        }

        if (actor.Role == "game-producer")
        {
            var data = JsonDocument.Parse(messages[1]).RootElement;
            if (data.TryGetProperty("existingObligations", out var existing))
                return new { commitments = new[] { new { statement = Outcome, sources = new[] { 0 }, evidenceIndexes = new[] { 0 },
                    proposedIndexes = new[] { 0 }, existingObligationIndex = existing.GetArrayLength() == 0 ? (int?)null : 0 } }, gaps = Array.Empty<string>() };
            Assert.True(data.TryGetProperty("candidates", out var candidates),
                "Unscripted Producer judgment: " + messages[0][..Math.Min(320, messages[0].Length)] + " " + string.Join(", ", data.EnumerateObject().Select(p => p.Name)));
            if (data.GetProperty("provisional").ValueKind == JsonValueKind.Array)
            {
                Admissions++;
                Assert.Contains(Setup, data.GetProperty("provisional").GetRawText());
            }
            if (candidates.GetArrayLength() == 0)
                return new { disposition = "conclude", nextGoal = "Deliver the only planned Story", nextCandidate = (int?)null,
                    draftAhead = false, keepDraft = false, revisitWhen = "When engineering finishes", feelSummary = "The increment awaits execution.",
                    actions = new[] { new { kind = "proceed", rationale = "The current Story covers the brief.", capability = (string?)null, evidenceIndexes = new[] { 0 } } } };
            return new { disposition = "select", nextGoal = "Ship the first playable interaction", nextCandidate = 0, draftAhead = false, keepDraft = true,
                revisitWhen = "After the scene runs", feelSummary = "The first interaction is planned.", actions = new[] { new { kind = "proceed",
                    rationale = "The staffed team can deliver this slice.", capability = (string?)null, evidenceIndexes = new[] { 0 } } }, additionalDraftCandidates = Array.Empty<int>() };
        }

        Assert.Equal("game-technical-director", actor.Role);
        if (technicalPage++ == 0)
        {
            Assert.Contains(messages, text => text.Contains(Pitch));
            Assert.Contains(messages, text => text.Contains("on merit"));
            StackChoices++;
            return new { engine = Engine, language = Language, libraries = Libraries, rationale = Rationale, setup = Setup, lockfile = Lockfile };
        }
        string[] none = [];
        return technicalPage switch
        {
            2 => new { commitments = new[] { new { statement = Outcome, sourceIndexes = new[] { 0 }, kind = "Outcome" } } },
            3 => new { epics = new[] { new { title = "Playable interaction", outcome = Outcome, acceptanceCriteria = new[] { Outcome },
                sourceIndexes = new[] { 0 }, prerequisiteIndexes = Array.Empty<int>() } }, feasibilityFindings = none, technicalConstraints = none, openFeasibilityDecisions = none },
            4 => new { stories = new[] { new { title = "Playable opening scene", outcome = Outcome, acceptanceCriteria = none, epicCriterionIndexes = new[] { 0 }, prerequisiteIndexes = Array.Empty<int>() } } },
            5 => new { setupTaskIndex = 0, tasks = new[] { new { title = "Implement the opening scene", description = Brief, workItemTypeKey = "video-game.task.v1",
                accountableRoleKey = "game-engineer", requiredSpecializationKeys = none, preferredSpecializationKeys = none, storyCriterionIndexes = new[] { 0 },
                additionalCriteria = none, browserVerification = "none", interactionCriteria = Array.Empty<int>(), earlierTaskIndexes = Array.Empty<int>(), priorTaskIndexes = Array.Empty<int>() } } },
            6 => new { items = new[] { new { proposalKey = "roadmap-epic-1.story-1.task-1", description = Brief } } },
            _ => throw new InvalidOperationException("Unexpected technical generation or correction: " + messages.Last()[..Math.Min(300, messages.Last().Length)])
        };
    }
}

