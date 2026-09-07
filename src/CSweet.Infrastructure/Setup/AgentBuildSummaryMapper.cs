using System.Text.RegularExpressions;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Setup;

internal static partial class AgentBuildSummaryMapper
{
    public static AgentBuildSummaryResponse? Create(AgentBuildJob? build)
    {
        if (build is null) return null;
        var steps = AgentBuildStepStore.Read(build);
        var failure = build.FailureMessage;
        var assignment = build.ExecutionAssignments.OrderByDescending(x => x.QueuedAt).FirstOrDefault();
        var diagnostic = assignment?.ResultLogExcerpt;
        if (build.Status == AgentBuildStatus.Failed && assignment?.Status == ExecutionAssignmentStatus.Failed &&
            !string.IsNullOrWhiteSpace(diagnostic))
        {
            // Enrich the user-facing response only. Guest diagnostics must not become
            // infrastructure log messages or broker protocol errors.
            var errors = diagnostic.Split('\n')
                .Where(line => !line.Contains("warning ", StringComparison.OrdinalIgnoreCase))
                .Select(line => BuildError().Match(line))
                .Where(match => match.Success)
                .Select(match => new { Code = match.Groups["code"].Value, Text = Clean(match.Value) })
                .DistinctBy(error => error.Text)
                .Take(3)
                .ToArray();
            if (errors.Length > 0)
            {
                failure = Clean(string.Join("\n", errors.Select(error => error.Text)));
                var activeStep = steps.LastOrDefault(step => step.Status is
                    AgentBuildStepStatuses.InProgress or AgentBuildStepStatuses.Failed);
                var stepKey = activeStep?.Key ?? AgentBuildStepKeys.Isolate;
                // Older fleet failures were always attributed to VM preparation.
                // NuGet and compiler diagnostics prove the guest got further than that.
                if (stepKey is AgentBuildStepKeys.Queued or AgentBuildStepKeys.Source or AgentBuildStepKeys.Isolate)
                {
                    if (errors[0].Code.StartsWith("NU", StringComparison.Ordinal))
                        stepKey = AgentBuildStepKeys.Restore;
                    else if (errors[0].Code.StartsWith("CS", StringComparison.Ordinal))
                        stepKey = AgentBuildStepKeys.Publish;
                }
                var failedIndex = steps.ToList().FindIndex(step => step.Key == stepKey);
                steps = steps.Select((step, index) => index == failedIndex
                    ? step with { Status = AgentBuildStepStatuses.Failed, Detail = null, Error = failure }
                    : index < failedIndex && step.Status != AgentBuildStepStatuses.Succeeded
                        ? step with { Status = AgentBuildStepStatuses.Succeeded, Detail = null, Error = null }
                        : step).ToArray();
            }
        }
        return new AgentBuildSummaryResponse(
            build.Id, build.Status.ToString(), build.Attempt, build.QueuedAt, build.StartedAt, build.CompletedAt,
            !string.IsNullOrWhiteSpace(build.LogPath) || !string.IsNullOrWhiteSpace(diagnostic), failure, steps);
    }

    private static string Clean(string value) => new(value
        .Where(character => !char.IsControl(character) || character is '\n' or '\t')
        .Take(1500).ToArray());

    [GeneratedRegex(@"\b(?:error\s+)?(?<code>(?:NU|CS|MSB|NETSDK)\d{4,5}):[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex BuildError();
}
