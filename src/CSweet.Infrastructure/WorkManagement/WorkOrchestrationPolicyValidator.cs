using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public static partial class WorkOrchestrationPolicyValidator
{
    private static readonly HashSet<string> StageTypes =
    [
        WorkOrchestrationStageTypes.Queue,
        WorkOrchestrationStageTypes.AgentExecution,
        WorkOrchestrationStageTypes.ManualWork,
        WorkOrchestrationStageTypes.ManagerApproval,
        WorkOrchestrationStageTypes.TrustedPlatformAction,
        WorkOrchestrationStageTypes.Terminal
    ];

    public static IReadOnlyList<WorkOrchestrationValidationError> Validate(
        string initialStageKey,
        string mergeMode,
        WorkOrchestrationConcurrencyLimits concurrency,
        IReadOnlyList<WorkOrchestrationStageDefinition> stages,
        IReadOnlyList<WorkOrchestrationTransitionDefinition> transitions,
        IReadOnlySet<Guid> boardColumnIds)
    {
        var errors = new List<WorkOrchestrationValidationError>();
        if (mergeMode is not (WorkMergeModes.ManagerApproval or WorkMergeModes.Automatic))
            errors.Add(Error("policy.merge_mode", "Merge mode must be ManagerApproval or Automatic."));
        if (concurrency.Global < 1 || concurrency.Organization < 1 || concurrency.Board < 1 ||
            concurrency.DefaultStage < 1 || concurrency.DefaultAssignee < 1)
            errors.Add(Error("policy.concurrency", "All concurrency limits must be positive."));
        if (stages.Count == 0)
            errors.Add(Error("policy.stages_required", "At least one stage is required."));

        var duplicates = stages.GroupBy(x => x.Key, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => x.Key);
        foreach (var key in duplicates)
            errors.Add(Error("stage.duplicate", $"Stage key '{key}' is duplicated.", stageKey: key));

        var stageByKey = stages.GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            if (!TokenRegex().IsMatch(stage.Key))
                errors.Add(Error("stage.key", $"Stage key '{stage.Key}' is invalid.", stageKey: stage.Key));
            if (!StageTypes.Contains(stage.StageType))
                errors.Add(Error("stage.type", $"Stage '{stage.Key}' has an invalid type.", stageKey: stage.Key));
            if (stage.ColumnId.HasValue && !boardColumnIds.Contains(stage.ColumnId.Value))
                errors.Add(Error("stage.column", $"Stage '{stage.Key}' references a column outside the board.", stageKey: stage.Key));
            if (stage.TimeoutSeconds is < 1 or > 86400)
                errors.Add(Error("stage.timeout", $"Stage '{stage.Key}' timeout must be between 1 and 86400 seconds.", stageKey: stage.Key));
            if (stage.ConcurrencyLimit is < 1)
                errors.Add(Error("stage.concurrency", $"Stage '{stage.Key}' concurrency must be positive.", stageKey: stage.Key));
            if (stage.RetryPolicy.MaximumAttempts is < 1 or > 10 ||
                stage.RetryPolicy.InitialDelaySeconds < 1 ||
                stage.RetryPolicy.MaximumDelaySeconds < stage.RetryPolicy.InitialDelaySeconds)
                errors.Add(Error("stage.retry", $"Stage '{stage.Key}' retry policy is invalid.", stageKey: stage.Key));
            ValidateJson(stage.InputSchemaJson, "input", stage.Key, errors);
            ValidateJson(stage.OutputSchemaJson, "output", stage.Key, errors);
            if (stage.StageType == WorkOrchestrationStageTypes.TrustedPlatformAction &&
                string.IsNullOrWhiteSpace(stage.PlatformAction))
                errors.Add(Error("stage.platform_action", $"Stage '{stage.Key}' requires a platform action.", stageKey: stage.Key));
        }

        if (!stageByKey.ContainsKey(initialStageKey))
            errors.Add(Error("policy.initial_stage", "The initial stage does not exist."));
        if (!stages.Any(x => x.StageType == WorkOrchestrationStageTypes.Terminal))
            errors.Add(Error("policy.terminal", "At least one terminal stage is required."));

        var transitionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transition in transitions)
        {
            var unique = $"{transition.FromStageKey}\n{transition.OutcomeCode}";
            if (!transitionKeys.Add(unique))
                errors.Add(Error("transition.duplicate", $"Stage '{transition.FromStageKey}' has duplicate outcome '{transition.OutcomeCode}'.", stageKey: transition.FromStageKey));
            if (!TokenRegex().IsMatch(transition.OutcomeCode))
                errors.Add(Error("transition.outcome", $"Outcome '{transition.OutcomeCode}' is invalid.", stageKey: transition.FromStageKey));
            if (!stageByKey.ContainsKey(transition.FromStageKey) || !stageByKey.ContainsKey(transition.ToStageKey))
                errors.Add(Error("transition.stage", "A transition references an unknown stage.", stageKey: transition.FromStageKey));
            if (transition.MaximumTraversals is < 1 or > 10)
                errors.Add(Error("transition.traversal", "Maximum traversals must be between 1 and 10.", stageKey: transition.FromStageKey));
        }

        if (errors.Count > 0) return errors;
        var adjacency = transitions.GroupBy(x => x.FromStageKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(t => t.ToStageKey).ToList(), StringComparer.Ordinal);
        var reachable = Reachable(initialStageKey, adjacency);
        foreach (var stage in stages.Where(x => !reachable.Contains(x.Key)))
            errors.Add(Error("stage.unreachable", $"Stage '{stage.Key}' is unreachable.", stageKey: stage.Key));
        foreach (var stage in stages.Where(x => x.StageType != WorkOrchestrationStageTypes.Terminal))
        {
            if (!CanReachTerminal(stage.Key, stageByKey, adjacency))
                errors.Add(Error("stage.no_terminal", $"Stage '{stage.Key}' cannot reach a terminal stage.", stageKey: stage.Key));
        }
        foreach (var transition in transitions)
        {
            if (CanReach(transition.ToStageKey, transition.FromStageKey, adjacency) &&
                !transition.MaximumTraversals.HasValue &&
                !transitions.Any(candidate => candidate.MaximumTraversals.HasValue &&
                    Reachable(transition.FromStageKey, adjacency).Contains(candidate.FromStageKey) &&
                    CanReach(candidate.ToStageKey, transition.FromStageKey, adjacency)))
                errors.Add(Error("transition.unbounded_cycle", $"Cyclic transition '{transition.FromStageKey}'/'{transition.OutcomeCode}' must declare maximum traversals.", stageKey: transition.FromStageKey));
        }
        return errors;
    }

    private static HashSet<string> Reachable(string start, Dictionary<string, List<string>> adjacency)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(); stack.Push(start);
        while (stack.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            if (adjacency.TryGetValue(current, out var next))
                foreach (var item in next) stack.Push(item);
        }
        return seen;
    }

    private static bool CanReachTerminal(
        string start,
        IReadOnlyDictionary<string, WorkOrchestrationStageDefinition> stages,
        Dictionary<string, List<string>> adjacency) =>
        Reachable(start, adjacency).Any(x => stages[x].StageType == WorkOrchestrationStageTypes.Terminal);

    private static bool CanReach(string start, string target, Dictionary<string, List<string>> adjacency) =>
        Reachable(start, adjacency).Contains(target);

    private static void ValidateJson(
        string value, string label, string stageKey,
        ICollection<WorkOrchestrationValidationError> errors)
    {
        try { using var _ = JsonDocument.Parse(value); }
        catch (JsonException exception)
        {
            errors.Add(Error("stage.schema", $"Stage '{stageKey}' {label} schema is invalid JSON: {exception.Message}", stageKey: stageKey));
        }
    }

    private static WorkOrchestrationValidationError Error(
        string code, string message, Guid? itemId = null, string? stageKey = null) =>
        new(code, message, itemId, stageKey);

    [GeneratedRegex("^[a-z][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
