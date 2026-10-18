using System.Text.Json;
using CSweet.Domain.WorkManagement;
using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK;
using CSweet.Compute.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public sealed partial class WorkDeliveryService
{
    private bool IsComputeTest(WorkDeliveryPlan plan, WorkDeliveryExecution execution) =>
        Branches(plan).Any(x => x.Scope == execution.Scope && x.ItemId == execution.WorkItemId) &&
        Branches(plan).Where(x => x.Scope == execution.Scope && x.ItemId == execution.WorkItemId)
            .All(x => x.ReadinessMode == "compute-test");

    private async Task ValidateComputeWaitAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution,
        WorkStageExecution stage, WorkDeliveryComputePending wait, CancellationToken ct)
    {
        if (!IsComputeTest(plan, execution) || wait.CandidateDigest != Candidate(execution)!.Digest ||
            wait.OperationIds.Count > 10 || wait.OperationIds.Distinct().Count() != wait.OperationIds.Count ||
            wait.RetryAt > clock.GetUtcNow().AddMinutes(10))
            throw new InvalidOperationException("Compute waits must bind the current candidate and bounded recovery time.");
        foreach (var id in wait.OperationIds)
        {
            var operation = await db.Set<CSweet.Domain.Compute.ComputeOperation>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == id && x.OrganizationId == plan.OrganizationId && x.InstallationId == stage.AgentInstallationId, ct)
                ?? throw new UnauthorizedAccessException("The pending compute operation belongs to another reviewer.");
            if (!await db.Set<CSweet.Domain.Compute.ComputeEnvironment>().AnyAsync(x => x.Id == operation.EnvironmentId &&
                x.OrganizationId == plan.OrganizationId && x.WorkstreamId == plan.WorkstreamId &&
                x.InstallationId == stage.AgentInstallationId && x.Generation == operation.Generation, ct))
                throw new UnauthorizedAccessException("The pending compute environment is outside the delivery project.");
        }
    }

    private async Task ValidateCurrentComputeTestAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution, CancellationToken ct)
    {
        var stage = execution.Stages.LastOrDefault(x => Handler(ScopeFor(plan,execution),x.StageKey) is "build-readiness" or "compute-test" && x.LastOutcomeCode == "approved");
        if (stage?.Attempts.LastOrDefault()?.ResultJson is not { } json) return;
        var review = Decode<WorkExecutionOutcomeV1>(json).Output.Deserialize<WorkDeliveryReviewResult>(Json);
        if (review?.ComputeTests is not { Count: > 0 }) return;
        foreach (var proof in review.ComputeTests)
        {
            if (proof.UrlExpiresAt <= clock.GetUtcNow() || !await db.Set<CSweet.Domain.Compute.ComputeEnvironment>()
                .AnyAsync(x => x.Id == proof.EnvironmentId && x.OrganizationId == plan.OrganizationId &&
                    x.InstallationId == stage.AgentInstallationId && x.WorkstreamId == plan.WorkstreamId &&
                    x.Generation == proof.Generation && x.State == CSweet.Domain.Compute.ComputeLifecycleState.Ready &&
                    x.LeaseExpiresAt > clock.GetUtcNow(), ct))
                throw new InvalidOperationException("The exact-candidate test link expired or its compute environment changed; obtain fresh readiness evidence.");
            var latest = await db.Set<CSweet.Domain.Compute.ComputeOperation>().AsNoTracking()
                .Where(x => x.EnvironmentId == proof.EnvironmentId && x.OrganizationId == plan.OrganizationId &&
                    x.InstallationId == stage.AgentInstallationId && x.Action == InfrastructureActions.Execute &&
                    x.WorkloadJson != null && x.WorkloadJson.Contains("csweet-candidate:"))
                .OrderByDescending(x => x.CreatedAt).Select(x => x.Id).FirstOrDefaultAsync(ct);
            if (latest != proof.ValidationOperationId)
                throw new InvalidOperationException("Another candidate deployment changed the test application; obtain fresh exact-source readiness evidence.");
        }
    }

    internal async Task ValidateReadinessAsync(WorkDeliveryPlan plan, WorkDeliveryExecution execution,
        WorkStageExecution stage, WorkDeliveryReviewResult review, CancellationToken ct)
    {
        if (!review.Approved) return; // Rejection records remediation findings; it cannot authorize readiness.
        if (!IsComputeTest(plan, execution))
        {
            if (review.ComputeTests.Count != 0) throw new InvalidOperationException("Compute evidence cannot replace agreed certified packaging.");
            await ValidateBuildIdentitiesAsync(plan, execution, review.BuildIds, true, ct); return;
        }
        var candidate = Candidate(execution)!;
        if (review.BuildIds.Count != 0 || review.ComputeTests.Count != candidate.Repositories.Count ||
            review.ComputeTests.Select(x => x.RepositoryId).Distinct().Count() != review.ComputeTests.Count)
            throw new InvalidOperationException("Provide exact compute test evidence for every repository.");
        foreach (var repository in candidate.Repositories)
        {
            var proof = review.ComputeTests.SingleOrDefault(x => x.RepositoryId == repository.RepositoryId);
            if (proof is null || proof.CommitSha != repository.CandidateCommitSha || proof.Generation < 1)
                throw new InvalidOperationException("The compute test does not bind the exact source revision.");
            var environment = await db.Set<CSweet.Domain.Compute.ComputeEnvironment>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == proof.EnvironmentId && x.OrganizationId == plan.OrganizationId &&
                x.WorkstreamId == plan.WorkstreamId && x.InstallationId == stage.AgentInstallationId, ct);
            if (environment is null || environment.Generation != proof.Generation ||
                environment.State != CSweet.Domain.Compute.ComputeLifecycleState.Ready || environment.LeaseExpiresAt <= clock.GetUtcNow())
                throw new InvalidOperationException("The test compute instance is unavailable or changed.");
            async Task<CSweet.Domain.Compute.ComputeOperation> Operation(Guid id, string action) =>
                await db.Set<CSweet.Domain.Compute.ComputeOperation>().AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == id && x.OrganizationId == plan.OrganizationId && x.InstallationId == stage.AgentInstallationId &&
                    x.EnvironmentId == environment.Id && x.Action == action &&
                    x.Status == "Completed", ct) ?? throw new InvalidOperationException("The test operation is not a completed authorized receipt.");
            var validation = await Operation(proof.ValidationOperationId, InfrastructureActions.Execute);
            var publication = await Operation(proof.PublicationOperationId, InfrastructureActions.PublishPort);
            // Each workload advances the cursor; adjacency excludes intervening commands/lifecycle changes.
            if (validation.Generation < 1 || publication.Generation != checked(validation.Generation + 1) ||
                publication.Generation != proof.Generation || validation.CompletedAt is null ||
                publication.CreatedAt < validation.CompletedAt)
                throw new InvalidOperationException("Test validation and publication must be adjacent authorized compute generations.");
            var command = Decode<ComputeWorkload>(validation.WorkloadJson!).Command;
            var result = Decode<ComputeWorkloadResult>(validation.ResultJson!);
            var link = Decode<ComputeWorkloadResult>(publication.ResultJson!);
            var snapshot = await ReadEvidenceAsync(plan.OrganizationId, stage.AgentInstallationId ?? stage.OrganizationUserId!.Value,
                new(plan.Id, execution.Id, repository.RepositoryId), ct);
            if (proof.ArchiveSha256 != snapshot.ArchiveSha256 || snapshot.Archive is null)
                throw new InvalidOperationException("The tested archive differs from the authoritative candidate snapshot.");
            // Commands carry workload data; the immutable image/features snapshot belongs
            // to the operation that provisioned this environment.
            var provision = await db.ComputeOperations.AsNoTracking().Where(o=>o.EnvironmentId==environment.Id&&
                o.OrganizationId==plan.OrganizationId&&o.InstallationId==stage.AgentInstallationId&&
                o.Action==InfrastructureActions.Provision&&o.Status=="Completed")
                .OrderBy(o=>o.Generation).FirstOrDefaultAsync(ct);
            var specification=JsonSerializer.Deserialize<CSweet.Domain.Compute.ComputeSpecification>(environment.SpecificationJson,ComputeProtocol.Json)!;
            var template=provision is null?null:JsonSerializer.Deserialize<CSweet.Domain.Compute.ComputeTemplate>(provision.TemplateJson,ComputeProtocol.Json);
            if(template?.Features is null||template.Id!=specification.TemplateId)
                throw new InvalidOperationException("The test environment's provisioning template is unavailable or does not match its specification.");
            var options = new CSweet.Agent.SDK.Compute.ComputeDefaults("Ready", plan.WorkstreamId, template.Id, null)
            {
                Features = template.Features.ToArray(),
                CachedImages = template.Features.Contains("docker-apps-v1") ? ["csweet/python:3.12", "csweet/node:22"] : []
            };
            var mode = DeliveryComputeTest.SelectMode(snapshot.Archive, options);
            var root = $"/var/lib/csweet-compute/work/review-{repository.RepositoryId:N}-{candidate.Digest[..16]}";
            var expected = DeliveryComputeTest.ValidationScript(root, repository, proof.ArchiveSha256, mode, plan.Id);
            if (mode == "node")
            {
                var networked = JsonSerializer.Deserialize<CSweet.Domain.Compute.ComputeSpecification>(environment.SpecificationJson, ComputeProtocol.Json)!.NetworkPolicy.AllowOutbound;
                var dependencies = networked ? null : DeliveryNodeDependencies.Read(snapshot.Archive);
                var lockfileHash = DeliveryNodeDependencies.LockfileHash(snapshot.Archive);
                var restore = await db.ComputeOperations.AsNoTracking().SingleOrDefaultAsync(o => o.EnvironmentId == environment.Id &&
                    o.OrganizationId == plan.OrganizationId && o.InstallationId == stage.AgentInstallationId &&
                    o.Generation == validation.Generation - 1 && o.Action == InfrastructureActions.Execute && o.Status == "Completed", ct);
                var restoreWorkload = restore?.WorkloadJson is { } restoreJson ? Decode<ComputeWorkload>(restoreJson) : null;
                var restoreResult = restore?.ResultJson is { } restoreResultJson ? Decode<ComputeWorkloadResult>(restoreResultJson) : null;
                var restoreKey=restore?.IdempotencyKey;var commandOffset=restoreKey?.LastIndexOf(":cmd:",StringComparison.Ordinal)??-1;
                var job=commandOffset>0&&restoreKey!.StartsWith($"compute-node2:{execution.Id:N}:",StringComparison.Ordinal)
                    ? DeliveryComputeTest.NetworkRestoreJobKey(restoreKey[..commandOffset]):null;
                var completedJob=networked&&job is not null&&restoreWorkload?.Command is {} observation&&
                    observation.Arguments.SequenceEqual(new[]{"-c",DeliveryComputeTest.ObserveNetworkRestoreScript(root,proof.ArchiveSha256,lockfileHash,job)})&&
                    restoreResult?.Command?.StandardOutput is {} observedBytes&&System.Text.Encoding.UTF8.GetString(observedBytes)
                        .Contains(DeliveryComputeTest.RestoreJobMarker(job,"ok"),StringComparison.Ordinal);
                if (restore is null || restore.CompletedAt is null || restore.CompletedAt > validation.CreatedAt ||
                    restoreWorkload?.Command is not { } install ||
                    (networked ? restoreWorkload.NodeDependencies is not null :
                        restoreWorkload.NodeDependencies is null || Encode(restoreWorkload.NodeDependencies) != Encode(dependencies)) ||
                    install.Executable != "/bin/sh" || install.WorkingDirectory != "/var/lib/csweet-compute/work" ||
                    (!completedJob&&!install.Arguments.SequenceEqual(new[] { "-c", DeliveryComputeTest.RestoreScript(root, proof.ArchiveSha256, lockfileHash, networked) })) ||
                    restoreResult is not { ErrorCode: null, Command: { ExitCode: 0, TimedOut: false, ErrorCode: null } installed } ||
                    installed.RequestId != install.RequestId)
                    throw new InvalidOperationException("Node readiness requires the adjacent successful exact-source, integrity-pinned dependency restore before tests/build.");
            }
            if (command is null || command.Executable != "/bin/sh" || command.WorkingDirectory != "/var/lib/csweet-compute/work" ||
                !command.Arguments.SequenceEqual(new[] { "-c", expected }) ||
                result is not { ErrorCode: null, Command: { ExitCode: 0, TimedOut: false, ErrorCode: null } output } ||
                output.RequestId != command.RequestId ||
                !System.Text.Encoding.UTF8.GetString(output.StandardOutput ?? []).Contains(DeliveryComputeTest.Marker(proof.CommitSha, proof.ArchiveSha256), StringComparison.Ordinal) ||
                link.ErrorCode is not null || link.Url != proof.Url || link.UrlExpiresAt != proof.UrlExpiresAt ||
                proof.UrlExpiresAt <= clock.GetUtcNow() || !Uri.TryCreate(proof.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme != "http" || uri.Host != "127.0.0.1" || Decode<ComputeWorkload>(publication.WorkloadJson!).PublishPort != 8080)
                throw new InvalidOperationException("Exact-source build, HTTP health and current provider URL receipts are required.");
        }
    }
}
