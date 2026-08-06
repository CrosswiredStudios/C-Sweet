using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;

namespace CSweet.Infrastructure.Setup;

internal static class AgentBuildStepStore
{
    private static readonly (string Key, string Label, string PendingDetail)[] Catalog =
    [
        (AgentBuildStepKeys.Queued, "Build queued", "Waiting for an available build worker."),
        (AgentBuildStepKeys.Source, "Prepare source", "Fetch and validate the approved source."),
        (AgentBuildStepKeys.Isolate, "Prepare build environment", "Start a disposable hardware-isolated builder VM."),
        (AgentBuildStepKeys.Restore, "Restore dependencies", "Resolve the project's package dependencies."),
        (AgentBuildStepKeys.Publish, "Compile and publish", "Compile the agent and create its release output."),
        (AgentBuildStepKeys.Package, "Verify package", "Validate and seal the immutable agent package.")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string CreateInitialJson(DateTimeOffset queuedAt) =>
        Serialize(CreateInitial(queuedAt));

    public static IReadOnlyList<AgentBuildStepResponse> Read(AgentBuildJob job)
    {
        var hasPersistedSteps = !string.IsNullOrWhiteSpace(job.StepsJson) && job.StepsJson.Trim() != "[]";
        var steps = Deserialize(job.StepsJson, job.QueuedAt);
        if (!hasPersistedSteps)
        {
            ApplyLegacyStatus(steps, job);
        }
        if (job.Status == AgentBuildStatus.Succeeded)
        {
            steps = steps.Select(step => step with
            {
                Status = AgentBuildStepStatuses.Succeeded,
                StartedAt = step.StartedAt ?? job.StartedAt ?? job.QueuedAt,
                CompletedAt = step.CompletedAt ?? job.CompletedAt
            }).ToList();
        }
        return steps;
    }

    private static void ApplyLegacyStatus(List<AgentBuildStepResponse> steps, AgentBuildJob job)
    {
        if (job.Status == AgentBuildStatus.Queued)
        {
            return;
        }

        var queued = steps[0];
        steps[0] = queued with
        {
            Status = AgentBuildStepStatuses.Succeeded,
            CompletedAt = job.StartedAt ?? job.CompletedAt ?? job.QueuedAt
        };
        if (job.Status == AgentBuildStatus.Cloning)
        {
            steps[1] = steps[1] with
            {
                Status = AgentBuildStepStatuses.InProgress,
                StartedAt = job.StartedAt
            };
            return;
        }

        var sourceCompletedAt = job.StartedAt ?? job.CompletedAt ?? job.QueuedAt;
        steps[1] = steps[1] with
        {
            Status = AgentBuildStepStatuses.Succeeded,
            StartedAt = job.StartedAt,
            CompletedAt = sourceCompletedAt
        };
        if (job.Status == AgentBuildStatus.Building)
        {
            steps[2] = steps[2] with
            {
                Status = AgentBuildStepStatuses.InProgress,
                StartedAt = sourceCompletedAt
            };
            return;
        }

        if (job.Status is AgentBuildStatus.Failed or AgentBuildStatus.Cancelled)
        {
            var targetIndex = string.IsNullOrWhiteSpace(job.LogPath) ? 1 : 2;
            steps[targetIndex] = steps[targetIndex] with
            {
                Status = job.Status == AgentBuildStatus.Failed
                    ? AgentBuildStepStatuses.Failed
                    : AgentBuildStepStatuses.Cancelled,
                StartedAt = steps[targetIndex].StartedAt ?? sourceCompletedAt,
                CompletedAt = job.CompletedAt,
                Error = job.FailureMessage
            };
        }
    }

    public static async Task ApplyAsync(
        CSweetDbContext dbContext,
        AgentBuildJob job,
        AgentBuildProgressUpdate update,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var steps = Deserialize(job.StepsJson, job.QueuedAt);
        var index = steps.FindIndex(step => string.Equals(step.Key, update.StepKey, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        var current = steps[index];
        var startedAt = update.Status == AgentBuildStepStatuses.InProgress
            ? current.StartedAt ?? now
            : current.StartedAt;
        var completedAt = update.Status is AgentBuildStepStatuses.Succeeded or AgentBuildStepStatuses.Failed or AgentBuildStepStatuses.Cancelled
            ? now
            : current.CompletedAt;
        steps[index] = current with
        {
            Status = update.Status,
            Detail = update.Detail ?? current.Detail,
            Error = update.Error,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
        job.StepsJson = Serialize(steps);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public static async Task CompleteRemainingAsync(
        CSweetDbContext dbContext,
        AgentBuildJob job,
        CancellationToken cancellationToken)
    {
        var steps = Deserialize(job.StepsJson, job.QueuedAt);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index].Status is AgentBuildStepStatuses.Succeeded or AgentBuildStepStatuses.Failed)
            {
                continue;
            }
            steps[index] = steps[index] with
            {
                Status = AgentBuildStepStatuses.Succeeded,
                StartedAt = steps[index].StartedAt ?? now,
                CompletedAt = now,
                Error = null
            };
        }
        job.StepsJson = Serialize(steps);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public static async Task FailCurrentAsync(
        CSweetDbContext dbContext,
        AgentBuildJob job,
        string error,
        string? preferredStepKey = null)
    {
        var steps = Deserialize(job.StepsJson, job.QueuedAt);
        var step = !string.IsNullOrWhiteSpace(preferredStepKey)
            ? steps.FirstOrDefault(candidate => candidate.Key == preferredStepKey)
            : null;
        step ??= steps.LastOrDefault(candidate => candidate.Status == AgentBuildStepStatuses.InProgress);
        step ??= steps.FirstOrDefault(candidate => candidate.Status == AgentBuildStepStatuses.Pending);
        if (step is null || step.Status == AgentBuildStepStatuses.Failed)
        {
            return;
        }
        await ApplyAsync(
            dbContext,
            job,
            new AgentBuildProgressUpdate(step.Key, AgentBuildStepStatuses.Failed, Error: error),
            CancellationToken.None);
    }

    private static List<AgentBuildStepResponse> Deserialize(string? json, DateTimeOffset queuedAt)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<AgentBuildStepResponse>>(json, JsonOptions);
                if (parsed is { Count: > 0 })
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
            }
        }
        return CreateInitial(queuedAt);
    }

    private static List<AgentBuildStepResponse> CreateInitial(DateTimeOffset queuedAt) =>
        Catalog.Select((definition, index) => new AgentBuildStepResponse(
            definition.Key,
            definition.Label,
            index == 0 ? AgentBuildStepStatuses.InProgress : AgentBuildStepStatuses.Pending,
            definition.PendingDetail,
            null,
            index == 0 ? queuedAt : null,
            null)).ToList();

    private static string Serialize(IReadOnlyList<AgentBuildStepResponse> steps) =>
        JsonSerializer.Serialize(steps, JsonOptions);
}

internal sealed class PersistedAgentBuildProgressReporter(
    CSweetDbContext dbContext,
    AgentBuildJob job) : IAgentBuildProgressReporter
{
    public Task ReportAsync(
        AgentBuildProgressUpdate update,
        CancellationToken cancellationToken = default) =>
        AgentBuildStepStore.ApplyAsync(dbContext, job, update, cancellationToken);
}
