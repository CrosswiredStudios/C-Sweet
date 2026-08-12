using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.SatelliteOffice.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

/// <summary>Submits builder VMs to the execution fleet; the app host never invokes a VM provider.</summary>
public sealed class FleetAgentBuildExecutor(
    CSweetDbContext dbContext,
    IExecutionWorkloadOrchestrator orchestrator,
    IGuestImageRegistry guestImages,
    IOptions<AgentRuntimeManagerOptions> options) : IPluginBuildExecutor
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

        await progress.ReportAsync(new AgentBuildProgressUpdate(
            AgentBuildStepKeys.Isolate, AgentBuildStepStatuses.InProgress,
            "Waiting for a certified execution node."), cancellationToken);
        try
        {
            while (true)
            {
                var assignment = await ReadAsync(reference.AssignmentId, cancellationToken);
                if (assignment.Status == ExecutionAssignmentStatus.Running)
                    await progress.ReportAsync(new AgentBuildProgressUpdate(
                        AgentBuildStepKeys.Isolate, AgentBuildStepStatuses.Succeeded,
                        $"Builder is running on execution node {assignment.ExecutionNodeId}."), cancellationToken);
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
            .SingleAsync(x => x.Id == id, cancellationToken);
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
}
