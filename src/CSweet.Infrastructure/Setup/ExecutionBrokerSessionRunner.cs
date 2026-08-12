using System.Globalization;
using System.Text.Json;
using CSweet.AgentBroker;
using CSweet.Application.Setup;
using CSweet.SatelliteOffice.Contracts.Workloads;
using CSweet.ExecutionArtifacts;
using CSweet.SatelliteOffice.Contracts.Guest;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

/// <summary>Terminates all guest broker sessions centrally after node tunnel authorization.</summary>
public sealed class ExecutionBrokerSessionRunner(
    CSweetDbContext dbContext,
    IAgentBrokerOperationHandler runtimeOperations,
    IAgentArtifactStore artifacts,
    IBuilderArtifactResultStore builderResults,
    IBuilderArtifactResultPublisher builderPublisher,
    ArtifactStoreOptions artifactOptions,
    TimeProvider timeProvider,
    ILogger<ExecutionBrokerSessionRunner> logger) : IExecutionBrokerSessionRunner
{
    public async Task RunAsync(
        Guid assignmentId,
        Stream duplexStream,
        CancellationToken cancellationToken = default)
    {
        var assignment = await dbContext.ExecutionWorkloadAssignments
            .Include(x => x.AgentBuildJob)!.ThenInclude(x => x!.PackageVersion)!.ThenInclude(x => x!.PackageSource)
            .SingleOrDefaultAsync(x => x.Id == assignmentId, cancellationToken)
            ?? throw new InvalidDataException("The tunnel assignment does not exist.");
        if (assignment.WorkloadKind == ExecutionWorkloadKind.Builder)
            await RunBuilderAsync(assignment, duplexStream, cancellationToken);
        else
            await RunRuntimeAsync(assignment, duplexStream, cancellationToken);
    }

    private async Task RunRuntimeAsync(
        ExecutionWorkloadAssignment assignment,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var workload = JsonSerializer.Deserialize<RuntimeWorkloadSpecification>(assignment.SpecificationJson)
            ?? throw new InvalidDataException("The runtime workload specification is empty.");
        // The node opens its tunnel before the WorkerHost call that submitted the assignment
        // returns. Do not permit the guest MCP handshake to race the durable runtime transition.
        while (true)
        {
            var status = await dbContext.AgentRuntimeInstances.AsNoTracking()
                .Where(x => x.Id == workload.WorkloadId)
                .Select(x => x.Status)
                .SingleAsync(cancellationToken);
            if (status == AgentRuntimeStatus.WaitingForMcpSession) break;
            if (AgentRuntimeInstance.IsTerminal(status))
                throw new InvalidOperationException("The runtime ended before its broker session could start.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        var grant = new AgentBrokerGrant(
            workload.WorkloadId,
            workload.BrokerLease.ChannelId,
            workload.Identity.InstallationId,
            workload.GuestImage.Digest,
            workload.Artifact.Digest,
            workload.BrokerLease.ProtocolVersion,
            workload.BrokerLease.BootToken,
            workload.BrokerLease.ExpiresAt,
            new HashSet<string>(StringComparer.Ordinal) { "mcp.runtime" },
            100_000, 1024 * 1024, 16 * 1024 * 1024, 16 * 1024 * 1024);
        var boot = new GuestBootConfiguration
        {
            WorkloadId = workload.WorkloadId.ToString("D"),
            ChannelId = workload.BrokerLease.ChannelId.ToString("D"),
            ProtocolVersion = workload.BrokerLease.ProtocolVersion,
            GuestImageDigest = workload.GuestImage.Digest,
            ArtifactDigest = workload.Artifact.Digest,
            BootToken = workload.BrokerLease.BootToken,
            LeaseExpiresAtUnixSeconds = workload.BrokerLease.ExpiresAt.ToUnixTimeSeconds(),
            ArtifactRoot = "/run/csweet/artifact/payload",
            WorkloadKind = (int)WorkloadKind.Runtime,
            InstallationId = workload.Identity.InstallationId.ToString("D"),
            BusinessId = workload.Identity.BusinessId,
            TickId = workload.Identity.TickId.ToString("D"),
            LocalBrokerSocketPath = "/run/csweet/broker.sock",
            WorkloadTokenPath = "/run/csweet/workload-token",
            MaximumFrameBytes = 16 * 1024 * 1024
        };
        var start = new StartCommand
        {
            WorkloadKind = (int)WorkloadKind.Runtime,
            MaximumLogBytes = workload.ResourceLimits.MaximumLogBytes
        };
        start.Entrypoint.AddRange(workload.Entrypoint);
        var session = new GuestBrokerHostSession(
            grant, runtimeOperations, timeProvider, bootConfiguration: boot, startCommand: start);
        await session.RunAsync(stream, stream, cancellationToken);
    }

    private async Task RunBuilderAsync(
        ExecutionWorkloadAssignment assignment,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var workload = JsonSerializer.Deserialize<BuilderWorkloadSpecification>(assignment.SpecificationJson)
            ?? throw new InvalidDataException("The builder workload specification is empty.");
        var job = assignment.AgentBuildJob
            ?? throw new InvalidDataException("The builder assignment is not bound to a build job.");
        var package = job.PackageVersion
            ?? throw new InvalidDataException("The builder package was not loaded.");
        var source = package.PackageSource
            ?? throw new InvalidDataException("The builder source was not loaded.");
        var settings = await dbContext.AgentRuntimeGlobalSettings.AsNoTracking().SingleAsync(cancellationToken);
        var request = new AgentBuildExecutionRequest(
            job.Id, package.Id, source.RepositoryUrl, package.CommitSha, package.ProjectPath ?? string.Empty,
            package.TargetFramework, workload.Repository.BuildProfileId,
            (int)workload.ResourceLimits.MaximumDuration.TotalSeconds,
            workload.ResourceLimits.MemoryMegabytes, workload.ResourceLimits.CpuPercent,
            workload.ResourceLimits.MaximumProcessCount, settings.MaximumRepositorySizeMb,
            Math.Max(1, workload.ResourceLimits.MaximumLogBytes / (1024 * 1024)));
        var progress = new PersistedAgentBuildProgressReporter(dbContext, job);
        var provenance = JsonSerializer.Serialize(new
        {
            request.RepositoryUrl,
            request.CommitSha,
            request.ProjectPath,
            request.BuildProfileId,
            workload.GuestImage.Digest,
            brokerProtocolVersion = workload.BrokerLease.ProtocolVersion
        });
        var artifact = new BuilderArtifactBrokerStreamHandler(
            new BuilderArtifactStreamGrant(
                workload.WorkloadId, request.PackageVersionId, "agent-artifact",
                workload.MaximumArtifactBytes, "1.0", "linux", workload.GuestImage.Architecture, provenance),
            artifacts,
            builderPublisher,
            Path.Combine(artifactOptions.ValidatedRootPath(), ".builder-streams"));
        await using (artifact)
        {
            var operations = new BuilderBrokerOperationHandler(
                workload, request, artifact, progress, logger);
            var grant = new AgentBrokerGrant(
                workload.WorkloadId,
                workload.BrokerLease.ChannelId,
                request.PackageVersionId,
                workload.GuestImage.Digest,
                null,
                workload.BrokerLease.ProtocolVersion,
                workload.BrokerLease.BootToken,
                workload.BrokerLease.ExpiresAt,
                new HashSet<string>(StringComparer.Ordinal)
                    { "build.fetch", "build.artifact", "build.progress" },
                100_000, 1024 * 1024, 1024 * 1024, 16 * 1024 * 1024);
            var boot = new GuestBootConfiguration
            {
                WorkloadId = workload.WorkloadId.ToString("D"),
                ChannelId = workload.BrokerLease.ChannelId.ToString("D"),
                ProtocolVersion = workload.BrokerLease.ProtocolVersion,
                GuestImageDigest = workload.GuestImage.Digest,
                BootToken = workload.BrokerLease.BootToken,
                LeaseExpiresAtUnixSeconds = workload.BrokerLease.ExpiresAt.ToUnixTimeSeconds(),
                ArtifactRoot = "/usr/lib/csweet/builder",
                WorkloadKind = (int)WorkloadKind.Builder,
                LocalBrokerSocketPath = "/run/csweet/broker.sock",
                WorkloadTokenPath = "/run/csweet/workload-token",
                MaximumFrameBytes = 16 * 1024 * 1024
            };
            var start = new StartCommand
            {
                WorkloadKind = (int)WorkloadKind.Builder,
                MaximumLogBytes = workload.ResourceLimits.MaximumLogBytes
            };
            start.Entrypoint.AddRange([
                "/usr/lib/csweet/builder/CSweet.SatelliteOffice.BuilderGuest",
                "--repository", request.RepositoryUrl,
                "--commit", request.CommitSha,
                "--project", request.ProjectPath,
                "--maximum-repository-bytes", checked(request.MaximumRepositorySizeMb * 1024L * 1024L).ToString(CultureInfo.InvariantCulture),
                "--maximum-artifact-bytes", workload.MaximumArtifactBytes.ToString(CultureInfo.InvariantCulture),
                "--broker-socket", "/run/csweet/broker.sock"
            ]);
            if (!string.IsNullOrWhiteSpace(request.TargetFramework))
                start.Entrypoint.AddRange(["--target-framework", request.TargetFramework]);
            var session = new GuestBrokerHostSession(
                grant, operations, timeProvider, bootConfiguration: boot, startCommand: start);
            var resultTask = builderResults.WaitAsync(workload.WorkloadId, cancellationToken);
            await session.RunAsync(stream, stream, cancellationToken);
            var result = await resultTask;
            assignment.ResultArtifactLocator = result.OpaqueLocator;
            assignment.ResultArtifactDigest = result.Artifact.Digest;
            assignment.ResultArtifactSignature = result.Artifact.Signature;
            assignment.ResultArtifactFormatVersion = result.Artifact.FormatVersion;
            assignment.ResultArtifactOperatingSystem = result.Artifact.OperatingSystem;
            assignment.ResultArtifactArchitecture = result.Artifact.Architecture;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
