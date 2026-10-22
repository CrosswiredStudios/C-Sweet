using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Core;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class CurrentActivityService
{
    private sealed record ComputeActivity(string State, string Action, DateTimeOffset ObservedAt, CurrentActivityCompute? Progress);
    private sealed record ComputeWaitBinding(string SourceCommitSha, string BuildSha256, Guid EnvironmentId,
        IReadOnlyList<Guid> OperationIds, DateTimeOffset RetryAt);

    private async Task<ComputeActivity?> ReadComputeAsync(Guid organization, WorkTask ticket, WorkStageExecution stage,
        DateTimeOffset now, CancellationToken token)
    {
        var attempts = await db.WorkExecutionAttempts.AsNoTracking().Where(x => x.StageExecutionId == stage.Id)
            .OrderByDescending(x => x.Attempt).Take(128).ToListAsync(token);
        var latest = attempts.FirstOrDefault(x => x.Status == WorkExecutionAttemptStatus.Completed);
        var binding = ReadBinding(latest, stage.Id);
        if (binding is null) return null;
        var environment = await db.ComputeEnvironments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == binding.EnvironmentId &&
            x.OrganizationId == organization && x.InstallationId == stage.AgentInstallationId &&
            x.WorkstreamId == ticket.Board!.WorkstreamId && x.TeardownConfirmedAt == null, token);
        if (environment is null || !await db.SourceControlWorkspaces.AsNoTracking().AnyAsync(x => x.OrganizationId == organization &&
            x.WorkItemId == ticket.Id && x.AgentInstallationId == stage.AgentInstallationId &&
            x.AssignmentRevision == ticket.AssignmentRevision && x.BaseCommitSha == binding.SourceCommitSha, token)) return null;
        var ids = attempts.Select(attempt => ReadBinding(attempt, stage.Id)).Where(previous => previous is not null &&
            previous.EnvironmentId == binding.EnvironmentId && previous.SourceCommitSha == binding.SourceCommitSha &&
            previous.BuildSha256 == binding.BuildSha256).SelectMany(previous => previous!.OperationIds).Distinct().ToArray();
        var operations = await db.ComputeOperations.AsNoTracking().Where(x => ids.Contains(x.Id) &&
            x.OrganizationId == organization && x.InstallationId == stage.AgentInstallationId &&
            x.EnvironmentId == environment.Id && x.Generation <= environment.Generation).ToListAsync(token);
        var current = binding.OperationIds.Count == 0 ? null : operations.SingleOrDefault(x => x.Id == binding.OperationIds[0]);
        if (binding.OperationIds.Count != 0 && current is null) return null;
        var phase = IsBrowserTest(current) ? "Browser testing" : operations.Any(operation => IsBrowserTest(operation) && Succeeded(operation)) ?
            "Collecting browser-test evidence" : stage.StageKey == "quality" ? "Browser test preparation or transfer" : "Compute workload";
        var observed = current?.CompletedAt ?? current?.CreatedAt ?? latest!.CompletedAt ?? stage.UpdatedAt;
        var status = current?.Status ?? environment.State.ToString();
        var state = "Waiting";
        var action = status switch
        {
            "Pending" => "Waiting for compute dispatch; the agent will resume automatically",
            "Dispatching" => "Waiting for compute execution and its result; the agent will resume automatically",
            "Reconciling" => "Waiting for compute to confirm the operation result",
            "Completed" => "Compute operation completed; waiting for the agent to resume",
            "Ready" => "Compute is ready; waiting for the agent to resume",
            _ => "Waiting for compute readiness; the agent will resume automatically"
        };
        if (current is not null && (current.Generation != environment.Generation ||
            current.Status is not ("Pending" or "Dispatching" or "Reconciling" or "Completed") ||
            current.Status == "Completed" && !Succeeded(current)) ||
            environment.State is ComputeLifecycleState.Failed or ComputeLifecycleState.Destroyed or ComputeLifecycleState.Stopped)
        {
            state = "Needs attention";
            action = "Compute needs recovery; no successful test result is implied";
        }
        var paused = stage.ItemExecution!.SprintExecution!.Status == WorkSprintExecutionStatus.Paused;
        if (paused) action = "Workflow is paused; automatic agent continuation is paused";
        else if (state == "Waiting" && stage.RetryAt <= now && current?.Status is "Completed" or null)
            action = "Compute is ready for continuation; awaiting the next workflow dispatch";
        return new(state, action, observed, new(phase, status, operations.Count(Succeeded), observed,
            paused ? null : stage.RetryAt, binding.SourceCommitSha));
    }

    private static ComputeWaitBinding? ReadBinding(WorkExecutionAttempt? attempt, Guid stageId)
    {
        if (attempt is not { Status: WorkExecutionAttemptStatus.Completed, ResultJson: not null }) return null;
        try
        {
            using var document = JsonDocument.Parse(attempt.ResultJson);
            var root = document.RootElement;
            if (root.GetProperty("stageExecutionId").GetGuid() != stageId || root.GetProperty("attemptId").GetGuid() != attempt.Id ||
                root.GetProperty("outcomeCode").GetString() != "awaiting-compute" ||
                root.GetProperty("disposition").GetString() != CSweet.WorkManagement.Contracts.WorkExecutionDispositions.Completed) return null;
            var binding = root.GetProperty("output").Deserialize<ComputeWaitBinding>(ComputeProtocol.Json);
            return binding is { EnvironmentId: var environment, OperationIds.Count: <= 1 } && environment != Guid.Empty &&
                binding.OperationIds.All(id => id != Guid.Empty) &&
                binding.SourceCommitSha is { Length: 40 or 64 } && binding.SourceCommitSha.All(Uri.IsHexDigit) &&
                binding.BuildSha256 is { Length: 64 } && binding.BuildSha256.All(Uri.IsHexDigit) ? binding : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return null; }
    }

    private static bool Succeeded(ComputeOperation operation)
    {
        if (operation.Status != "Completed" || operation.FailureCode is not null) return false;
        if (operation.WorkloadJson is null) return true;
        try
        {
            return operation.ResultJson is not null && JsonSerializer.Deserialize<ComputeWorkloadResult>(operation.ResultJson, ComputeProtocol.Json) is
                { ErrorCode: null } result && (result.Command is null || result.Command is { ExitCode: 0, TimedOut: false, ErrorCode: null });
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }

    private static bool IsBrowserTest(ComputeOperation? operation)
    {
        if (operation?.WorkloadJson is null) return false;
        try
        {
            using var document = JsonDocument.Parse(operation.WorkloadJson);
            return document.RootElement.TryGetProperty("browserTest", out var browser) && browser.ValueKind == JsonValueKind.Object;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }
}
