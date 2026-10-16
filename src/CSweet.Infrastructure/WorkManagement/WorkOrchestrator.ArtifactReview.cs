using System.Text.Json;
using CSweet.Domain.WorkManagement;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator
{
    internal static IReadOnlyList<Shared.WorkExecutionOutcomeV1> AssignmentOutcomes(WorkStageExecution stage)
    {
        var item = stage.ItemExecution!;
        var delivery = JsonSerializer.Deserialize<Shared.WorkItemDeliverySpecification>(
            item.WorkItem!.DeliverySpecificationJson ?? "null", JsonOptions);
        if (stage.StageKey == "quality" && delivery?.DeliveryKind == "Artifact")
        {
            // History is not a candidate selector. Bind this QA cycle to the latest attempt
            // of its exact producer, including failures (never fall back to earlier success).
            var author = item.Stages.SingleOrDefault(x => x.Traversal == stage.Traversal &&
                x.StageKey is "development" or "specialist-execution");
            var attempt = author?.Attempts.OrderByDescending(x => x.Attempt).FirstOrDefault();
            var outcome = attempt?.ResultJson is { } result
                ? JsonSerializer.Deserialize<Shared.WorkExecutionOutcomeV1>(result, JsonOptions) : null;
            if (item.Traversal != stage.Traversal || item.CurrentStageKey != stage.StageKey ||
                author?.Status != WorkStageExecutionStatus.Completed ||
                attempt?.Status != WorkExecutionAttemptStatus.Completed || outcome is null ||
                outcome.StageExecutionId != author.Id || outcome.AttemptId != attempt.Id ||
                outcome.Disposition != Shared.WorkExecutionDispositions.Completed ||
                outcome.OutcomeCode is not ("completed" or "artifact-delivered") ||
                !Shared.WorkArtifactDelivery.TryRead(outcome.Output, out _))
                throw new InvalidOperationException("The current author attempt must deliver an exact artifact before QA dispatch.");
            return [outcome];
        }
        return item.Stages.OrderBy(x => x.Traversal).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .SelectMany(x => x.Attempts.OrderBy(a => a.Attempt))
            .Where(x => !string.IsNullOrWhiteSpace(x.ResultJson))
            .Select(x => JsonSerializer.Deserialize<Shared.WorkExecutionOutcomeV1>(x.ResultJson!, JsonOptions)!).ToArray();
    }
}
