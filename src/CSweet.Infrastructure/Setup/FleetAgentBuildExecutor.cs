using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

/// <summary>Submits builder VMs to the execution fleet; the app host never invokes a VM provider.</summary>
public sealed class FleetAgentBuildExecutor(
    CSweetDbContext dbContext,
    IExecutionWorkloadOrchestrator orchestrator,
    IGuestImageRegistry guestImages,
    IOptions<AgentRuntimeManagerOptions> options,
    ILogger<FleetAgentBuildExecutor> logger) : IPluginBuildExecutor
{
    private const int MinimumBuilderMemoryMb = 4096;

    public Task<AgentBuildWorkspace> CloneAsync(
        AgentBuildExecutionRequest request,
        IAgentBuildProgressReporter progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var logRoot = Path.GetFullPath(options.Value.BuildLogStorePath);
        Directory.CreateDirectory(logRoot);
        var logPath = Path.Combine(logRoot, $"build-{request.BuildJobId:N}.log");
        File.WriteAllText(logPath,
            $"[{DateTimeOffset.UtcNow:O}] Build queued for distributed execution.{Environment.NewLine}",
            new UTF8Encoding(false));
        return Task.FromResult(new AgentBuildWorkspace(
            $"fleet-source:{request.BuildJobId:N}",
            $"fleet-artifact:{request.BuildJobId:N}",
            logPath));
    }

    public async Task<AgentBuildExecutionResult> BuildAsync(
        AgentBuildExecutionRequest request,
        AgentBuildWorkspace workspace,
        IAgentBuildProgressReporter progress,
        CancellationToken cancellationToken = default)
    {
        var configured = options.Value;
        var guest = await guestImages.ResolveAsync(new GuestImageResolutionRequest(
            configured.BuilderGuestImageId,
            configured.BuilderGuestImageVersion,
            configured.BuilderGuestOperatingSystem,
            configured.BuilderGuestArchitecture,
            AgentTrustLevel.UntrustedRepository,
            "1.0",
            configured.PreferredIsolationProviderId,
            configured.BuilderGuestImageDigest,
            configured.RequiredCertificationSuiteVersion), cancellationToken);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var workload = new BuilderWorkloadSpecification(
            request.BuildJobId,
            guest,
            new WorkloadResourceLimits(
                Math.Max(1, (int)Math.Ceiling(request.CpuPercent / 100d)),
                request.CpuPercent,
                Math.Max(request.MemoryMb, MinimumBuilderMemoryMb),
                Math.Max(request.MaximumRepositorySizeMb * 3, 512),
                request.PidsLimit,
                checked(request.MaximumBuildLogMb * 1024 * 1024),
                TimeSpan.FromSeconds(request.TimeoutSeconds)),
            new BrokerChannelLease(
                Guid.NewGuid(), "1.0", token, guest.Digest, null,
                DateTimeOffset.UtcNow.AddSeconds(request.TimeoutSeconds).AddMinutes(5)),
            new RepositoryDescriptor(
                request.RepositoryUrl, request.CommitSha, false, request.BuildProfileId, "1.0"),
            checked((long)request.MaximumRepositorySizeMb * 1024 * 1024));

        var job = await dbContext.AgentBuildJobs.AsNoTracking()
            .SingleAsync(x => x.Id == request.BuildJobId, cancellationToken);
        var reference = await orchestrator.SubmitAsync(new ExecutionWorkloadRequest(
            ExecutionWorkloadKind.Builder,
            request.BuildJobId,
            null,
            job.ExecutionPoolId,
            null,
            configured.PreferredIsolationProviderId,
            guest.Digest,
            null,
            workload.ResourceLimits.VirtualCpuCount,
            workload.ResourceLimits.MemoryMegabytes,
            workload.ResourceLimits.WritableDiskMegabytes,
            JsonSerializer.Serialize(workload)), cancellationToken);

        string? reportedState = null;
        try
        {
            while (true)
            {
                var assignment = await ReadAsync(reference.AssignmentId, cancellationToken);
                var state = ProgressState(assignment);
                if (!string.Equals(reportedState, state.Key, StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "Builder assignment {AssignmentId} for build {BuildJobId} is {AssignmentStatus} " +
                        "on attempt {Attempt} using node {ExecutionNodeId} and provider {ProviderId}. Failure code: {FailureCode}",
                        assignment.Id,
                        request.BuildJobId,
                        assignment.Status,
                        assignment.Attempt,
                        assignment.ExecutionNodeId,
                        assignment.ProviderId,
                        assignment.FailureCode);
                    await progress.ReportAsync(new AgentBuildProgressUpdate(
                        AgentBuildStepKeys.Isolate,
                        state.Succeeded ? AgentBuildStepStatuses.Succeeded : AgentBuildStepStatuses.InProgress,
                        state.Detail), cancellationToken);
                    reportedState = state.Key;
                }
                if (assignment.Status == ExecutionAssignmentStatus.Completed)
                    return Result(assignment, workspace.LogPath);
                if (assignment.Status is ExecutionAssignmentStatus.Failed or ExecutionAssignmentStatus.Fenced or
                    ExecutionAssignmentStatus.Cancelled)
                    throw new AgentBuildException(
                        assignment.SanitizedFailure ?? "The distributed builder workload failed.",
                        AgentBuildStepKeys.Isolate);
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await orchestrator.CancelAsync(reference.AssignmentId,
                "Builder execution was cancelled by the control plane.", CancellationToken.None);
            throw;
        }
    }

    public Task CleanupWorkspaceAsync(AgentBuildWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private async Task<ExecutionWorkloadAssignment> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var tracked = dbContext.ChangeTracker.Entries<ExecutionWorkloadAssignment>()
            .FirstOrDefault(x => x.Entity.Id == id);
        if (tracked is not null) await tracked.ReloadAsync(cancellationToken);
        return await dbContext.ExecutionWorkloadAssignments.AsNoTracking()
            .Include(x => x.ExecutionNode)
            .SingleAsync(x => x.Id == id, cancellationToken);
    }

    internal static AssignmentProgressState ProgressState(ExecutionWorkloadAssignment assignment)
    {
        var node = assignment.ExecutionNode;
        var nodeLabel = node is null
            ? assignment.ExecutionNodeId?.ToString("D") ?? "an eligible Office"
            : $"{node.MachineName} (version {node.NodeVersion})";
        var retry = assignment.Attempt > 1
            ? $" Retry attempt {assignment.Attempt}."
            : string.Empty;
        var priorFailure = assignment.Attempt > 1 && !string.IsNullOrWhiteSpace(assignment.SanitizedFailure)
            ? $" Previous attempt: {assignment.SanitizedFailure}"
            : string.Empty;
        var detail = assignment.Status switch
        {
            ExecutionAssignmentStatus.Pending =>
                $"Waiting for Office capacity or connection. This build will start automatically when an Office is available.{retry}{priorFailure}",
            ExecutionAssignmentStatus.Assigned =>
                $"Dispatched to {nodeLabel}; waiting for it to accept the signed assignment.{retry}",
            ExecutionAssignmentStatus.Starting =>
                $"{nodeLabel} accepted the assignment and is preparing provider {assignment.ProviderId}.{retry}",
            ExecutionAssignmentStatus.Running =>
                $"The isolated builder VM started on {nodeLabel}; waiting for its authenticated guest channel to connect.",
            ExecutionAssignmentStatus.Stopping =>
                $"The isolated builder on {nodeLabel} is stopping and finalizing its result.",
            ExecutionAssignmentStatus.Completed =>
                $"The isolated builder completed on {nodeLabel}.",
            _ => assignment.SanitizedFailure ?? $"The distributed builder is {assignment.Status}."
        };
        return new AssignmentProgressState(
            $"{assignment.Status}|{assignment.Attempt}|{assignment.ExecutionNodeId}|{assignment.FailureCode}|{assignment.SanitizedFailure}",
            detail,
            assignment.Status == ExecutionAssignmentStatus.Completed);
    }

    private static AgentBuildExecutionResult Result(ExecutionWorkloadAssignment assignment, string logPath)
    {
        if (string.IsNullOrWhiteSpace(assignment.ResultArtifactLocator) ||
            string.IsNullOrWhiteSpace(assignment.ResultArtifactDigest) ||
            string.IsNullOrWhiteSpace(assignment.ResultArtifactSignature))
            throw new AgentBuildException(
                "The execution node completed without an authenticated builder artifact result.",
                AgentBuildStepKeys.Package);
        var digest = assignment.ResultArtifactDigest.StartsWith("sha256:", StringComparison.Ordinal)
            ? assignment.ResultArtifactDigest[7..]
            : assignment.ResultArtifactDigest;
        return new AgentBuildExecutionResult(
            assignment.ResultArtifactLocator,
            digest,
            logPath,
            assignment.ResultArtifactSignature,
            assignment.ResultArtifactFormatVersion ?? "1.0",
            assignment.ResultArtifactOperatingSystem ?? "linux",
            assignment.ResultArtifactArchitecture ?? "x64");
    }

    internal sealed record AssignmentProgressState(string Key, string Detail, bool Succeeded);
}
