using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    internal const string LegacyComputeCursorFailure = "The test environment generation changed; inspect retained operations before rerunning.";
    internal const string LegacySourceArchiveFailure = "The immutable source archive changed.";
    internal const string LegacyMissingDependencyFailure = "Compute validation failed or its result is unknown: FAIL: could not read/parse node_modules/phaser/package.json: /app/node_modules/phaser/package.json (ENOENT: no such file or directory, open '/app/node_modules/phaser/package.json')";
    internal const string InterruptedManagedRestore = "Compute validation failed or its result is unknown: outcome-unknown";
    internal const string TruncatedComputeValidation = "Compute validation omitted exact-source and HTTP health evidence.";
    internal const string FailedProvisionCursor = "The test environment generation changed without a matching retained operation; inspect current operations before rerunning.";
    internal const string UndispatchedLocalLink = "No current provider-verified local test URL was returned.";

    internal async Task<bool> IsFailedProvisionCleanupAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        if (execution.ScopeRevision != plan.ScopeRevision || execution.Status != "Blocked" || execution.CurrentStageKey != "build-readiness" ||
            execution.BlockedReason != FailedProvisionCursor || !IsComputeTest(plan, execution) ||
            Candidate(execution) is not { Repositories.Count: 1 } candidate) return false;
        if (plan.Findings.Where(f => f.Status == "Open").Any(f => f.ExecutionId != execution.Id ||
            f.CandidateDigest != candidate.Digest || f.Summary != FailedProvisionCursor)) return false;
        var current = CurrentDeliveryStage(execution);
        if (current.LastOutcomeCode != "changes_requested" || current.AgentInstallationId is not { } installation) return false;
        var prior = execution.Stages.Where(s => s.StageKey == "build-readiness" && s.Traversal < current.Traversal &&
            s.AgentInstallationId == installation && s.LastOutcomeCode == "awaiting-compute").OrderByDescending(s => s.Traversal).FirstOrDefault();
        var previous = prior?.Attempts.Where(a => a.Status == WorkExecutionAttemptStatus.Completed && a.ResultJson != null)
            .OrderByDescending(a => a.Attempt).Select(a => Decode<WorkExecutionOutcomeV1>(a.ResultJson!)).FirstOrDefault();
        var wait = previous?.Output.Deserialize<WorkDeliveryComputePending>(Json);
        if (wait?.CandidateDigest != candidate.Digest || wait.OperationIds.Count != 0) return false;
        var desired = $"project-test-internet:{plan.WorkstreamId:N}";
        var environment = await db.ComputeEnvironments.AsNoTracking().SingleOrDefaultAsync(e => e.OrganizationId == plan.OrganizationId &&
            e.WorkstreamId == plan.WorkstreamId && e.InstallationId == installation && e.DesiredEnvironmentKey == desired &&
            e.DesiredState == ComputeDesiredState.Destroyed && e.State == ComputeLifecycleState.Destroyed && e.TeardownConfirmedAt != null, ct);
        if (environment is null || environment.CreatedAt < execution.CreatedAt || environment.CreatedAt > current.CreatedAt ||
            JsonSerializer.Deserialize<ComputeSpecification>(environment.SpecificationJson, ComputeProtocol.Json)!.NetworkPolicy is not { Mode: ComputeNetworkMode.OutboundOnly, AllowOutbound: true }) return false;
        var operations = await db.ComputeOperations.AsNoTracking().Where(o => o.EnvironmentId == environment.Id).ToArrayAsync(ct);
        if (operations.Any(o => o.OrganizationId != plan.OrganizationId || o.InstallationId != installation || o.WorkloadJson is not null ||
            o.Action is not (InfrastructureActions.Provision or InfrastructureActions.Destroy))) return false;
        return operations.Count(o => o.Action == InfrastructureActions.Provision && o.Generation == 1 &&
                   o.Status == "Superseded" && o.FailureCode == "provider-operation-failed" && o.CompletedAt != null) == 1 &&
               operations.Any(o => o.Generation == environment.Generation && o.Action == InfrastructureActions.Destroy &&
                   o.Status == "Completed" && o.CompletedAt != null && o.FailureCode is null);
    }
    internal static string LegacyVerboseNodeValidationScript(string root, WorkDeliveryRepositoryCandidate repository, string archiveHash, Guid planId) =>
        CSweet.Agent.SDK.DeliveryComputeTest.ValidationScript(root, repository, archiveHash, "node", planId).Replace(
            "if ! docker run --rm --network=none -v \"$PWD/source:/app\" -w /app csweet/node:22 sh -c 'npm test && npm run build' > validation.log 2>&1; then\n tail -c 6000 validation.log >&2\n exit 1\nfi",
            "docker run --rm --network=none -v \"$PWD/source:/app\" -w /app csweet/node:22 sh -c 'npm test && npm run build'", StringComparison.Ordinal);

    internal async Task<bool> IsTruncatedComputeValidationAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        if (execution.ScopeRevision != plan.ScopeRevision || execution.CurrentStageKey != "build-readiness" || execution.Status != "Blocked" ||
            execution.BlockedReason != TruncatedComputeValidation || !IsComputeTest(plan, execution) ||
            Candidate(execution) is not { Repositories.Count: 1 } candidate) return false;
        if (plan.Findings.Where(f => f.Status == "Open").Any(f => f.ExecutionId != execution.Id ||
            f.CandidateDigest != candidate.Digest || f.Summary != TruncatedComputeValidation)) return false;
        var current = CurrentDeliveryStage(execution);
        if (current.LastOutcomeCode != "changes_requested" || current.AgentInstallationId is not { } installation) return false;
        var prior = execution.Stages.Where(s => s.StageKey == "build-readiness" && s.Traversal < current.Traversal &&
            s.AgentInstallationId == installation && s.LastOutcomeCode == "awaiting-compute").OrderByDescending(s => s.Traversal).FirstOrDefault();
        var previous = prior?.Attempts.Where(a => a.Status == WorkExecutionAttemptStatus.Completed && a.ResultJson != null)
            .OrderByDescending(a => a.Attempt).Select(a => Decode<WorkExecutionOutcomeV1>(a.ResultJson!)).FirstOrDefault();
        var wait = previous?.Output.Deserialize<WorkDeliveryComputePending>(Json);
        if (wait?.CandidateDigest != candidate.Digest || wait.OperationIds.Count != 1) return false;
        var validation = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == wait.OperationIds[0] &&
            o.OrganizationId == plan.OrganizationId && o.InstallationId == installation && o.Action == InfrastructureActions.Execute && o.Status == "Completed", ct);
        if (validation?.ResultJson is null || validation.WorkloadJson is null || validation.CompletedAt is null ||
            Decode<ComputeWorkloadResult>(validation.ResultJson) is not { ErrorCode: null, Command: { ExitCode: 0, TimedOut: false, ErrorCode: null, Truncated: true } output } ||
            Decode<ComputeWorkload>(validation.WorkloadJson) is not { Command: { } command, NodeDependencies: null, PublishPort: null } ||
            output.RequestId != command.RequestId || command.Arguments.Count != 2 ||
            command.Executable != "/bin/sh" || command.WorkingDirectory != "/var/lib/csweet-compute/work") return false;
        if (!await db.ComputeEnvironments.AnyAsync(e => e.Id == validation.EnvironmentId && e.OrganizationId == plan.OrganizationId &&
            e.WorkstreamId == plan.WorkstreamId && e.InstallationId == installation && e.Generation == validation.Generation &&
            e.State == ComputeLifecycleState.Ready && e.TeardownConfirmedAt == null && e.LeaseExpiresAt > clock.GetUtcNow(), ct)) return false;
        var repository = candidate.Repositories.Single();
        var pins = System.Text.RegularExpressions.Regex.Matches(command.Arguments[1], "'([a-f0-9]{64})' \\| sha256sum -c -");
        if (pins.Count != 1) return false;
        var archive = pins[0].Groups[1].Value;
        var root = $"/var/lib/csweet-compute/work/review-{repository.RepositoryId:N}-{candidate.Digest[..16]}";
        if (!command.Arguments.SequenceEqual(new[] { "-c", LegacyVerboseNodeValidationScript(root, repository, archive, plan.Id) }) ||
            System.Text.Encoding.UTF8.GetString(output.StandardOutput).Contains(CSweet.Agent.SDK.DeliveryComputeTest.Marker(repository.CandidateCommitSha, archive), StringComparison.Ordinal)) return false;
        var stopped = execution.Stages.Where(s => s.CreatedAt < prior!.CreatedAt && s.LastOutcomeCode is "changes_requested" or "rejected")
            .OrderBy(s => s.CreatedAt).LastOrDefault();
        var epoch = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidate.Digest + ":" + (stopped?.Id.ToString("N") ?? "initial"))));
        var key = $"compute-node2:{execution.Id:N}:{epoch}"; var prefix = key + ":cmd:";
        if (validation.IdempotencyKey is not { } operationKey || !operationKey.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(operationKey[prefix.Length..], out var step) || step < 1 ||
            command.RequestId != new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}:{step}")).AsSpan(0, 16))) return false;
        var restore = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(o => o.EnvironmentId == validation.EnvironmentId &&
            o.OrganizationId == plan.OrganizationId && o.InstallationId == installation && o.Generation == validation.Generation - 1 &&
            o.Action == InfrastructureActions.Execute && o.Status == "Completed", ct);
        if (restore?.ResultJson is null || restore.WorkloadJson is null || restore.CompletedAt is null || restore.CompletedAt > validation.CreatedAt ||
            restore.IdempotencyKey != prefix + (step - 1) ||
            Decode<ComputeWorkload>(restore.WorkloadJson) is not { Command: { } install, NodeDependencies: { } dependencies } ||
            Decode<ComputeWorkloadResult>(restore.ResultJson) is not { ErrorCode: null, Command: { ExitCode: 0, TimedOut: false, ErrorCode: null } installed } ||
            installed.RequestId != install.RequestId || install.Executable != "/bin/sh" || install.WorkingDirectory != command.WorkingDirectory ||
            install.RequestId != new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}:{step - 1}")).AsSpan(0, 16))) return false;
        dependencies.Validate();
        return install.Arguments.SequenceEqual(new[] { "-c", CSweet.Agent.SDK.DeliveryComputeTest.RestoreScript(root, archive, dependencies.LockfileSha256) });
    }
    internal const string ManagedRestoreDiagnosticScript = "set -eu\nfor d in /var/lib/csweet-compute/work/npm-*; do [ -d \"$d\" ] || continue; find \"$d/packages\" -maxdepth 1 -type f -printf '%f %s bytes\\n'; if [ -d \"$d/cache\" ]; then du -sk \"$d/cache\"; fi; done\nsystemctl list-units --all 'csweet-command-*' --no-pager\ndocker ps --format '{{.Names}} {{.Status}}'\ndf -h /var/lib/csweet-compute/work\n";

    internal async Task<bool> IsInterruptedManagedRestoreAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        // Explicit owner recovery starts a new fenced operation; it never replays the uncertain one or approves readiness.
        if (execution.ScopeRevision != plan.ScopeRevision || execution.CurrentStageKey != "build-readiness" || execution.Status != "Blocked" ||
            execution.BlockedReason != InterruptedManagedRestore || !IsComputeTest(plan, execution) ||
            Candidate(execution) is not { Repositories.Count: 1 } candidate) return false;
        var findings = plan.Findings.Where(f => f.Status == "Open").ToArray();
        // A repeated identical processor rejection can reference an already superseded historical finding.
        // Its current authenticated rejected stage and exact uncertain operation still must match below.
        if (findings.Any(f => f.ExecutionId != execution.Id || f.CandidateDigest != candidate.Digest || f.Summary != InterruptedManagedRestore)) return false;
        var current = CurrentDeliveryStage(execution);
        if (current.LastOutcomeCode != "changes_requested" || current.AgentInstallationId is not { } installation) return false;
        var prior = execution.Stages.Where(s => s.StageKey == "build-readiness" && s.Traversal < current.Traversal && s.AgentInstallationId == installation &&
            s.LastOutcomeCode == "awaiting-compute").OrderByDescending(s => s.Traversal).FirstOrDefault();
        var previous = prior?.Attempts.Where(a => a.Status == WorkExecutionAttemptStatus.Completed && a.ResultJson != null)
            .OrderByDescending(a => a.Attempt).Select(a => Decode<WorkExecutionOutcomeV1>(a.ResultJson!)).FirstOrDefault();
        var wait = previous?.Output.Deserialize<WorkDeliveryComputePending>(Json);
        if (wait?.CandidateDigest != candidate.Digest || wait.OperationIds.Count != 1) return false;
        var operation = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == wait.OperationIds[0] &&
            o.OrganizationId == plan.OrganizationId && o.InstallationId == installation && o.Action == InfrastructureActions.Execute && o.Status == "Completed", ct);
        if (operation?.ResultJson is null || operation.WorkloadJson is null || operation.CompletedAt is null ||
            Decode<ComputeWorkloadResult>(operation.ResultJson) is not { ErrorCode: "outcome-unknown", Command: null, Url: null } ||
            Decode<ComputeWorkload>(operation.WorkloadJson) is not { Command: { } command, NodeDependencies: { } dependencies }) return false;
        var environment = await db.ComputeEnvironments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == operation.EnvironmentId &&
            e.OrganizationId == plan.OrganizationId && e.WorkstreamId == plan.WorkstreamId && e.InstallationId == installation &&
            e.State == ComputeLifecycleState.Ready && e.TeardownConfirmedAt == null && e.LeaseExpiresAt > clock.GetUtcNow(), ct);
        if (environment is null) return false;
        if (environment.Generation != operation.Generation)
        {
            // Permit one completed, exact read-only diagnostic; any unrelated command or lifecycle drift still blocks recovery.
            if (environment.Generation != operation.Generation + 1) return false;
            var diagnostic = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(o => o.EnvironmentId == environment.Id &&
                o.OrganizationId == plan.OrganizationId && o.InstallationId == installation && o.Generation == environment.Generation &&
                o.Action == InfrastructureActions.Execute && o.Status == "Completed", ct);
            if (diagnostic?.WorkloadJson is null || diagnostic.ResultJson is null ||
                Decode<ComputeWorkload>(diagnostic.WorkloadJson) is not { Command: { } inspection, NodeDependencies: null, PublishPort: null } ||
                inspection.Executable != "/bin/sh" || inspection.WorkingDirectory != "/var/lib/csweet-compute/work" ||
                !inspection.Arguments.SequenceEqual(new[] { "-c", ManagedRestoreDiagnosticScript }) ||
                Decode<ComputeWorkloadResult>(diagnostic.ResultJson) is not { ErrorCode: null, Command: { ExitCode: 0, TimedOut: false, ErrorCode: null } inspected } ||
                inspected.RequestId != inspection.RequestId) return false;
        }
        var repository = candidate.Repositories.Single();
        // A rejected review's evidence grant has closed. Recovery must not borrow that identity or fetch source.
        // Bind the trusted pending receipt to its SDK candidate/rejection epoch and exact restore-only recipe.
        // The new assignment must independently authorize and re-read source before executing fresh work.
        var stopped = execution.Stages.Where(s => s.CreatedAt < prior!.CreatedAt && s.LastOutcomeCode is "changes_requested" or "rejected")
            .OrderBy(s => s.CreatedAt).LastOrDefault();
        var epoch = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            candidate.Digest + ":" + (stopped?.Id.ToString("N") ?? "initial"))));
        var key = $"compute-node2:{execution.Id:N}:{epoch}";
        var prefix = key + ":cmd:";
        if (operation.IdempotencyKey is not { } operationKey || !operationKey.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(operationKey[prefix.Length..], out var step) || step < 0 ||
            command.RequestId != new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}:{step}")).AsSpan(0, 16)) ||
            command.Arguments.Count != 2) return false;
        var pins = System.Text.RegularExpressions.Regex.Matches(command.Arguments[1], "'([a-f0-9]{64})' \\| sha256sum -c -");
        if (pins.Count != 2 || pins[1].Groups[1].Value != dependencies.LockfileSha256) return false;
        dependencies.Validate();
        var root = $"/var/lib/csweet-compute/work/review-{repository.RepositoryId:N}-{candidate.Digest[..16]}";
        return command.Executable == "/bin/sh" &&
            command.WorkingDirectory == "/var/lib/csweet-compute/work" &&
            command.Arguments.SequenceEqual(new[] { "-c", CSweet.Agent.SDK.DeliveryComputeTest.RestoreScript(root, pins[0].Groups[1].Value, dependencies.LockfileSha256) });
    }

    internal async Task<bool> IsLegacyMissingDependencyFailureAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        if (execution.ScopeRevision != plan.ScopeRevision || execution.CurrentStageKey != "build-readiness" || execution.Status != "Blocked" ||
            execution.BlockedReason?.TrimEnd() != LegacyMissingDependencyFailure || !IsComputeTest(plan, execution) ||
            Candidate(execution) is not { Repositories.Count: 1 } candidate) return false;
        var findings = plan.Findings.Where(f => f.Status == "Open").ToArray();
        if (findings.Length == 0 || findings.Any(f => f.ExecutionId != execution.Id || f.CandidateDigest != candidate.Digest ||
            f.Summary.TrimEnd() != LegacyMissingDependencyFailure)) return false;
        var current = CurrentDeliveryStage(execution);
        if (current.LastOutcomeCode != "changes_requested" || current.AgentInstallationId is not { } installation) return false;
        var prior = execution.Stages.Where(s => s.StageKey == "build-readiness" && s.Traversal < current.Traversal && s.AgentInstallationId == installation &&
            s.LastOutcomeCode == "awaiting-compute").OrderByDescending(s => s.Traversal).FirstOrDefault();
        var previous = prior?.Attempts.Where(a => a.Status == WorkExecutionAttemptStatus.Completed && a.ResultJson != null)
            .OrderByDescending(a => a.Attempt).Select(a => Decode<WorkExecutionOutcomeV1>(a.ResultJson!)).FirstOrDefault();
        var wait = previous?.Output.Deserialize<WorkDeliveryComputePending>(Json);
        if (wait?.CandidateDigest != candidate.Digest || wait.OperationIds.Count != 1) return false;
        var operation = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == wait.OperationIds[0] &&
            o.OrganizationId == plan.OrganizationId && o.InstallationId == installation && o.Action == InfrastructureActions.Execute && o.Status == "Completed", ct);
        if (operation?.ResultJson is null || operation.CompletedAt is null ||
            Decode<ComputeWorkloadResult>(operation.ResultJson) is not { ErrorCode: null, Command: { ExitCode: 1, TimedOut: false, ErrorCode: null } result }) return false;
        var workload = Decode<ComputeWorkload>(operation.WorkloadJson!);
        var operationPrefix = $"delivery-compute:{execution.Id:N}:{candidate.Digest}:cmd:";
        if (!operation.IdempotencyKey.StartsWith(operationPrefix, StringComparison.Ordinal) ||
            !int.TryParse(operation.IdempotencyKey[operationPrefix.Length..], out var step) || step < 0 || result.StandardError is null) return false;
        if (workload.NodeDependencies is not null || workload.Command is not { } command || command.RequestId != result.RequestId || command.Arguments.Count != 2 ||
            command.Arguments[0] != "-c" || command.Executable != "/bin/sh" || command.WorkingDirectory != "/var/lib/csweet-compute/work" ||
            "Compute validation failed or its result is unknown: " + System.Text.Encoding.UTF8.GetString(result.StandardError).TrimEnd() != LegacyMissingDependencyFailure) return false;
        var match = System.Text.RegularExpressions.Regex.Match(command.Arguments[1], "'([a-f0-9]{64})' \\| sha256sum -c -");
        if (!match.Success) return false;
        var repository = candidate.Repositories.Single();
        var root = $"/var/lib/csweet-compute/work/review-{repository.RepositoryId:N}-{candidate.Digest[..16]}";
        var legacy = CSweet.Agent.SDK.DeliveryComputeTest.ValidationScript(root, repository, match.Groups[1].Value, "docker", plan.Id)
            .Replace("if [ 'docker' != node ] && [ -f source/package.json ]; then", "if [ -f source/package.json ]; then", StringComparison.Ordinal);
        if (command.Arguments[1] != legacy) return false;
        return await db.ComputeEnvironments.AsNoTracking().AnyAsync(e => e.Id == operation.EnvironmentId && e.OrganizationId == plan.OrganizationId &&
            e.WorkstreamId == plan.WorkstreamId && e.InstallationId == installation && e.Generation == operation.Generation &&
            e.State == ComputeLifecycleState.Ready && e.TeardownConfirmedAt == null && e.LeaseExpiresAt > clock.GetUtcNow(), ct);
    }

    internal async Task<bool> IsLegacyComputeCursorFailureAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        if (execution.ScopeRevision != plan.ScopeRevision || execution.CurrentStageKey != "build-readiness" ||
            execution.Status != "Blocked" || execution.BlockedReason is not (LegacyComputeCursorFailure or LegacySourceArchiveFailure) || !IsComputeTest(plan, execution) ||
            Candidate(execution) is not { } candidate) return false;
        var findings = plan.Findings.Where(f => f.Status == "Open").ToArray();
        if (findings.Length == 0 || findings.Any(f => f.ExecutionId != execution.Id || f.CandidateDigest != candidate.Digest ||
            f.Summary != execution.BlockedReason)) return false;
        var current = CurrentDeliveryStage(execution);
        if (current.LastOutcomeCode != "changes_requested" || current.AgentInstallationId is not { } installation) return false;
        var prior = execution.Stages.Where(s => s.StageKey == "build-readiness" && s.Traversal < current.Traversal &&
            s.AgentInstallationId == installation && s.Status == WorkStageExecutionStatus.Completed && s.LastOutcomeCode == "awaiting-compute")
            .OrderByDescending(s => s.Traversal).FirstOrDefault();
        var result = prior?.Attempts.Where(a => a.Status == WorkExecutionAttemptStatus.Completed && a.ResultJson != null)
            .OrderByDescending(a => a.Attempt).Select(a => Decode<WorkExecutionOutcomeV1>(a.ResultJson!)).FirstOrDefault();
        if (result is null || result.OutcomeCode != "awaiting-compute") return false;
        var wait = result.Output.Deserialize<WorkDeliveryComputePending>(Json);
        if (wait?.CandidateDigest != candidate.Digest || wait.OperationIds.Count != 1) return false;
        var operation = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == wait.OperationIds[0] &&
            o.OrganizationId == plan.OrganizationId && o.InstallationId == installation && o.Action == InfrastructureActions.Execute &&
            o.Status == "Completed" && o.Generation == 2 && o.IdempotencyKey == $"delivery-compute:{execution.Id:N}:{candidate.Digest}:cmd:0", ct);
        if (operation?.ResultJson is null || operation.CompletedAt is null ||
            Decode<ComputeWorkloadResult>(operation.ResultJson) is not
                { ErrorCode: null, Command: { ExitCode: 0, TimedOut: false, ErrorCode: null } command }) return false;
        var workload = Decode<ComputeWorkload>(operation.WorkloadJson!);
        if (workload.Command?.RequestId != command.RequestId) return false;
        return await db.ComputeEnvironments.AsNoTracking().AnyAsync(e => e.Id == operation.EnvironmentId &&
            e.OrganizationId == plan.OrganizationId && e.WorkstreamId == plan.WorkstreamId && e.InstallationId == installation &&
            e.Generation == operation.Generation && e.State == ComputeLifecycleState.Ready && e.TeardownConfirmedAt == null &&
            e.LeaseExpiresAt > clock.GetUtcNow(), ct);
    }
}
