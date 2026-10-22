using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.WorkManagement;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkOrchestrator
{
    private sealed record IntegratedResubmissionOutput(bool ResubmitsIntegratedCandidate, Guid RepositoryId,
        string CommitSha, IReadOnlyList<string>? ChangedFiles, IReadOnlyList<ResubmissionValidation>? Validations);
    private sealed record ResubmissionValidation(string Command, bool Succeeded, int ExitCode);

    internal async Task<IntegratedCandidateResponse> ValidateIntegratedResubmissionAsync(WorkStageExecution stage,
        Shared.WorkExecutionOutcomeV1 outcome, CancellationToken ct)
    {
        var output = outcome.Output.Deserialize<IntegratedResubmissionOutput>(JsonOptions)!;
        if (outcome.Disposition != Shared.WorkExecutionDispositions.Completed || outcome.OutcomeCode is not ("completed" or "code-published") ||
            stage.StageKey is not ("specialist-execution" or "development") || !output.ResubmitsIntegratedCandidate ||
            output.ChangedFiles is not { Count: 0 } || output.Validations is not { Count: > 0 } ||
            output.Validations.Any(validation => !validation.Succeeded || validation.ExitCode != 0 || string.IsNullOrWhiteSpace(validation.Command)))
            throw new InvalidOperationException("Integrated candidate resubmission requires unchanged author rework with successful current validation.");
        var candidate = await IntegratedCandidateResubmission.RequireAsync(db, repositoryInputs, stage.ItemExecution!.WorkItem!, ct);
        if (output.RepositoryId != candidate.RepositoryId || output.CommitSha != candidate.IntegratedCommitSha ||
            outcome.Evidence.Count(evidence => evidence.Kind == "commit") != 1 ||
            outcome.Evidence.Single(evidence => evidence.Kind == "commit").Value != candidate.IntegratedCommitSha)
            throw new InvalidOperationException("The unchanged rework does not identify the exact recorded integrated candidate.");
        return candidate;
    }
}
